using System;
using System.Reflection;
using Unity.XR.CoreUtils.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Genesis.RoomScan
{
    /// <summary>
    /// Captures stereo depth from the AR occlusion subsystem, computes world-space normals,
    /// runs optional bilateral filtering guided by the passthrough RGB feed, and produces
    /// dilated depth textures consumed by <see cref="VolumeIntegrator"/> for TSDF integration.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class DepthCapture : MonoBehaviour
    {
        public static DepthCapture Instance { get; private set; }

        [SerializeField] private ComputeShader depthNormalCompute;
        [SerializeField] private ComputeShader depthDilationCompute;
        [SerializeField] private ComputeShader bilateralFilterCompute;

        [Header("Bilateral Depth Filter")]
        [Tooltip("Edge-preserving depth denoising guided by passthrough RGB. Smooths flat surfaces while keeping object boundaries sharp.")]
        [SerializeField] private bool enableBilateralFilter = true;
        [SerializeField, Range(1f, 8f)] private float sigmaSpatial = 3.0f;
        [SerializeField, Range(0.01f, 0.5f)] private float sigmaColor = 0.1f;
        [SerializeField, Range(0.001f, 0.1f)] private float sigmaDepth = 0.02f;
        [SerializeField, Range(1, 5)] private int filterRadius = 2;

        [Header("Dilation")]
        [SerializeField] private int dilationSteps = 8;
        [SerializeField] private float voxelDistance = 0.2f;
        [SerializeField] private float voxelSize = 0.05f;

        [Header("Linear depth (Android XR)")]
        [Tooltip("Near plane (m) used to re-encode the Android XR provider's linear-metre depth into the NDC the depth shaders decode, and for the depth projection matrix. Depth closer than this is treated as invalid. The provider's own near/far (Camera.main clip planes) is ignored.")]
        [SerializeField, Min(0.01f)] private float linearDepthNear = 0.1f;
        [Tooltip("Far plane (m) for the same encoding. Kept finite so NDC 1 decodes to a real point instead of Inf/NaN. What happens to depth at or beyond it, and to +Inf ('very far'), is set by carveOnInfiniteDepth.")]
        [SerializeField, Min(0.5f)] private float linearDepthFar = 20f;
        [Tooltip("Flip the converted depth vertically. Off by default. The periodic 'orientation' log line (look down at the floor) says which value is right.")]
        [SerializeField] private bool flipLinearDepthY;
        [Tooltip("Treat +Inf and depth at or beyond the far plane as open space that carves the volume. Off by default: such pixels are invalid, like 0, which matches how Quest's infinite-far NDC 1 was rejected. Turn on only if the depth row log shows +inf solely where the room really is open.")]
        [SerializeField] private bool carveOnInfiniteDepth;

        [Header("Hand removal")]
        [Tooltip("Meta occlusion subsystem only: ask it to inpaint hands out of the depth texture. The Android XR occlusion provider has no hand removal, so this has no effect on Galaxy XR; capsule exclusion is the only hand filter there.")]
        [SerializeField] private bool removeHandsFromDepth = true;

        private readonly Matrix4x4[] _proj = new Matrix4x4[2];
        private readonly Matrix4x4[] _projInv = new Matrix4x4[2];
        private readonly Matrix4x4[] _view = new Matrix4x4[2];
        private readonly Matrix4x4[] _viewInv = new Matrix4x4[2];
        private Vector2 _planes;

        /// <summary>Per-eye projection matrices derived from the depth frame's FOV and near/far planes.</summary>
        public Matrix4x4[] Proj => _proj;
        /// <summary>Inverse projection matrices (per-eye).</summary>
        public Matrix4x4[] ProjInv => _projInv;
        /// <summary>Per-eye view matrices (tracking-space to depth-camera-space).</summary>
        public Matrix4x4[] View => _view;
        /// <summary>Inverse view matrices (per-eye), mapping depth-camera-space back to tracking-space.</summary>
        public Matrix4x4[] ViewInv => _viewInv;
        /// <summary>Near and far clip distances (x = near, y = far) for the current depth frame.</summary>
        public Vector2 Planes => _planes;

        // Shader property IDs
        public static readonly int DepthTexID = Shader.PropertyToID("gsDepthTex");
        public static readonly int DepthTexRWID = Shader.PropertyToID("gsDepthTexRW");
        public static readonly int TexSizeID = Shader.PropertyToID("gsDepthTexSize");
        public static readonly int NormTexID = Shader.PropertyToID("gsDepthNormalTex");
        public static readonly int NormTexRWID = Shader.PropertyToID("gsDepthNormalTexRW");
        public static readonly int ZParamsID = Shader.PropertyToID("gsDepthZParams");
        public static readonly int ProjID = Shader.PropertyToID("gsDepthProj");
        public static readonly int ProjInvID = Shader.PropertyToID("gsDepthProjInv");
        public static readonly int ViewID = Shader.PropertyToID("gsDepthView");
        public static readonly int ViewInvID = Shader.PropertyToID("gsDepthViewInv");
        public static readonly int InputRawMonoDepthID = Shader.PropertyToID("gsInputRawMonoDepth");
        public static readonly int DilateSrcID = Shader.PropertyToID("gsDilateSrc");
        public static readonly int DilateDestID = Shader.PropertyToID("gsDilateDest");
        public static readonly int DilateStepSizeID = Shader.PropertyToID("gsDilateStepSize");
        public static readonly int DilatedDepthTexID = Shader.PropertyToID("gsDilatedDepth");
        public static readonly int VoxDistID = Shader.PropertyToID("gsVoxDist");
        public static readonly int VoxSizeShaderID = Shader.PropertyToID("gsVoxSize");

        // Linear-depth (XR_LINEAR_DEPTH) conversion property IDs
        private static readonly int InputLinearDepthID = Shader.PropertyToID("gsInputLinearDepth");
        private static readonly int LinearDepthNdcParamsID = Shader.PropertyToID("gsLinearDepthNdcParams");
        private static readonly int LinearDepthInfoID = Shader.PropertyToID("gsLinearDepthInfo");
        private static readonly int LinearDepthInfo2ID = Shader.PropertyToID("gsLinearDepthInfo2");

        /// <summary>
        /// AR Foundation shader keyword a provider enables when its depth texture
        /// holds linear metres instead of NDC (the Android XR occlusion provider does).
        /// </summary>
        private const string LinearDepthKeyword = "XR_LINEAR_DEPTH";

        // Bilateral filter property IDs
        private static readonly int BilSrcDepthID = Shader.PropertyToID("_SrcDepth");
        private static readonly int BilRGBGuideID = Shader.PropertyToID("_RGBGuide");
        private static readonly int BilDstDepthID = Shader.PropertyToID("_DstDepth");
        private static readonly int BilDepthWID = Shader.PropertyToID("_DepthW");
        private static readonly int BilDepthHID = Shader.PropertyToID("_DepthH");
        private static readonly int BilSigmaSpatialID = Shader.PropertyToID("_SigmaSpatial");
        private static readonly int BilSigmaColorID = Shader.PropertyToID("_SigmaColor");
        private static readonly int BilSigmaDepthID = Shader.PropertyToID("_SigmaDepth");
        private static readonly int BilFilterRadiusID = Shader.PropertyToID("_FilterRadius");

        /// <summary>True once a valid depth frame has been received from the AR occlusion subsystem.</summary>
        public static bool DepthAvailable { get; private set; }

        /// <summary>
        /// Increments each time a depth frame with a new timestamp arrives. The
        /// Android XR provider re-delivers its last depth frame (same exposure
        /// timestamp) every render frame until a new one lands, so a consumer
        /// that must not fuse the same depth twice can compare this with the
        /// value it last used. Counts every frame when no timestamp is given.
        /// </summary>
        public int DepthFrameSerial => _newDepthFrameCount;

        /// <summary>
        /// True after the scene-understanding permission
        /// (<see cref="AndroidRuntimePermission.Scene"/>, SCENE_UNDERSTANDING_FINE on
        /// Android XR) is observed (requested by
        /// <see cref="RoomScanner.StartScanningAsync"/>, or earlier by the host via
        /// <see cref="RoomScanSession.RequestScenePermissionAsync"/>). Does not
        /// start the depth sensor. <see cref="StartDepthCapture"/> queues until
        /// this is set, then <see cref="ApplyCaptureState"/> enables hardware.
        /// </summary>
        private bool _permissionReady;

        /// <summary>
        /// Tracks whether the caller (RoomScanner) wants depth capture active.
        /// Persists across app pause/resume so the subsystem is re-enabled correctly.
        /// </summary>
        private bool _captureActive;

        private ComputeKernelHelper _normKernel;
        private ComputeKernelHelper _monoConvertKernel;
        private ComputeKernelHelper _linearToNdcKernel;
        private ComputeKernelHelper _initDilateKernel;
        private ComputeKernelHelper _dilateStepKernel;
        private ComputeKernelHelper _bilateralKernel;
        private bool _hasBilateralKernel;

        private Texture _depthTex;
        /// <summary>The current depth texture (raw or bilateral-filtered), as a stereo Tex2DArray.</summary>
        public Texture DepthTex => _depthTex;

        private RenderTexture _normTex;
        /// <summary>World-space normals computed from the depth texture via the DepthNorm compute shader.</summary>
        public RenderTexture NormTex => _normTex;

        private RenderTexture _dilationA, _dilationB;
        private RenderTexture _dilatedDepth;
        /// <summary>Depth texture after jump-flood dilation, used by the integrator to fill holes near voxel boundaries.</summary>
        public RenderTexture DilatedDepthTex => _dilatedDepth;

        private RenderTexture _simulatedDepthTex;
        private RenderTexture _filteredDepthTex;
        /// <summary>XR_LINEAR_DEPTH source re-encoded as NDC (R32_SFloat, 2 slices).</summary>
        private RenderTexture _linearDepthNdcTex;
        private int _dilationMaxStep;

        private Texture _rgbGuide;

        private AROcclusionManager _arOcclusionManager;
        private Unity.XR.CoreUtils.XROrigin _xrOrigin;
        private Transform _trackingSpaceTransform;
        private Camera _mainCam;
        private bool _started;
        private bool _dilationDirty;
        private int _frameCount;
        private float _lastLogTime;

        // Periodic-log window and new-frame (distinct timestamp) tracking.
        private int _newDepthFrameCount;
        private long _lastDepthTimestamp = long.MinValue;
        private int _logFrameCount;
        private int _logNewDepthFrameCount;

        /// <summary>How the provider's depth texture is encoded; logged once per change.</summary>
        private enum DepthEncoding { None, ProviderNdc, LinearMetres }
        private DepthEncoding _encoding;
        private DepthEncoding _loggedEncoding;
        private Texture _sourceDepthTex;
        private bool _loggedNoDepthTexture;
        private bool _loggedNoViews;
        private bool _loggedBadPlanes;
        private bool _loggedBadDimension;

        // One-row readback of the linear source for the periodic log.
        private bool _rowReadbackPending;
        private int _rowSampleY;
        private int _rowSampleHeight;
        private int _rowSampleWidth;
        private float _rowSamplePitch;

        private bool _handRemovalLogged;

        /// <summary>Raised after each depth frame is processed (filtering, normals computed, globals set).</summary>
        public event Action Updated;

        /// <summary>
        /// Provide an RGB texture as edge guide for bilateral depth filtering.
        /// Call each frame from RoomScanner with the passthrough camera frame.
        /// </summary>
        public void SetRGBGuide(Texture tex) => _rgbGuide = tex;

        private static readonly Vector3 ScaleFlipZ = new(1, 1, -1);

        /// <summary>
        /// Convert a pose from XR tracking space to Unity world space.
        /// Required because the XR Origin's camera-offset object (floor offset,
        /// recentering) may offset tracking space from the XROrigin root.
        /// </summary>
        public Pose TrackingToWorld(Pose trackingPose)
        {
            if (_trackingSpaceTransform == null) return trackingPose;
            return new Pose(
                _trackingSpaceTransform.TransformPoint(trackingPose.position),
                _trackingSpaceTransform.rotation * trackingPose.rotation);
        }

        private void Awake()
        {
            Instance = this;
            // All Awakes run before any OnEnable. Disable here so a scene-serialized
            // AROcclusionManager never starts the headset depth sensor at load.
            var occl = FindAnyObjectByType<AROcclusionManager>(FindObjectsInactive.Include);
            if (occl != null)
                occl.enabled = false;
        }

        private void Start()
        {
            // Editor + no XR loader = AR subsystems will all be null and any
            // toggle of AROcclusionManager.enabled blows up DestroyTextures.
            // Build and run on the headset to actually scan.
            if (!XRRuntimeGuard.IsXRActive)
            {
                Logger.Warning("DepthCapture: " + XRRuntimeGuard.EditorDisabledMessage);
                enabled = false;
                return;
            }

            EnsureARSession();

            _arOcclusionManager = FindAnyObjectByType<AROcclusionManager>(FindObjectsInactive.Include);
            if (!_arOcclusionManager)
                throw new Exception("[RoomScan] AROcclusionManager not found in scene");

            _xrOrigin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            CacheTrackingSpaceTransform();

            _normKernel = new ComputeKernelHelper(depthNormalCompute, "DepthNorm");
            _monoConvertKernel = new ComputeKernelHelper(depthNormalCompute, "MonoRawDepthToStereo");
            _linearToNdcKernel = new ComputeKernelHelper(depthNormalCompute, "LinearDepthToNDC");
            _initDilateKernel = new ComputeKernelHelper(depthDilationCompute, "InitDepthDilation");
            _dilateStepKernel = new ComputeKernelHelper(depthDilationCompute, "DilateDepthStep");
            if (bilateralFilterCompute != null)
            {
                _bilateralKernel = new ComputeKernelHelper(bilateralFilterCompute, "BilateralFilter");
                _hasBilateralKernel = true;
            }

            _dilationMaxStep = 1;
            for (int i = 0; i < dilationSteps; i++)
                _dilationMaxStep *= 2;

            _arOcclusionManager.enabled = false;
            CheckPermissionAndMarkReady();
            ApplyCaptureState();

            _started = true;
        }

        /// <summary>
        /// Resolves the tracking-space transform — the parent of the XR cameras
        /// that world-locking can reposition each frame. Using this instead of the
        /// XROrigin root ensures depth-to-world conversion includes that offset.
        /// </summary>
        private void CacheTrackingSpaceTransform()
        {
            // XROrigin.CameraFloorOffsetObject is the tracking-space parent
            // under OpenXR: it carries the floor offset the camera rides on.
            // Prefer it over the XROrigin root.
            if (_xrOrigin != null && _xrOrigin.CameraFloorOffsetObject != null)
            {
                _trackingSpaceTransform = _xrOrigin.CameraFloorOffsetObject.transform;
                Logger.Info($"DepthCapture: using XROrigin.CameraFloorOffsetObject '{_trackingSpaceTransform.name}'");
                return;
            }

            // Last resort: XROrigin root (pre-fix behaviour)
            _trackingSpaceTransform = _xrOrigin != null ? _xrOrigin.transform : null;
            Logger.Warning("DepthCapture: no TrackingSpace found, falling back to XROrigin root");
        }

        private void EnsureARSession()
        {
            if (FindAnyObjectByType<ARSession>() == null)
            {
                var go = new GameObject("[AR Session]");
                go.AddComponent<ARSession>();
                Logger.Info("Created ARSession (was missing from scene)");
            }
        }

        /// <summary>
        /// Observe the scene-understanding permission
        /// (<see cref="AndroidRuntimePermission.Scene"/>) without starting capture.
        /// Does <b>not</b> call <c>RequestUserPermission</c> — a second request
        /// while the host already has a dialog up is dropped by Android with no
        /// UI. The one requester is <c>AndroidRuntimePermission</c>, driven by
        /// <see cref="RoomScanner.StartScanningAsync"/> (and optionally the host at boot).
        /// </summary>
        private void CheckPermissionAndMarkReady()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(AndroidRuntimePermission.Scene))
            {
                MarkScenePermissionReady();
            }
            else if (!_permissionReady)
            {
                Logger.Info(
                    $"{AndroidRuntimePermission.Scene} not granted yet — waiting (RoomScanner requests it at scan start)");
            }
#else
            MarkScenePermissionReady();
#endif
        }

        private void MarkScenePermissionReady()
        {
            if (_permissionReady) return;
            _permissionReady = true;
            Logger.Info($"DepthCapture: {AndroidRuntimePermission.Scene} ready (sensor stays off until StartDepthCapture)");
        }

        private bool _subscribed;

        /// <summary>
        /// Enable the occlusion subsystem only while a scan is active and the
        /// scene-understanding permission is granted. Permission alone must not
        /// start the sensor.
        /// </summary>
        private void ApplyCaptureState()
        {
            if (_arOcclusionManager == null) return;

            bool want = _captureActive && _permissionReady;
            if (want)
            {
                if (!_arOcclusionManager.enabled)
                    _arOcclusionManager.enabled = true;
                if (!_subscribed)
                {
                    _arOcclusionManager.frameReceived += OnDepthFrame;
                    _subscribed = true;
                }
                TryEnableHandRemoval();
            }
            else
            {
                if (_subscribed)
                {
                    _arOcclusionManager.frameReceived -= OnDepthFrame;
                    _subscribed = false;
                }
                if (_arOcclusionManager.enabled)
                    _arOcclusionManager.enabled = false;
                DepthAvailable = false;
            }
        }

        /// <summary>
        /// Meta's occlusion subsystem can inpaint hands out of the depth
        /// texture (TSDF-safe); it is reached by type name +
        /// TrySetHandRemovalEnabled so there is no hard reference. The Android
        /// XR occlusion provider (Galaxy XR) has no hand removal, so there this
        /// returns at the type-name check and does nothing; hands are kept out
        /// of the volume only by RoomScanner's exclusion capsules.
        /// </summary>
        void TryEnableHandRemoval()
        {
            if (!removeHandsFromDepth || _arOcclusionManager == null) return;
            var subsystem = _arOcclusionManager.subsystem;
            if (subsystem == null) return;
            var type = subsystem.GetType();
            if (type.Name.IndexOf("MetaOpenXROcclusion", StringComparison.Ordinal) < 0)
                return;
            var method = type.GetMethod("TrySetHandRemovalEnabled", BindingFlags.Instance | BindingFlags.Public);
            if (method == null) return;
            try
            {
                var result = method.Invoke(subsystem, new object[] { true });
                if (!_handRemovalLogged)
                {
                    Logger.Info($"DepthCapture: Meta hand removal requested ({result})");
                    _handRemovalLogged = true;
                }
            }
            catch (Exception e)
            {
                if (!_handRemovalLogged)
                {
                    Logger.Warning($"DepthCapture: Meta hand removal request failed: {e.Message}");
                    _handRemovalLogged = true;
                }
            }
        }

        /// <summary>
        /// Enables the AROcclusionManager and subscribes to depth frames.
        /// Called by RoomScanner when scanning starts. If the scene-understanding
        /// permission is not yet granted, capture starts when it arrives.
        /// </summary>
        public void StartDepthCapture()
        {
            _captureActive = true;
            ResetRateWindow();
            ApplyCaptureState();
            if (_permissionReady && _arOcclusionManager != null)
                Logger.Info("DepthCapture: subsystem started");
            else
                Logger.Info($"DepthCapture: queued until {AndroidRuntimePermission.Scene} is granted");
        }

        /// <summary>
        /// Unsubscribes from depth frames and disables the AROcclusionManager,
        /// stopping the headset depth sensor. Called by RoomScanner when
        /// scanning stops.
        /// </summary>
        public void StopDepthCapture()
        {
            _captureActive = false;
            ApplyCaptureState();
            Logger.Info("DepthCapture: subsystem stopped");
        }

        private void OnApplicationPause(bool paused)
        {
            if (!_started) return;

            if (paused)
            {
                if (_arOcclusionManager != null)
                {
                    _arOcclusionManager.frameReceived -= OnDepthFrame;
                    _arOcclusionManager.enabled = false;
                    _subscribed = false;
                }
                DepthAvailable = false;
            }
            else
            {
                CheckPermissionAndMarkReady();
                ApplyCaptureState();
            }
        }

        private void OnDisable()
        {
            if (_arOcclusionManager == null) return;
            if (_subscribed)
            {
                _arOcclusionManager.frameReceived -= OnDepthFrame;
                _subscribed = false;
            }
            _arOcclusionManager.enabled = false;
            DepthAvailable = false;
        }

        private void OnDestroy()
        {
            ReleaseResources();
        }

        /// <summary>
        /// Destroys GPU textures (normals, dilation, filtered depth) to free memory.
        /// Textures are lazily recreated when the next depth frame arrives.
        /// </summary>
        public void ReleaseResources()
        {
            if (_normTex) { Destroy(_normTex); _normTex = null; }
            if (_dilationA) { Destroy(_dilationA); _dilationA = null; }
            if (_dilationB) { Destroy(_dilationB); _dilationB = null; }
            if (_simulatedDepthTex) { Destroy(_simulatedDepthTex); _simulatedDepthTex = null; }
            if (_filteredDepthTex) { Destroy(_filteredDepthTex); _filteredDepthTex = null; }
            if (_linearDepthNdcTex) { Destroy(_linearDepthNdcTex); _linearDepthNdcTex = null; }
            _dilatedDepth = null;
            Logger.Info("DepthCapture: GPU resources released");
        }

        private void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_started && !_permissionReady &&
                Permission.HasUserAuthorizedPermission(AndroidRuntimePermission.Scene))
            {
                MarkScenePermissionReady();
                ApplyCaptureState();
            }
#endif
            if (!_captureActive) return;
            float t = Time.unscaledTime;
            float window = t - _lastLogTime;
            if (window >= 5f)
            {
                // events/s counts every frameReceived; newDepth/s counts distinct
                // depth timestamps (Android XR re-delivers the last frame every
                // render frame, so the second number is the real sensor rate).
                float eventRate = (_frameCount - _logFrameCount) / window;
                float newRate = (_newDepthFrameCount - _logNewDepthFrameCount) / window;
                ResetRateWindow();

                var sub = _arOcclusionManager != null ? _arOcclusionManager.subsystem : null;
                var src = _sourceDepthTex;
                string srcInfo = src != null
                    ? $"{src.width}x{src.height}x{SliceCount(src)} {src.graphicsFormat}"
                    : "none";
                Logger.Info($"DepthCapture: path={_encoding}, src={srcInfo}, events={eventRate:F1}/s, " +
                          $"newDepth={newRate:F1}/s, frames={_frameCount}, depthAvail={DepthAvailable}, " +
                          $"occMgr.enabled={_arOcclusionManager?.enabled}, sub={sub?.GetType().Name ?? "null"}, " +
                          $"running={sub?.running}");

                if (_encoding == DepthEncoding.LinearMetres && src != null)
                    RequestDepthRowSample(src);
            }
        }

        private void ResetRateWindow()
        {
            _lastLogTime = Time.unscaledTime;
            _logFrameCount = _frameCount;
            _logNewDepthFrameCount = _newDepthFrameCount;
        }

        private void OnDepthFrame(AROcclusionFrameEventArgs args)
        {
            _frameCount++;
            if (_frameCount <= 3 || _frameCount % 100 == 0)
                Logger.Info($"OnDepthFrame #{_frameCount}, textures={args.externalTextures?.Count ?? 0}");

            if (!args.TryGetTimestamp(out long timestamp) || timestamp != _lastDepthTimestamp)
            {
                _newDepthFrameCount++;
                _lastDepthTimestamp = timestamp;
            }

            if (Application.isEditor)
                HandleEditorSimulation(args);
            else
                HandleDeviceDepth(args);

            if (!DepthAvailable) return;

            ApplyBilateralFilter();
            SetGlobalShaderProperties();
            ComputeNormals();
            _dilationDirty = true;

            Updated?.Invoke();
        }

        /// <summary>
        /// Run dilation if depth has been updated since last call.
        /// Called by VolumeIntegrator before integration (not every frame).
        /// </summary>
        public void UpdateDilationIfNeeded()
        {
            if (!_dilationDirty || !DepthAvailable) return;
            ComputeDilation();
            _dilationDirty = false;
        }

        private void HandleEditorSimulation(AROcclusionFrameEventArgs args)
        {
            Texture rawDepth = FirstTexture(args);
            DepthAvailable = rawDepth != null;
            if (!DepthAvailable) return;

            if (_simulatedDepthTex == null ||
                _simulatedDepthTex.width != rawDepth.width ||
                _simulatedDepthTex.height != rawDepth.height)
            {
                if (_simulatedDepthTex) Destroy(_simulatedDepthTex);
                _simulatedDepthTex = new RenderTexture(rawDepth.width, rawDepth.height, 0,
                    GraphicsFormat.R16_UNorm, 1)
                {
                    dimension = TextureDimension.Tex2DArray,
                    volumeDepth = 2,
                    enableRandomWrite = true
                };
            }

            _monoConvertKernel.Set(DepthTexRWID, _simulatedDepthTex);
            _monoConvertKernel.Set(InputRawMonoDepthID, rawDepth);
            _monoConvertKernel.DispatchFit(rawDepth.width, rawDepth.height);
            _depthTex = _simulatedDepthTex;

            if (!_mainCam) _mainCam = Camera.main;
            if (!_mainCam) return;

            Matrix4x4 p = _mainCam.projectionMatrix;
            Matrix4x4 pi = p.inverse;
            Transform ct = _mainCam.transform;
            Matrix4x4 vi = Matrix4x4.TRS(ct.position, ct.rotation, ScaleFlipZ);
            Matrix4x4 v = vi.inverse;

            for (int i = 0; i < 2; i++)
            {
                _proj[i] = p;
                _projInv[i] = pi;
                _view[i] = v;
                _viewInv[i] = vi;
            }

            _planes = new Vector2(_mainCam.nearClipPlane, _mainCam.farClipPlane);
        }

        private static Texture FirstTexture(AROcclusionFrameEventArgs args)
        {
            var textures = args.externalTextures;
            return textures != null && textures.Count > 0 ? textures[0].texture : null;
        }

        private static bool HasEnabledKeyword(XRShaderKeywords keywords, string keyword)
        {
            var enabled = keywords.enabledKeywords;
            if (enabled is null) return false;
            for (int i = 0; i < enabled.Count; i++)
                if (string.Equals(enabled[i], keyword, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static int SliceCount(Texture tex) => tex switch
        {
            RenderTexture rt => rt.dimension == TextureDimension.Tex2DArray ? rt.volumeDepth : 1,
            Texture2DArray array => array.depth,
            _ => 1
        };

        /// <summary>
        /// Near/far the fork encodes linear depth with. Chosen here rather than
        /// taken from the frame: the Android XR provider reports Camera.main's
        /// clip planes (0/0 when there is no MainCamera), which have nothing to
        /// do with the depth data. Far stays finite so NDC 1 decodes to a point.
        /// </summary>
        private XRNearFarPlanes LinearDepthPlanes()
        {
            float near = Mathf.Max(0.01f, linearDepthNear);
            float far = Mathf.Clamp(linearDepthFar, near + 0.1f, 1000f);
            return new XRNearFarPlanes(near, far);
        }

        private static bool ArePlanesUsable(XRNearFarPlanes planes) =>
            planes.nearZ > 0f && (float.IsPositiveInfinity(planes.farZ) || planes.farZ > planes.nearZ);

        private void HandleDeviceDepth(AROcclusionFrameEventArgs args)
        {
            DepthAvailable = false;

            Texture src = FirstTexture(args);
            _sourceDepthTex = src;
            if (src == null)
            {
                if (!_loggedNoDepthTexture)
                {
                    _loggedNoDepthTexture = true;
                    Logger.Warning($"DepthCapture: depth frame has no depth texture " +
                                   $"(externalTextures={args.externalTextures?.Count ?? 0}); waiting");
                }
                return;
            }

            if (!args.TryGetFovs(out ReadOnlyList<XRFov> fovs) || fovs == null || fovs.Count == 0 ||
                !args.TryGetPoses(out ReadOnlyList<Pose> poses) || poses == null || poses.Count == 0)
            {
                if (!_loggedNoViews)
                {
                    _loggedNoViews = true;
                    Logger.Warning("DepthCapture: depth frame has no per-eye FOVs/poses; waiting");
                }
                return;
            }

            args.TryGetNearFarPlanes(out XRNearFarPlanes providerPlanes);
            bool linear = HasEnabledKeyword(args.shaderKeywords, LinearDepthKeyword);
            _encoding = linear ? DepthEncoding.LinearMetres : DepthEncoding.ProviderNdc;

            // Linear metres (Android XR): encode with the fork's own planes so the
            // conversion and the projection below agree regardless of Camera.main.
            // Provider NDC: the texture was encoded with the provider's planes, so
            // only those decode it.
            XRNearFarPlanes depthPlanes = linear ? LinearDepthPlanes() : providerPlanes;
            if (!linear && !ArePlanesUsable(depthPlanes))
            {
                if (!_loggedBadPlanes)
                {
                    _loggedBadPlanes = true;
                    Logger.Warning($"DepthCapture: provider NDC depth with unusable near/far " +
                                   $"({depthPlanes.nearZ}, {depthPlanes.farZ}); skipping frames");
                }
                return;
            }

            if (_loggedEncoding != _encoding)
            {
                _loggedEncoding = _encoding;
                string srcInfo = $"{src.width}x{src.height}x{SliceCount(src)} {src.graphicsFormat} {src.dimension}";
                if (linear)
                    Logger.Info($"DepthCapture: depth path = {LinearDepthKeyword} linear metres -> NDC " +
                                $"(near={depthPlanes.nearZ:F2} m, far={depthPlanes.farZ:F1} m, flipY={flipLinearDepthY}; " +
                                $"provider near/far {providerPlanes.nearZ}/{providerPlanes.farZ} ignored), src={srcInfo}");
                else
                    Logger.Info($"DepthCapture: depth path = provider NDC (no {LinearDepthKeyword}), " +
                                $"near={depthPlanes.nearZ} far={depthPlanes.farZ}, src={srcInfo}");
            }

            for (int i = 0; i < 2; i++)
            {
                // A provider with a single view feeds both eyes from it.
                _proj[i] = CalculateProjectionMatrix(fovs[Mathf.Min(i, fovs.Count - 1)], depthPlanes);
                _projInv[i] = Matrix4x4.Inverse(_proj[i]);

                Pose pose = poses[Mathf.Min(i, poses.Count - 1)];
                Matrix4x4 depthFrameMat = Matrix4x4.TRS(pose.position, pose.rotation, ScaleFlipZ);

                Matrix4x4 worldToTracking = _trackingSpaceTransform != null
                    ? _trackingSpaceTransform.worldToLocalMatrix
                    : Matrix4x4.identity;

                _view[i] = depthFrameMat.inverse * worldToTracking;
                _viewInv[i] = Matrix4x4.Inverse(_view[i]);
            }

            if (linear)
            {
                if (!ConvertLinearDepth(src, depthPlanes)) return;
            }
            else
            {
                _depthTex = src;
            }

            _planes = new Vector2(depthPlanes.nearZ, depthPlanes.farZ);
            DepthAvailable = true;
        }

        /// <summary>
        /// Re-encodes the provider's linear-metre Tex2DArray into NDC (R32_SFloat,
        /// same size, 2 slices) with the LinearDepthToNDC kernel. Runs before the
        /// bilateral filter so its R16_UNorm output and every decoder downstream
        /// only ever see NDC. The encode uses _proj[0]'s m22/m23, so it is the
        /// exact inverse of gsDepthNDCToLinear for this frame.
        /// </summary>
        private bool ConvertLinearDepth(Texture src, XRNearFarPlanes planes)
        {
            if (src.dimension != TextureDimension.Tex2DArray)
            {
                if (!_loggedBadDimension)
                {
                    _loggedBadDimension = true;
                    Logger.Error($"DepthCapture: linear depth texture is {src.dimension}, expected Tex2DArray; " +
                                 "depth disabled");
                }
                return false;
            }

            int w = src.width;
            int h = src.height;
            if (_linearDepthNdcTex == null || _linearDepthNdcTex.width != w || _linearDepthNdcTex.height != h)
            {
                if (_linearDepthNdcTex) Destroy(_linearDepthNdcTex);
                _linearDepthNdcTex = new RenderTexture(w, h, 0, GraphicsFormat.R32_SFloat, 1)
                {
                    name = "RoomScan Linear Depth NDC",
                    dimension = TextureDimension.Tex2DArray,
                    volumeDepth = 2,
                    enableRandomWrite = true,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                _linearDepthNdcTex.Create();
            }

            var cs = depthNormalCompute;
            cs.SetVector(LinearDepthNdcParamsID,
                new Vector4(_proj[0].m22, _proj[0].m23, planes.nearZ, planes.farZ));
            cs.SetVector(LinearDepthInfoID,
                new Vector4(w, h, SliceCount(src), flipLinearDepthY ? 1f : 0f));
            cs.SetVector(LinearDepthInfo2ID, new Vector4(carveOnInfiniteDepth ? 1f : 0f, 0f, 0f, 0f));
            _linearToNdcKernel.Set(InputLinearDepthID, src);
            _linearToNdcKernel.Set(DepthTexRWID, _linearDepthNdcTex);
            _linearToNdcKernel.DispatchFit(w, h, 2);

            _depthTex = _linearDepthNdcTex;
            return true;
        }

        /// <summary>
        /// Reads back eye 0 of the linear source (asynchronously, every ~5 s) and
        /// logs the middle row's valid range in metres plus an orientation probe:
        /// the mean depth of the lowest and highest quarter of rows, with the head
        /// pitch at request time. Looking down at a floor, the bottom of the view
        /// is nearer, so this settles <see cref="flipLinearDepthY"/> from one log.
        /// </summary>
        private void RequestDepthRowSample(Texture src)
        {
            if (_rowReadbackPending || !SystemInfo.supportsAsyncGPUReadback) return;
            _rowSampleY = src.height / 2;
            _rowSampleHeight = src.height;
            _rowSampleWidth = src.width;
            _rowSamplePitch = 0f;
            var cam = _mainCam ? _mainCam : Camera.main;
            if (cam)
            {
                // Positive = looking down.
                float x = cam.transform.eulerAngles.x;
                _rowSamplePitch = x > 180f ? x - 360f : x;
            }
            _rowReadbackPending = true;
            AsyncGPUReadback.Request(src, 0, 0, src.width, 0, src.height, 0, 1, OnDepthRowReadback);
        }

        private void OnDepthRowReadback(AsyncGPUReadbackRequest request)
        {
            _rowReadbackPending = false;
            if (this == null || request.hasError) return;

            var data = request.GetData<float>();
            int w = _rowSampleWidth, h = _rowSampleHeight;
            if (w <= 0 || h <= 0 || data.Length < w * h) return;

            int valid = 0, zero = 0, posInf = 0, other = 0;
            float min = float.PositiveInfinity, max = 0f;
            int rowStart = _rowSampleY * w;
            for (int i = rowStart; i < rowStart + w; i++)
            {
                float m = data[i];
                if (m == 0f) zero++;
                else if (float.IsPositiveInfinity(m)) posInf++;
                else if (float.IsNaN(m) || m < 0f) other++;
                else
                {
                    valid++;
                    if (m < min) min = m;
                    if (m > max) max = m;
                }
            }

            string range = valid > 0 ? $"min={min:F2} m, max={max:F2} m" : "no valid pixels";
            Logger.Info($"DepthCapture: linear depth row {_rowSampleY}/{h} (eye 0): " +
                        $"valid={valid}/{w}, {range}, zero={zero}, +inf={posInf}, other={other}");

            // Orientation probe. Raw row 0 is what the pipeline treats as the
            // bottom of the view when flipLinearDepthY is off (uv v=0 -> NDC y=-1).
            int quarter = Mathf.Max(1, h / 4);
            float lowMean = MeanValidDepth(data, w, 0, quarter);
            float highMean = MeanValidDepth(data, w, h - quarter, h);
            string hint;
            if (_rowSamplePitch < 25f)
                hint = "look down at the floor (pitch > 25) for a flip suggestion";
            else if (float.IsNaN(lowMean) || float.IsNaN(highMean))
                hint = "not enough valid depth for a flip suggestion";
            else
            {
                // The true bottom of the view is nearer when looking at a floor.
                bool rawRow0IsBottom = lowMean < highMean;
                bool suggestFlip = !rawRow0IsBottom;
                hint = suggestFlip == flipLinearDepthY
                    ? $"current flipLinearDepthY={flipLinearDepthY} looks correct"
                    : $"set flipLinearDepthY={suggestFlip} (depth looks upside down)";
            }
            Logger.Info($"DepthCapture: orientation probe pitch={_rowSamplePitch:F0} deg, " +
                        $"raw rows 0..{quarter - 1} mean={lowMean:F2} m, rows {h - quarter}..{h - 1} mean={highMean:F2} m -> {hint}");
        }

        private static float MeanValidDepth(Unity.Collections.NativeArray<float> data, int w, int rowFrom, int rowTo)
        {
            double sum = 0;
            int n = 0;
            for (int y = rowFrom; y < rowTo; y++)
            for (int x = 0; x < w; x++)
            {
                float m = data[y * w + x];
                if (m > 0f && !float.IsInfinity(m) && !float.IsNaN(m))
                {
                    sum += m;
                    n++;
                }
            }
            return n > (rowTo - rowFrom) * w / 10 ? (float)(sum / n) : float.NaN;
        }

        private bool _loggedBilateralSkip;
        private void ApplyBilateralFilter()
        {
            if (!enableBilateralFilter || !_hasBilateralKernel || _rgbGuide == null || _depthTex == null)
            {
                if (!_loggedBilateralSkip && enableBilateralFilter && _hasBilateralKernel && _rgbGuide == null)
                {
                    _loggedBilateralSkip = true;
                    Logger.Info("Bilateral depth filter skipped — no RGB guide (camera unavailable). " +
                              "Depth will be noisier at edges.");
                }
                return;
            }

            int w = _depthTex.width;
            int h = _depthTex.height;

            if (_filteredDepthTex == null || _filteredDepthTex.width != w || _filteredDepthTex.height != h)
            {
                if (_filteredDepthTex) Destroy(_filteredDepthTex);
                _filteredDepthTex = new RenderTexture(w, h, 0, GraphicsFormat.R16_UNorm, 1)
                {
                    dimension = TextureDimension.Tex2DArray,
                    volumeDepth = 2,
                    enableRandomWrite = true,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                _filteredDepthTex.Create();
            }

            var cs = bilateralFilterCompute;
            _bilateralKernel.Set(BilSrcDepthID, _depthTex);
            _bilateralKernel.Set(BilRGBGuideID, _rgbGuide);
            _bilateralKernel.Set(BilDstDepthID, _filteredDepthTex);
            cs.SetInt(BilDepthWID, w);
            cs.SetInt(BilDepthHID, h);
            cs.SetFloat(BilSigmaSpatialID, sigmaSpatial);
            cs.SetFloat(BilSigmaColorID, sigmaColor);
            cs.SetFloat(BilSigmaDepthID, sigmaDepth);
            cs.SetInt(BilFilterRadiusID, filterRadius);

            _bilateralKernel.DispatchFit(w, h, 2);

            _depthTex = _filteredDepthTex;
        }

        private void SetGlobalShaderProperties()
        {
            Shader.SetGlobalMatrixArray(ProjID, _proj);
            Shader.SetGlobalMatrixArray(ProjInvID, _projInv);
            Shader.SetGlobalMatrixArray(ViewID, _view);
            Shader.SetGlobalMatrixArray(ViewInvID, _viewInv);
            Shader.SetGlobalVector(ZParamsID, _planes);
            Shader.SetGlobalVector(TexSizeID, new Vector2(_depthTex.width, _depthTex.height));
            Shader.SetGlobalTexture(DepthTexID, _depthTex);
        }

        private void ComputeNormals()
        {
            if (_normTex == null || _normTex.width != _depthTex.width || _normTex.height != _depthTex.height)
            {
                if (_normTex) Destroy(_normTex);
                _normTex = new RenderTexture(_depthTex.width, _depthTex.height, 0,
                    GraphicsFormat.R8G8B8A8_SNorm, 1)
                {
                    dimension = TextureDimension.Tex2DArray,
                    volumeDepth = 2,
                    useMipMap = false,
                    enableRandomWrite = true
                };
            }

            _normKernel.Set(DepthTexID, _depthTex);
            _normKernel.Set(NormTexRWID, _normTex);
            _normKernel.DispatchFit(_normTex);
            Shader.SetGlobalTexture(NormTexID, _normTex);
        }

        private void ComputeDilation()
        {
            if (_dilationA == null || _dilationA.width != _depthTex.width || _dilationA.height != _depthTex.height)
            {
                if (_dilationA) Destroy(_dilationA);
                if (_dilationB) Destroy(_dilationB);

                var desc = new RenderTextureDescriptor
                {
                    width = _depthTex.width,
                    height = _depthTex.height,
                    volumeDepth = 1,
                    dimension = TextureDimension.Tex2D,
                    autoGenerateMips = false,
                    enableRandomWrite = true,
                    graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    msaaSamples = 1
                };

                _dilationA = new RenderTexture(desc);
                _dilationB = new RenderTexture(desc);
            }

            depthDilationCompute.SetFloat(VoxDistID, voxelDistance);
            depthDilationCompute.SetFloat(VoxSizeShaderID, voxelSize);

            _initDilateKernel.Set(DepthTexID, _depthTex);
            _initDilateKernel.Set(DilateSrcID, _dilationA);
            _initDilateKernel.Set(DilateDestID, _dilationB);
            _initDilateKernel.DispatchFit(_dilationA.width, _dilationA.height);

            int stepSize = _dilationMaxStep;
            for (int i = 0; i < dilationSteps; i++)
            {
                _dilateStepKernel.Set(DilateSrcID, _dilationA);
                _dilateStepKernel.Set(DilateDestID, _dilationB);
                depthDilationCompute.SetInt(DilateStepSizeID, stepSize);
                _dilateStepKernel.DispatchFit(_dilationA.width, _dilationA.height);

                stepSize /= 2;
                (_dilationA, _dilationB) = (_dilationB, _dilationA);
            }

            _dilatedDepth = _dilationA;
            Shader.SetGlobalTexture(DilatedDepthTexID, _dilatedDepth);
        }

        private static Matrix4x4 CalculateProjectionMatrix(XRFov fov, XRNearFarPlanes planes)
        {
            // OpenXR's XrFovf is signed (left/down negative for a centred view),
            // which the raw tan() here used to rely on. AR Foundation's own
            // ARShaderOcclusion instead builds -|left|, |right|, -|down|, |up|,
            // so a provider may legally report magnitudes. Normalising the signs
            // gives the same frustum under either convention (it assumes each
            // depth view contains its optical axis, as ARShaderOcclusion does).
            float left = Mathf.Tan(-Mathf.Abs(fov.angleLeft));
            float right = Mathf.Tan(Mathf.Abs(fov.angleRight));
            float bottom = Mathf.Tan(-Mathf.Abs(fov.angleDown));
            float top = Mathf.Tan(Mathf.Abs(fov.angleUp));

            float near = planes.nearZ;
            float far = planes.farZ;

            float x = 2.0f / (right - left);
            float y = 2.0f / (top - bottom);
            float a = (right + left) / (right - left);
            float b = (top + bottom) / (top - bottom);

            float c, d;
            if (float.IsInfinity(far))
            {
                c = -1.0f;
                d = -2.0f * near;
            }
            else
            {
                c = -(far + near) / (far - near);
                d = -(2.0f * far * near) / (far - near);
            }

            return new Matrix4x4
            {
                m00 = x,  m01 = 0, m02 = a,  m03 = 0,
                m10 = 0,  m11 = y, m12 = b,  m13 = 0,
                m20 = 0,  m21 = 0, m22 = c,  m23 = d,
                m30 = 0,  m31 = 0, m32 = -1, m33 = 0
            };
        }

        /// <summary>
        /// Update voxel parameters used by dilation (called by VolumeIntegrator when its values change).
        /// </summary>
        public void SetVoxelParams(float voxDist, float voxSize)
        {
            voxelDistance = voxDist;
            voxelSize = voxSize;
        }
    }
}
