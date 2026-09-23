using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Camera provider backed by ARFoundation's <see cref="ARCameraManager"/>
    /// (Quest 3+ via the Meta OpenXR provider). Provides intrinsics, pose, and
    /// RGB frames from the headset cameras.
    ///
    /// <para>
    /// The manager is discovered <b>scene-wide</b> rather than per-GameObject:
    /// exactly one <see cref="ARCameraManager"/> drives the single native camera
    /// handle, and creating a second one leaves neither able to run. This
    /// provider therefore adopts whatever the XR rig already has and never adds
    /// its own.
    /// </para>
    /// Frame delivery starts only from <see cref="StartCapture"/> (scan start)
    /// and stops from <see cref="StopCapture"/>. HEADSET_CAMERA is requested by
    /// the host via <see cref="RequestCameraPermissionAsync"/> — that is
    /// independent of starting capture.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class PassthroughCameraProvider : MonoBehaviour, ICameraProvider, ICameraFrameTiming
    {
        /// <summary>The Horizon OS permission required for camera access on Quest 3+.</summary>
        public const string CameraPermissionId = AndroidRuntimePermission.Camera;

        [SerializeField] private Vector2Int requestedResolution = new(1280, 960);

        private ARCameraManager _cameraManager;
        private Texture _latestTexture;
        private long _latestTimestampNs;
        private int _latestFrame = -1;
        private bool _capturing;

        private void Awake() => AdoptCameraManager();

        /// <inheritdoc />
        public bool IsReady => _capturing && _latestTexture != null && _latestFrame == Time.frameCount;

        /// <inheritdoc />
        public bool IsPlaying =>
            _capturing && _cameraManager != null && _cameraManager.subsystem is { running: true };

        /// <inheritdoc />
        public Texture CurrentFrame => IsPlaying ? _latestTexture : null;

        /// <summary>
        /// World-space pose of the camera this frame.
        /// <para>
        /// This is the AR camera's pose, not a per-sensor extrinsic: the Meta
        /// OpenXR provider does not surface the individual passthrough sensor
        /// transform, so frames are treated as originating at the AR camera.
        /// </para>
        /// </summary>
        public Pose CameraPose
        {
            get
            {
                if (!IsPlaying) return Pose.identity;
                var t = _cameraManager.transform;
                return new Pose(t.position, t.rotation);
            }
        }

        /// <inheritdoc />
        public double FrameTimeSeconds =>
            IsPlaying && _latestTimestampNs > 0
                ? _latestTimestampNs * 1e-9d
                : Time.realtimeSinceStartupAsDouble;

        /// <inheritdoc />
        public Vector2 FocalLength =>
            TryGetIntrinsics(out var i) ? i.focalLength : Vector2.one;

        /// <inheritdoc />
        public Vector2 PrincipalPoint =>
            TryGetIntrinsics(out var i) ? i.principalPoint : Vector2.zero;

        /// <inheritdoc />
        public Vector2 SensorResolution =>
            TryGetIntrinsics(out var i)
                ? new Vector2(i.resolution.x, i.resolution.y)
                : new Vector2(requestedResolution.x, requestedResolution.y);

        /// <inheritdoc />
        public Vector2 CurrentResolution =>
            _latestTexture != null
                ? new Vector2(_latestTexture.width, _latestTexture.height)
                : SensorResolution;

        private bool TryGetIntrinsics(out XRCameraIntrinsics intrinsics)
        {
            if (IsPlaying && _cameraManager.TryGetIntrinsics(out intrinsics))
                return true;
            intrinsics = default;
            return false;
        }

        /// <summary>
        /// True when the user has granted the Horizon OS HEADSET_CAMERA
        /// permission. Always true outside Android device builds.
        /// </summary>
        public static bool HasCameraPermission => AndroidRuntimePermission.Has(CameraPermissionId);

        /// <summary>
        /// Requests the HEADSET_CAMERA permission and resolves once the user
        /// accepts, denies, or dismisses the system dialog. Always resolves
        /// <c>true</c> outside Android device builds (no permission to request).
        /// Resolves <c>true</c> immediately if already granted.
        /// </summary>
        public static Task<bool> RequestCameraPermissionAsync()
            => AndroidRuntimePermission.RequestAsync(CameraPermissionId);

        /// <inheritdoc />
        public void StartCapture()
        {
            // No permission request here: RoomScanner.StartScanningAsync asks
            // through AndroidRuntimePermission (serialised) before bring-up. A
            // bare request from this spot raced that queue.
            if (!AndroidRuntimePermission.Has(CameraPermissionId))
                Logger.Warning("HEADSET_CAMERA not granted — no camera frames; scanning depth-only.");

            AdoptCameraManager();
            if (_cameraManager == null || _capturing) return;

            _cameraManager.frameReceived += OnFrameReceived;
            _cameraManager.enabled = true;
            _capturing = true;
        }

        /// <inheritdoc />
        public void StopCapture()
        {
            if (!_capturing) return;
            _capturing = false;

            if (_cameraManager != null)
            {
                _cameraManager.frameReceived -= OnFrameReceived;
                _cameraManager.enabled = false;
            }

            _latestTexture = null;
            _latestFrame = -1;
            _latestTimestampNs = 0;
        }

        private void OnFrameReceived(ARCameraFrameEventArgs args)
        {
            if (args.textures != null && args.textures.Count > 0)
            {
                _latestTexture = args.textures[0];
                _latestFrame = Time.frameCount;
            }

            if (args.timestampNs.HasValue)
                _latestTimestampNs = args.timestampNs.Value;
        }

        private void AdoptCameraManager()
        {
            if (_cameraManager != null) return;

            _cameraManager = FindAnyObjectByType<ARCameraManager>(FindObjectsInactive.Include);
            if (_cameraManager == null)
            {
                // Deliberately not added here: ARCameraManager must sit on the
                // XROrigin's camera to produce correct poses, and a second one
                // would contend for the single native camera handle.
                Logger.Warning("PassthroughCameraProvider: no ARCameraManager in scene — " +
                               "add one to the XR Origin's camera to enable camera frames.");
            }
            else
            {
                Logger.Info($"PassthroughCameraProvider: adopted ARCameraManager on '{_cameraManager.gameObject.name}'.");
            }
        }

        private void OnDestroy() => StopCapture();
    }
}
