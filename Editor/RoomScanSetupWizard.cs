using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Genesis.RoomScan.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Unity.XR.CoreUtils;

namespace Genesis.RoomScan.Editor
{
    public partial class RoomScanSetupWizard : EditorWindow
    {
        double _lastRefresh;
        const double REFRESH_SEC = 0.8;
        Vector2 _scroll;

        // Cached scene state
        ARSession _arSession;
        AROcclusionManager _arOcclusion;
        GameObject _cameraRig;

        DepthCapture _depthCapture;
        VolumeIntegrator _volumeIntegrator;
        MeshExtractor _meshExtractor;
        RoomScanner _roomScanner;
        PassthroughCameraProvider _cameraProvider;
        ARCameraManager _arCameraManager;
        CameraDebugOverlay _cameraDebug;
        DepthDebugOverlay _depthDebug;
        TriplanarCache _triplanarCache;
        RoomScanPersistence _persistence;
        RoomScanSession _session;
        KeyframeCollector _keyframeCollector;
        DebugMenuController _debugMenu;
        RoomScanInputHandler _inputHandler;
        RoomAnchorManager _roomAnchor;
        EventSystem _eventSystem;
        XRUIInputModule _xrInputModule;
        VRDocumentRaycaster _vrRaycaster;
        ControllerRayDriver _rayDriver;
        PanelInputConfiguration _panelInputConfig;

        TextureRefinement _textureRefinement;

        bool _depthCaptureWired, _volumeWired, _meshMatWired, _triplanarWired, _computeShaderWired;
        bool _refinedShaderWired, _occlusionShaderWired, _atlasBakeComputeWired;
        bool _debugOverlayWired;
        bool _androidXRManifest;
        bool _questManifestLeftovers;
        bool _cleartextAllowed;
        bool _insecureHttpAllowed;
        bool _xrCameraConfigured;

        // Style
        static readonly Color COL_OK   = new(0.25f, 0.82f, 0.35f);
        static readonly Color COL_WARN = new(0.95f, 0.78f, 0.15f);
        static readonly Color COL_MISS = new(0.92f, 0.28f, 0.25f);
        static readonly Color COL_INFO = new(0.45f, 0.72f, 0.95f);
        static readonly Color COL_SECT = new(0.18f, 0.18f, 0.22f);

        const string PKG = "Packages/com.genesis.roomscan/Runtime/Shaders/";

        [MenuItem("RoomScan/Setup Scene")]
        static void Open()
        {
            var w = GetWindow<RoomScanSetupWizard>("Room Scan Setup");
            w.minSize = new Vector2(420, 480);
        }

        void OnEnable()  => Refresh();
        void OnFocus()   => Refresh();

        void Update()
        {
            if (EditorApplication.timeSinceStartup - _lastRefresh > REFRESH_SEC)
            {
                Refresh();
                Repaint();
            }
        }

        // =================================================================
        //  REFRESH
        // =================================================================

        void Refresh()
        {
            _lastRefresh = EditorApplication.timeSinceStartup;

            _arSession = FindAny<ARSession>();
            _arOcclusion = FindAny<AROcclusionManager>();

            // Camera rig = the XR Origin
            _cameraRig = null;
            var xrOrigin = FindAny<Unity.XR.CoreUtils.XROrigin>();
            if (xrOrigin != null)
                _cameraRig = xrOrigin.gameObject;

            _depthCapture = FindAny<DepthCapture>();
            _volumeIntegrator = FindAny<VolumeIntegrator>();
            _meshExtractor = FindAny<MeshExtractor>();
            _roomScanner = FindAny<RoomScanner>();
            _cameraProvider = FindAny<PassthroughCameraProvider>();
            _arCameraManager = FindAny<ARCameraManager>();
            _cameraDebug = FindAny<CameraDebugOverlay>();
            _depthDebug = FindAny<DepthDebugOverlay>();
            _triplanarCache = FindAny<TriplanarCache>();
            _persistence = FindAny<RoomScanPersistence>();
            _session = FindAny<RoomScanSession>();
            _keyframeCollector = FindAny<KeyframeCollector>();
            _debugMenu = FindAny<DebugMenuController>();
            _inputHandler = FindAny<RoomScanInputHandler>();
            _roomAnchor = FindAny<RoomAnchorManager>();
            _eventSystem = FindAny<EventSystem>();
            _xrInputModule = FindAny<XRUIInputModule>();
            _vrRaycaster = FindAny<VRDocumentRaycaster>();
            _rayDriver = FindAny<ControllerRayDriver>();
            _panelInputConfig = FindAny<PanelInputConfiguration>();

            _textureRefinement = _roomScanner != null ? _roomScanner.GetComponent<TextureRefinement>() : null;

            _depthCaptureWired = _depthCapture != null && AreFieldsAssigned(_depthCapture,
                "depthNormalCompute", "depthDilationCompute", "bilateralFilterCompute");
            _volumeWired = _volumeIntegrator != null && AreFieldsAssigned(_volumeIntegrator,
                "compute");
            _meshMatWired = _meshExtractor != null && AreFieldsAssigned(_meshExtractor,
                "scanMeshMaterial");
            _triplanarWired = _triplanarCache != null && AreFieldsAssigned(_triplanarCache,
                "bakeCompute");
            _computeShaderWired = _meshExtractor != null && AreFieldsAssigned(_meshExtractor,
                "surfaceNetsCompute");
            _refinedShaderWired = _textureRefinement != null && AreFieldsAssigned(_textureRefinement,
                "refinedMeshShader", "refinedMeshBackfaceShader");
            _occlusionShaderWired = _textureRefinement != null && AreFieldsAssigned(_textureRefinement,
                "occlusionMeshShader");
            _atlasBakeComputeWired = _textureRefinement != null && AreFieldsAssigned(_textureRefinement,
                "atlasBakeCompute");
            _debugOverlayWired = _roomScanner != null && AreFieldsAssigned(_roomScanner,
                "debugOverlayShader");
            RefreshGSplat();
            RefreshAIDetection();
            RefreshVRProject();

            RefreshURPState();

            _androidXRManifest = AndroidXRManifestLibHasPermissions();
            _questManifestLeftovers = MainManifestHasQuestEntries();
            _cleartextAllowed = AndroidXRManifestLibHasCleartext();
            _insecureHttpAllowed = PlayerSettings.insecureHttpOption != InsecureHttpOption.NotAllowed;
            _xrCameraConfigured = IsXRCameraConfigured(out _);
        }

        // Partial methods implemented in RoomScanSetupWizard.GSplat.cs when
        // HAS_GAUSSIAN_SPLATTING is defined; silent no-ops otherwise.
        partial void RefreshGSplat();
        partial void DrawGSplatOptionalStatus();
        partial void CheckGSplatAnyMissing(ref bool anyMissing);
        partial void DrawGSplatShaderStatus(ref bool needsFix);
        partial void WireGSplatComponents();
        partial void SetupGSplatIfAvailable(GameObject root);

        // Partial methods implemented in RoomScanSetupWizard.AIDetection.cs when
        // HAS_AI_INFERENCE is defined; silent no-ops otherwise.
        partial void RefreshAIDetection();
        partial void DrawAIDetectionOptionalStatus();
        partial void CheckAIDetectionAnyMissing(ref bool anyMissing);
        partial void DrawAIDetectionShaderStatus(ref bool needsFix);
        partial void WireAIDetectionComponents();
        partial void SetupAIDetectionIfAvailable(GameObject root);

        // Partial methods implemented in RoomScanSetupWizard.VRProject.cs.
        // Always present (no #if guard) because OpenXR + Android XR are core deps.
        partial void RefreshVRProject();
        partial void DrawVRProjectSection();

        // =================================================================
        //  GUI
        // =================================================================

        void OnGUI()
        {
            DrawHeader();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            GUILayout.Space(4);

            // Game-Ready Preset is the promoted, common workflow for game
            // developers — keep it at the top so it's the first thing seen.
            // Everything below is for inspection / piecemeal fixes / opt-in
            // modules / final "do absolutely everything" sweep.
            DrawGameReadyPreset();

            DrawPrerequisites();
            DrawProjectSettings();
            DrawComponents();
            DrawVRProjectSection();
            DrawShaderWiring();
            DrawNativePlugins();

            GUILayout.Space(12);
            DrawMasterButton();
            GUILayout.Space(8);

            EditorGUILayout.EndScrollView();
        }

        void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Room Scan Setup", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
                Refresh();
            EditorGUILayout.EndHorizontal();
        }

        // -- Prerequisites ------------------------------------------------

        void DrawPrerequisites()
        {
            BeginSection("PREREQUISITES");

            string urpLabel = _urpAssetCached != null
                ? $"URP pipeline asset wired ({_urpAssetCached.name})"
                : "URP pipeline asset wired";
            StatusRow(urpLabel, _urpConfigured);
            StatusRow("ARSession", _arSession != null);
            StatusRow("Camera Rig (XROrigin)", _cameraRig != null);
            StatusRow("AROcclusionManager", _arOcclusion != null);
            if (_cameraRig != null)
                StatusRow("XR camera (MainCamera tag, transparent clear, ARCameraManager on, Floor origin, no post-processing)",
                          _xrCameraConfigured);

            if (!_urpConfigured)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Setup URP (Android XR defaults)", GUILayout.Width(220)))
                {
                    EnsureURPSetup();
                    Refresh();
                }
                EditorGUILayout.EndHorizontal();
            }

            if (_arSession == null)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add ARSession", GUILayout.Width(200)))
                    FixARSession();
                EditorGUILayout.EndHorizontal();
            }

            if (_cameraRig == null)
            {
                EditorGUILayout.HelpBox(
                    "No XR Origin in the scene. Add one below (XR Origin > Camera Offset > Main Camera " +
                    "with a TrackedPoseDriver and ARCameraManager); the Game-Ready preset also does this.\n" +
                    "The wizard adds AROcclusionManager to its camera automatically.",
                    MessageType.Info);
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add XR Origin Rig", GUILayout.Width(200)))
                {
                    EnsureXRRig();
                    Refresh();
                }
                EditorGUILayout.EndHorizontal();
            }
            else if (!_xrCameraConfigured)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Configure XR Camera", GUILayout.Width(200)))
                {
                    ConfigureXRCameraForAndroidXR();
                    MarkDirty();
                    Refresh();
                }
                EditorGUILayout.EndHorizontal();
            }

            if (_cameraRig != null && _arOcclusion == null)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add AROcclusionManager", GUILayout.Width(200)))
                    FixAROcclusion();
                EditorGUILayout.EndHorizontal();
            }

            EndSection();
        }

        void FixARSession()
        {
            var go = FindByName("AR Session");
            if (go == null)
            {
                go = new GameObject("AR Session");
                Undo.RegisterCreatedObjectUndo(go, "Create AR Session");
            }

            if (go.GetComponent<ARSession>() == null)
                Undo.AddComponent<ARSession>(go);
            // Same pairing as the Android XR rig that has run on Galaxy XR.
            if (go.GetComponent<ARInputManager>() == null)
                Undo.AddComponent<ARInputManager>(go);

            MarkDirty();
            Refresh();
        }

        /// <summary>The XR Origin's camera, else the first camera under the rig.</summary>
        static Camera FindXRCamera()
        {
            var origin = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>(FindObjectsInactive.Include);
            if (origin == null) return null;
            if (origin.Camera != null) return origin.Camera;
            return origin.GetComponentInChildren<Camera>(true);
        }

        void FixAROcclusion()
        {
            if (_cameraRig == null) return;

            Camera cam = FindXRCamera();
            if (cam == null)
            {
                Debug.LogWarning("[RoomScan Setup] No Camera found under camera rig");
                return;
            }

            GameObject target = cam.gameObject;

            // Need ARCameraManager as well for AROcclusionManager to work.
            // Both throw a wall of "No active XRSubsystem" errors in Editor
            // play mode without an active XR loader (no headset attached).
            // On device they're fine. We previously tried to silence
            // the Editor errors with EditorPlayModeXRGuard but the AR
            // OnEnable order bug it relied on never reliably fired before
            // the AR components' own OnEnable, and the workaround
            // introduced its own NRE chain via AROcclusionManager.OnDisable
            // → DestroyTextures. Reverted; just live with the Editor errors
            // and build to device to actually test.
            if (target.GetComponent<ARCameraManager>() == null)
                Undo.AddComponent<ARCameraManager>(target);

            var occl = target.GetComponent<AROcclusionManager>();
            if (occl == null)
                occl = Undo.AddComponent<AROcclusionManager>(target);
            // Depth sensor starts only from DepthCapture.StartDepthCapture.
            occl.enabled = false;
            DisableIdleScanHardware();

            MarkDirty();
            Refresh();
        }

        /// <summary>
        /// Leave AROcclusionManager disabled in the scene: DepthCapture
        /// enables it for the scan window, once
        /// SCENE_UNDERSTANDING_FINE is granted (Android XR's occlusion
        /// provider only warns when it starts without that permission).
        /// ARCameraManager is the opposite on Android XR: enabling it is
        /// what turns passthrough on (it delivers no camera images), so it
        /// is kept enabled or the app renders over black outside a scan.
        /// </summary>
        void DisableIdleScanHardware()
        {
            foreach (var occl in Object.FindObjectsByType<AROcclusionManager>(FindObjectsInactive.Include))
            {
                if (occl == null || !occl.enabled) continue;
                occl.enabled = false;
                EditorUtility.SetDirty(occl);
            }
            foreach (var cam in Object.FindObjectsByType<ARCameraManager>(FindObjectsInactive.Include))
            {
                if (cam == null || cam.enabled) continue;
                cam.enabled = true;
                EditorUtility.SetDirty(cam);
            }
        }

        /// <summary>
        /// Ensures the XR Origin's camera has an <see cref="ARCameraManager"/>,
        /// which is where ARFoundation expects it. Added enabled: on Android
        /// XR it is the passthrough switch (Android XR: AR Camera feature).
        /// </summary>
        static void EnsureARCameraManager()
        {
            if (Object.FindAnyObjectByType<ARCameraManager>(FindObjectsInactive.Include) != null)
                return;

            var cam = FindXRCamera();
            if (cam == null) cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[RoomScan Setup] No XR Origin camera found — " +
                                 "cannot add ARCameraManager for passthrough.");
                return;
            }

            var mgr = Undo.AddComponent<ARCameraManager>(cam.gameObject);
            mgr.enabled = true;
        }

        /// <summary>
        /// True when the XR camera matches what Android XR needs: tagged
        /// MainCamera (the occlusion provider reads Camera.main's clip
        /// planes), SolidColor clear with alpha 0 (passthrough composites
        /// under it), both eyes, an enabled ARCameraManager on it (the
        /// passthrough switch), the XR Origin requesting Floor tracking, and
        /// URP post-processing off on the camera.
        /// </summary>
        internal static bool IsXRCameraConfigured(out string problem)
        {
            problem = null;
            var origin = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>(FindObjectsInactive.Include);
            var cam = FindXRCamera();
            if (origin == null || cam == null) { problem = "no XR Origin camera"; return false; }

            var issues = new List<string>();
            if (!cam.CompareTag("MainCamera")) issues.Add("tag != MainCamera");
            var arCam = cam.GetComponent<ARCameraManager>();
            if (arCam == null) issues.Add("no ARCameraManager (passthrough)");
            else if (!arCam.enabled) issues.Add("ARCameraManager disabled (passthrough off)");
            if (cam.clearFlags != CameraClearFlags.SolidColor) issues.Add($"clearFlags={cam.clearFlags}");
            if (cam.backgroundColor.a > 0.001f) issues.Add($"clear alpha={cam.backgroundColor.a:0.##}");
            if (cam.stereoTargetEye != StereoTargetEyeMask.Both) issues.Add($"stereoTargetEye={cam.stereoTargetEye}");
            if (origin.RequestedTrackingOriginMode != Unity.XR.CoreUtils.XROrigin.TrackingOriginMode.Floor)
                issues.Add($"trackingOrigin={origin.RequestedTrackingOriginMode}");
            var urp = cam.GetComponent<UniversalAdditionalCameraData>();
            if (urp != null && urp.renderPostProcessing) issues.Add("URP post-processing on");

            if (issues.Count == 0) return true;
            problem = string.Join(", ", issues);
            return false;
        }

        /// <summary>
        /// Applies <see cref="IsXRCameraConfigured"/>'s requirements to the
        /// XR Origin's camera. Floor tracking is what this package's depth
        /// and floor logic assume; if the headset rejects it, the fallback
        /// is NotSpecified with CameraYOffset 0 (what the earlier Galaxy XR
        /// project shipped).
        /// </summary>
        static void ConfigureXRCameraForAndroidXR()
        {
            var origin = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>(FindObjectsInactive.Include);
            var cam = FindXRCamera();
            if (origin == null || cam == null)
            {
                Debug.LogWarning("[RoomScan Setup] No XR Origin camera to configure.");
                return;
            }

            if (origin.Camera == null)
            {
                Undo.RecordObject(origin, "Assign XR Origin camera");
                origin.Camera = cam;
            }

            Undo.RecordObject(cam.gameObject, "Tag XR camera");
            if (!cam.CompareTag("MainCamera")) cam.gameObject.tag = "MainCamera";

            Undo.RecordObject(cam, "Configure XR camera for Android XR");
            cam.clearFlags = CameraClearFlags.SolidColor;
            var bg = cam.backgroundColor;
            cam.backgroundColor = new Color(bg.r, bg.g, bg.b, 0f);
            cam.stereoTargetEye = StereoTargetEyeMask.Both;
            EditorUtility.SetDirty(cam);

            // Passthrough on Android XR = an enabled ARCameraManager on the
            // XR camera (Android XR: AR Camera feature).
            var arCam = cam.GetComponent<ARCameraManager>();
            if (arCam == null) arCam = Undo.AddComponent<ARCameraManager>(cam.gameObject);
            if (!arCam.enabled)
            {
                Undo.RecordObject(arCam, "Enable ARCameraManager (passthrough)");
                arCam.enabled = true;
                EditorUtility.SetDirty(arCam);
            }

            if (origin.RequestedTrackingOriginMode != Unity.XR.CoreUtils.XROrigin.TrackingOriginMode.Floor)
            {
                Undo.RecordObject(origin, "XR Origin Floor tracking");
                origin.RequestedTrackingOriginMode = Unity.XR.CoreUtils.XROrigin.TrackingOriginMode.Floor;
                EditorUtility.SetDirty(origin);
            }

            var urp = cam.GetComponent<UniversalAdditionalCameraData>();
            if (urp == null) urp = Undo.AddComponent<UniversalAdditionalCameraData>(cam.gameObject);
            Undo.RecordObject(urp, "URP camera settings for Android XR");
            urp.renderPostProcessing = false;
            urp.allowXRRendering = true;
            EditorUtility.SetDirty(urp);
        }

        /// <summary>
        /// Ensures the scene has an XR Origin rig with a tracked camera, plus
        /// an <see cref="ARCameraManager"/> on it. Under OpenXR the rig is a
        /// plain XR Origin, and on Android XR passthrough comes from the
        /// Android XR: AR Camera feature via that ARCameraManager (an
        /// ARCameraBackground does nothing there). The camera is then
        /// brought in line with <see cref="ConfigureXRCameraForAndroidXR"/>.
        /// </summary>
        void EnsureXRRig()
        {
            var origin = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>(FindObjectsInactive.Include);
            if (origin == null)
            {
                var go = new GameObject("XR Origin");
                Undo.RegisterCreatedObjectUndo(go, "Create XR Origin");
                origin = Undo.AddComponent<Unity.XR.CoreUtils.XROrigin>(go);

                var offset = new GameObject("Camera Offset");
                Undo.RegisterCreatedObjectUndo(offset, "Create Camera Offset");
                offset.transform.SetParent(go.transform, false);
                origin.CameraFloorOffsetObject = offset;

                var camGo = new GameObject("Main Camera");
                Undo.RegisterCreatedObjectUndo(camGo, "Create XR Camera");
                camGo.transform.SetParent(offset.transform, false);
                camGo.tag = "MainCamera";

                var cam = Undo.AddComponent<Camera>(camGo);
                cam.clearFlags = CameraClearFlags.SolidColor;
                // Transparent clear so passthrough shows through.
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.nearClipPlane = 0.01f;
                // Drive the camera from the HMD pose. Bindings are set
                // explicitly so the rig tracks without depending on a
                // project-specific input action asset.
                var tpd = Undo.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>(camGo);
                tpd.positionInput = new InputActionProperty(
                    new InputAction("Position", binding: "<XRHMD>/centerEyePosition",
                                    expectedControlType: "Vector3"));
                tpd.rotationInput = new InputActionProperty(
                    new InputAction("Rotation", binding: "<XRHMD>/centerEyeRotation",
                                    expectedControlType: "Quaternion"));
                tpd.trackingStateInput = new InputActionProperty(
                    new InputAction("Tracking State", binding: "<XRHMD>/trackingState",
                                    expectedControlType: "Integer"));
                origin.Camera = cam;

                // Floor-relative so the rig matches the user's real floor.
                origin.RequestedTrackingOriginMode =
                    Unity.XR.CoreUtils.XROrigin.TrackingOriginMode.Floor;
            }

            EnsureARCameraManager();
            ConfigureXRCameraForAndroidXR();
            MarkDirty();
        }

        // -- Project Settings ---------------------------------------------

        // Android manifest additions for Galaxy XR.
        //
        // The Android XR OpenXR build step (AndroidXRManifest, an
        // IPostGenerateGradleAndroidProject in com.unity.xr.androidxr-openxr)
        // already writes every XR entry into the generated
        // unityLibrary/xrmanifest.androidlib, driven by the enabled features:
        //   * SCENE_UNDERSTANDING_COARSE (AR Camera / Anchor / Occlusion)
        //   * SCENE_UNDERSTANDING_FINE   (AR Occlusion: the scan's depth)
        //   * HAND_TRACKING              (Hand Tracking Subsystem)
        //   * EYE_TRACKING_*             (Foveated Rendering)
        //   * android.software.xr.api.openxr, the controller / hand-tracking
        //     uses-features, the OpenXR runtime-broker queries,
        //     libopenxr.google.so and the full-space activity start mode.
        // Declaring those again here would only duplicate them. What it does
        // not add is android.permission.CAMERA (world-facing RGB camera via
        // Camera2) and cleartext HTTP for the LAN Gaussian-splat server.
        // Those two live in a small Android library module (same layout as
        // the .androidlib that has shipped on Galaxy XR), so no custom main
        // manifest is needed. A custom main manifest left over from a Quest
        // setup is stripped of its Quest / Horizon entries.
        const string MANIFEST_PATH = "Assets/Plugins/Android/AndroidManifest.xml";

        const string ANDROIDLIB_DIR      = "Assets/Plugins/Android/RoomScanAndroidXR.androidlib";
        const string ANDROIDLIB_GRADLE   = ANDROIDLIB_DIR + "/build.gradle";
        const string ANDROIDLIB_MANIFEST = ANDROIDLIB_DIR + "/src/main/AndroidManifest.xml";
        const string ANDROIDLIB_NSC      = ANDROIDLIB_DIR + "/src/main/res/xml/network_security_config.xml";
        const string ANDROIDLIB_NAMESPACE = "com.genesis.roomscan.androidxr";

        // The previous (Quest) layout of the cleartext module.
        const string LEGACY_NSC_ANDROIDLIB_DIR = "Assets/Plugins/Android/NetworkSecurityConfig.androidlib";

        const string ANDROID_NS = "http://schemas.android.com/apk/res/android";

        static readonly string[] REQUIRED_PERMISSIONS =
        {
            "android.permission.CAMERA",
        };

        // Manifest entries that only mean something on Meta Horizon OS.
        static readonly string[] QUEST_ENTRY_PREFIXES =
        {
            "com.oculus.", "oculus.", "horizonos.", "com.meta.",
        };
        const string QUEST_HEADTRACKING_FEATURE = "android.hardware.vr.headtracking";
        const string HORIZONOS_NS = "http://schemas.horizonos/sdk";

        void DrawProjectSettings()
        {
            BeginSection("PROJECT SETTINGS");

            EditorGUILayout.HelpBox(
                "Scene-understanding, hand-tracking and OpenXR manifest entries are generated by the " +
                "Android XR build step from the enabled OpenXR features. This adds only what it does " +
                "not: android.permission.CAMERA and cleartext HTTP (LAN splat server), in " +
                ANDROIDLIB_DIR + ".",
                MessageType.Info);

            StatusRow("Android XR manifest additions (CAMERA permission)", _androidXRManifest);
            StatusRow("Cleartext HTTP for the LAN splat server", _cleartextAllowed);
            StatusRow("Player Settings: Allow HTTP", _insecureHttpAllowed);
            if (_questManifestLeftovers)
                StatusRow("Custom AndroidManifest.xml free of Quest / Horizon entries", false);

            if (!_androidXRManifest || !_cleartextAllowed || _questManifestLeftovers)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Apply Android XR Manifest Entries", GUILayout.Width(240)))
                {
                    EnsureAndroidXRManifest();
                    Refresh();
                }
                EditorGUILayout.EndHorizontal();
            }

            if (!_insecureHttpAllowed)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Allow HTTP in Player Settings", GUILayout.Width(200)))
                {
                    PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                    Debug.Log("[RoomScan Setup] Set Player Settings > insecureHttpOption to AlwaysAllowed");
                    Refresh();
                }
                EditorGUILayout.EndHorizontal();
            }

            EndSection();
        }

        static string ProjectPath(string assetRelative) =>
            Path.Combine(Application.dataPath, "..", assetRelative);

        static XDocument TryLoadXml(string assetRelative)
        {
            string full = ProjectPath(assetRelative);
            if (!File.Exists(full)) return null;
            try { return XDocument.Load(full); }
            catch { return null; }
        }

        /// <summary>
        /// True iff the library module exists (build.gradle + manifest) and
        /// its manifest declares every entry in REQUIRED_PERMISSIONS.
        /// </summary>
        static bool AndroidXRManifestLibHasPermissions()
        {
            if (!File.Exists(ProjectPath(ANDROIDLIB_GRADLE))) return false;
            var doc = TryLoadXml(ANDROIDLIB_MANIFEST);
            if (doc?.Root == null) return false;
            XNamespace android = ANDROID_NS;
            return REQUIRED_PERMISSIONS.All(p => doc.Root.Elements("uses-permission")
                .Any(e => e.Attribute(android + "name")?.Value == p));
        }

        static bool AndroidXRManifestLibHasCleartext()
        {
            if (!File.Exists(ProjectPath(ANDROIDLIB_NSC))) return false;
            var doc = TryLoadXml(ANDROIDLIB_MANIFEST);
            var app = doc?.Root?.Element("application");
            if (app == null) return false;
            XNamespace android = ANDROID_NS;
            return app.Attribute(android + "usesCleartextTraffic")?.Value == "true"
                && app.Attribute(android + "networkSecurityConfig")?.Value == "@xml/network_security_config";
        }

        static bool IsQuestManifestName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name == QUEST_HEADTRACKING_FEATURE) return true;
            foreach (var prefix in QUEST_ENTRY_PREFIXES)
                if (name.StartsWith(prefix, System.StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Every element in a custom main manifest that only exists for Meta
        /// Horizon OS: com.oculus.* / horizonos.* permissions, features,
        /// meta-data and intent categories, the VR head-tracking feature, and
        /// horizonos:* elements.
        /// </summary>
        static List<XElement> QuestManifestElements(XDocument doc)
        {
            var found = new List<XElement>();
            if (doc?.Root == null) return found;
            XNamespace android = ANDROID_NS;
            XNamespace horizonos = HORIZONOS_NS;
            foreach (var e in doc.Root.Descendants())
            {
                if (e.Name.Namespace == horizonos) { found.Add(e); continue; }
                string local = e.Name.LocalName;
                if (local != "uses-permission" && local != "uses-feature" && local != "meta-data"
                    && local != "category" && local != "uses-permission-sdk-23")
                    continue;
                if (IsQuestManifestName(e.Attribute(android + "name")?.Value))
                    found.Add(e);
            }
            return found;
        }

        static bool MainManifestHasQuestEntries()
        {
            var doc = TryLoadXml(MANIFEST_PATH);
            if (doc?.Root == null) return false;
            return QuestManifestElements(doc).Count > 0
                || doc.Root.Attribute(XNamespace.Xmlns + "horizonos") != null;
        }

        /// <summary>
        /// Removes Quest / Horizon-only entries from a custom main manifest,
        /// if the project has one. Never touches anything else in it.
        /// Returns the number of entries removed.
        /// </summary>
        static int StripQuestEntriesFromMainManifest()
        {
            string fullPath = ProjectPath(MANIFEST_PATH);
            var doc = TryLoadXml(MANIFEST_PATH);
            if (doc?.Root == null) return 0;

            try
            {
                XNamespace android = ANDROID_NS;
                var removed = new List<string>();
                foreach (var e in QuestManifestElements(doc))
                {
                    removed.Add($"{e.Name.LocalName}:{e.Attribute(android + "name")?.Value ?? e.Name.ToString()}");
                    e.Remove();
                }
                var nsAttr = doc.Root.Attribute(XNamespace.Xmlns + "horizonos");
                if (nsAttr != null)
                {
                    nsAttr.Remove();
                    removed.Add("xmlns:horizonos");
                }
                if (removed.Count == 0) return 0;

                doc.Save(fullPath);
                AssetDatabase.ImportAsset(MANIFEST_PATH);
                Debug.Log($"[RoomScan Setup] {MANIFEST_PATH}: removed {removed.Count} Quest / Horizon entr" +
                          $"{(removed.Count == 1 ? "y" : "ies")} → " + string.Join(", ", removed));
                return removed.Count;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RoomScan Setup] Failed to strip Quest entries from {MANIFEST_PATH}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Idempotent: creates / updates the RoomScanAndroidXR.androidlib
        /// module (CAMERA permission + cleartext HTTP via a network security
        /// config), and strips Quest / Horizon entries from a custom main
        /// manifest. Existing entries in the module manifest are kept.
        /// </summary>
        static void EnsureAndroidXRManifest()
        {
            try
            {
                var written = new List<string>();

                // build.gradle — mirrors the library module layout that has
                // built with Unity 6000.4 for Galaxy XR (AGP namespace, SDK
                // levels taken from Unity's gradle properties).
                string gradleFull = ProjectPath(ANDROIDLIB_GRADLE);
                if (!File.Exists(gradleFull))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(gradleFull));
                    File.WriteAllText(gradleFull,
                        "apply plugin: 'com.android.library'\n" +
                        "\n" +
                        "dependencies {\n" +
                        "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n" +
                        "}\n" +
                        "\n" +
                        "android {\n" +
                        "    namespace \"" + ANDROIDLIB_NAMESPACE + "\"\n" +
                        "    compileSdk getProperty(\"unity.compileSdkVersion\") as int\n" +
                        "    buildToolsVersion = getProperty(\"unity.buildToolsVersion\")\n" +
                        "\n" +
                        "    compileOptions {\n" +
                        "        sourceCompatibility JavaVersion.valueOf(getProperty(\"unity.javaCompatabilityVersion\"))\n" +
                        "        targetCompatibility JavaVersion.valueOf(getProperty(\"unity.javaCompatabilityVersion\"))\n" +
                        "    }\n" +
                        "\n" +
                        "    defaultConfig {\n" +
                        "        minSdk getProperty(\"unity.minSdkVersion\") as int\n" +
                        "        targetSdk getProperty(\"unity.targetSdkVersion\") as int\n" +
                        "    }\n" +
                        "}\n");
                    written.Add("build.gradle");
                }

                // Network security config: cleartext to any host, system CAs.
                string nscFull = ProjectPath(ANDROIDLIB_NSC);
                if (!File.Exists(nscFull))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(nscFull));
                    File.WriteAllText(nscFull,
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                        "<network-security-config>\n" +
                        "    <base-config cleartextTrafficPermitted=\"true\">\n" +
                        "        <trust-anchors>\n" +
                        "            <certificates src=\"system\" />\n" +
                        "        </trust-anchors>\n" +
                        "    </base-config>\n" +
                        "</network-security-config>\n");
                    written.Add("network_security_config.xml");
                }

                // Library manifest (merged into the app manifest by Gradle).
                string manifestFull = ProjectPath(ANDROIDLIB_MANIFEST);
                XNamespace android = ANDROID_NS;
                XDocument doc = TryLoadXml(ANDROIDLIB_MANIFEST);
                bool dirty = false;
                if (doc?.Root == null)
                {
                    doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
                        new XComment(" Generated by the Room Scan setup wizard (Galaxy XR / Android XR). " +
                                     "Scene-understanding and hand-tracking permissions come from the " +
                                     "Android XR build step, not from here. "),
                        new XElement("manifest", new XAttribute(XNamespace.Xmlns + "android", ANDROID_NS)));
                    dirty = true;
                }

                foreach (var p in REQUIRED_PERMISSIONS)
                {
                    bool exists = doc.Root.Elements("uses-permission")
                        .Any(e => e.Attribute(android + "name")?.Value == p);
                    if (exists) continue;
                    doc.Root.Add(new XElement("uses-permission", new XAttribute(android + "name", p)));
                    written.Add($"perm:{p}");
                    dirty = true;
                }

                // CAMERA implies a required camera for store filtering;
                // declare it optional (sideloaded builds ignore this).
                const string cameraFeature = "android.hardware.camera";
                if (!doc.Root.Elements("uses-feature").Any(e => e.Attribute(android + "name")?.Value == cameraFeature))
                {
                    doc.Root.Add(new XElement("uses-feature",
                        new XAttribute(android + "name", cameraFeature),
                        new XAttribute(android + "required", "false")));
                    written.Add($"feature:{cameraFeature}");
                    dirty = true;
                }

                var app = doc.Root.Element("application");
                if (app == null)
                {
                    app = new XElement("application");
                    doc.Root.Add(app);
                    dirty = true;
                }
                if (app.Attribute(android + "usesCleartextTraffic")?.Value != "true")
                {
                    app.SetAttributeValue(android + "usesCleartextTraffic", "true");
                    written.Add("usesCleartextTraffic");
                    dirty = true;
                }
                if (app.Attribute(android + "networkSecurityConfig")?.Value != "@xml/network_security_config")
                {
                    app.SetAttributeValue(android + "networkSecurityConfig", "@xml/network_security_config");
                    written.Add("networkSecurityConfig");
                    dirty = true;
                }

                if (dirty)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(manifestFull));
                    doc.Save(manifestFull);
                }

                if (written.Count > 0)
                {
                    AssetDatabase.Refresh();
                    Debug.Log($"[RoomScan Setup] {ANDROIDLIB_DIR}: wrote " + string.Join(", ", written));
                }

                if (AssetDatabase.IsValidFolder(LEGACY_NSC_ANDROIDLIB_DIR))
                    Debug.LogWarning($"[RoomScan Setup] {LEGACY_NSC_ANDROIDLIB_DIR} is the old cleartext module; " +
                                     $"{ANDROIDLIB_DIR} replaces it. Delete the old one if nothing else uses it.");

                StripQuestEntriesFromMainManifest();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RoomScan Setup] Failed to update Android manifest additions: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // -- Components ---------------------------------------------------

        void DrawComponents()
        {
            // ── Core (required) ──
            BeginSection("CORE COMPONENTS (Required)");

            StatusRow("RoomScanner", _roomScanner != null);
            StatusRow("DepthCapture", _depthCapture != null);
            StatusRow("VolumeIntegrator", _volumeIntegrator != null);
            StatusRow("MeshExtractor", _meshExtractor != null);
            StatusRow("RoomScanPersistence", _persistence != null);
            StatusRow("RoomAnchorManager (AR anchors)", _roomAnchor != null);

            bool coreMissing = _roomScanner == null || _depthCapture == null ||
                               _volumeIntegrator == null || _meshExtractor == null ||
                               _persistence == null || _roomAnchor == null;
            if (coreMissing)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add Core Components", GUILayout.Width(180)))
                    FixCoreComponents();
                EditorGUILayout.EndHorizontal();
            }

            EndSection();

            // ── Optional modules ──
            BeginSection("OPTIONAL MODULES");
            EditorGUILayout.HelpBox(
                "These are optional. Add them via the RoomScanner inspector's \"Add Module\" dropdown, or use \"Add All\" below.",
                MessageType.Info);

            StatusRowOptional("PassthroughCameraProvider", _cameraProvider != null);
            StatusRowOptional("ARCameraManager", _arCameraManager != null);
            StatusRowOptional("TriplanarCache", _triplanarCache != null);
            StatusRowOptional("KeyframeCollector", _keyframeCollector != null);
            DrawGSplatOptionalStatus();
            DrawAIDetectionOptionalStatus();
            StatusRowOptional("TextureRefinement", _roomScanner != null && _roomScanner.GetComponent<TextureRefinement>() != null);
            StatusRowOptional("RoomUnderstanding (scene-understanding stub)", _roomScanner != null && _roomScanner.GetComponent<RoomUnderstanding>() != null);
            StatusRowOptional("CameraDebugOverlay", _cameraDebug != null);
            StatusRowOptional("DepthDebugOverlay", _depthDebug != null);
            StatusRowOptional("RoomScanInputHandler", _inputHandler != null);
            StatusRowOptional("DebugMenuController (HUD)", _debugMenu != null);

            bool anyOptionalMissing = _cameraProvider == null || _triplanarCache == null ||
                                      _debugMenu == null ||
                                      _inputHandler == null;
            CheckGSplatAnyMissing(ref anyOptionalMissing);
            CheckAIDetectionAnyMissing(ref anyOptionalMissing);
            if (anyOptionalMissing)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add All Optional", GUILayout.Width(160)))
                    FixAllOptionalModules();
                EditorGUILayout.EndHorizontal();
            }

            EndSection();

            // Game-Ready Preset is rendered at the very top of the wizard
            // (see OnGUI) — it is the promoted workflow.

            // ── Debug Preset ──
            DrawDebugPreset();
        }

        GameObject FindOrCreateRoot()
        {
            GameObject root = null;
            if (_roomScanner != null)
                root = _roomScanner.gameObject;
            else if (_depthCapture != null)
                root = _depthCapture.gameObject;

            if (root == null)
            {
                root = FindByName("RoomScan");
                if (root == null)
                {
                    root = new GameObject("RoomScan");
                    Undo.RegisterCreatedObjectUndo(root, "Create RoomScan");
                }
            }

            EnsureRoomScanIdentityTransform(root);
            return root;
        }

        // The RoomScan GameObject MUST have an identity local transform.
        // RoomScanner spawns a child "RefinedMeshRenderer" whose mesh
        // vertices are pre-baked into world-space coordinates by
        // RoomScanPersistence.RelocateVertices, and RefinedMesh.shader
        // bypasses the object-to-world matrix (uses posWS directly). Unity
        // still uses localToWorldMatrix for frustum-cull bounds, so any
        // non-identity scale on this GameObject silently shrinks (or moves)
        // the culling box away from the actual geometry — the rendered mesh
        // then "disappears" from most viewing angles unless the camera
        // frustum happens to clip the displaced bounds. Reset defensively.
        static void EnsureRoomScanIdentityTransform(GameObject root)
        {
            if (root == null) return;
            var t = root.transform;

            bool wrongScale = t.localScale != Vector3.one;
            bool wrongPos = t.localPosition != Vector3.zero;
            bool wrongRot = t.localRotation != Quaternion.identity;
            if (!wrongScale && !wrongPos && !wrongRot) return;

            Undo.RecordObject(t, "Reset RoomScan transform to identity");
            t.localScale = Vector3.one;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            EditorUtility.SetDirty(root);

            Debug.LogWarning(
                $"[RoomScanSetupWizard] Reset '{root.name}' transform to identity " +
                $"(wrongScale={wrongScale}, wrongPos={wrongPos}, wrongRot={wrongRot}). " +
                "RoomScanner's refined-mesh renderer hosts pre-baked world-space " +
                "vertices and bypasses object-to-world in its shader; any non-identity " +
                "parent transform breaks frustum-cull bounds and the mesh disappears " +
                "from most viewing angles. Don't put world-space UI panels (UIDocument) " +
                "directly on RoomScan — keep them on their own child GameObject.");
        }

        void FixCoreComponents()
        {
            var root = FindOrCreateRoot();

            // Adding RoomScanner auto-adds [RequireComponent] core siblings:
            // DepthCapture, VolumeIntegrator, MeshExtractor,
            // RoomScanPersistence, RoomAnchorManager
            if (root.GetComponent<RoomScanner>() == null)
                Undo.AddComponent<RoomScanner>(root);

            // Wire shader/compute on newly added core components
            foreach (var c in root.GetComponents<Component>())
                WireComponent(c);

            MarkDirty();
            Refresh();
        }

        void FixAllOptionalModules()
        {
            var root = FindOrCreateRoot();

            // Ensure core exists first
            if (root.GetComponent<RoomScanner>() == null)
                Undo.AddComponent<RoomScanner>(root);

            // ARCameraManager must sit on the XR Origin's camera, so it is
            // not pulled in by RequireComponent on the scanner root. It will
            // log "no active XRSubsystem" in Editor play mode without an XR
            // loader; that is expected — build to device to test.
            EnsureARCameraManager();
            if (root.GetComponent<PassthroughCameraProvider>() == null)
                Undo.AddComponent<PassthroughCameraProvider>(root);

            if (root.GetComponent<TriplanarCache>() == null)
                Undo.AddComponent<TriplanarCache>(root);
            if (root.GetComponent<TextureRefinement>() == null)
                Undo.AddComponent<TextureRefinement>(root);
            if (root.GetComponent<RoomUnderstanding>() == null)
                Undo.AddComponent<RoomUnderstanding>(root);

            // Public game-dev facade — see comment in AddGameReadyComponentsToRoot.
            if (root.GetComponent<RoomScanSession>() == null)
                Undo.AddComponent<RoomScanSession>(root);

            SetupGSplatIfAvailable(root);
            SetupAIDetectionIfAvailable(root);

            // Optional components not covered by RequireComponent
            if (root.GetComponent<RoomScanInputHandler>() == null)
                Undo.AddComponent<RoomScanInputHandler>(root);

            // Debug overlays — disabled by default
            if (root.GetComponent<CameraDebugOverlay>() == null)
            {
                var c = Undo.AddComponent<CameraDebugOverlay>(root);
                c.enabled = false;
            }
            if (root.GetComponent<DepthDebugOverlay>() == null)
            {
                var c = Undo.AddComponent<DepthDebugOverlay>(root);
                c.enabled = false;
            }

            // DebugMenu lives on a child (needs UIDocument)
            if (FindAny<DebugMenuController>() == null)
            {
                var debugGo = new GameObject("DebugMenu");
                debugGo.transform.SetParent(root.transform);
                Undo.RegisterCreatedObjectUndo(debugGo, "Create DebugMenu");

                Undo.AddComponent<UIDocument>(debugGo);
                Undo.AddComponent<DebugMenuController>(debugGo);
            }

            // Always ensure UIDocument has its assets assigned
            EnsureDebugMenuAssets();

            // Wire all components (core + optional)
            foreach (var c in root.GetComponents<Component>())
                WireComponent(c);

            // EventSystem + VR controller UI input pipeline
            EnsureVRInputInfrastructure();

            DisableIdleScanHardware();

            MarkDirty();
            Refresh();
        }

        void FixComponents()
        {
            FixCoreComponents();
            FixAllOptionalModules();
        }

        // -- Game-Ready Preset ----------------------------------------------

        void DrawGameReadyPreset()
        {
            BeginSection("GAME-READY PRESET");
            EditorGUILayout.HelpBox(
                "One-click \"make this project actually buildable for Samsung Galaxy XR\":\n" +
                "  \u2022 Switch to the plain Android platform if needed (re-click after the reload)\n" +
                "  \u2022 URP pipeline + renderer at Assets/Settings/ with Android XR defaults (HDR off, post-processing off, 4x MSAA, single shadow cascade)\n" +
                "  \u2022 Android XR project prerequisites (OpenXR loader, Android XR features, IL2CPP / ARM64 / Vulkan, Run In Background \u2014 Outstanding tier)\n" +
                "  \u2022 Android manifest additions: CAMERA + cleartext HTTP + insecureHttpOption (scene / hand permissions come from the Android XR build step)\n" +
                "  \u2022 XR Origin rig (Floor), transparent MainCamera, ARCameraManager (passthrough)\n" +
                "  \u2022 AR Session + AROcclusionManager on the XR camera\n" +
                "  \u2022 Game-ready scene modules (scan \u2192 refine \u2192 release GPU \u2192 play)\n" +
                "  \u2022 Shader wiring + xatlas native plugin build (background)\n" +
                "Skips TriplanarCache, Gaussian Splat, and debug tools to keep the build lean.",
                MessageType.Info);

            // ── Scene-level state ──
            bool hasCameraManager = _arCameraManager != null;
            bool hasPCAProvider = _cameraProvider != null;
            bool hasRefinement = _textureRefinement != null;
            bool hasRoomUnderstanding = _roomScanner != null && _roomScanner.GetComponent<RoomUnderstanding>() != null;

            StatusRowOptional("ARCameraManager (camera RGB)", hasCameraManager);
            StatusRowOptional("PassthroughCameraProvider", hasPCAProvider);
            StatusRowOptional("TextureRefinement (atlas baking)", hasRefinement);
            StatusRowOptional("RoomUnderstanding (scene-understanding stub)", hasRoomUnderstanding);
            StatusRowOptional("RoomScanSession (game-dev async API: StartScanAsync / UnloadActiveScanAsync / FinalizeScanAsync / LoadAsync)",
                              _session != null);

            if (hasRefinement)
            {
                var so = new SerializedObject(_textureRefinement);
                var simplifyProp = so.FindProperty("postBakeSimplificationRatio");
                if (simplifyProp != null)
                {
                    float val = simplifyProp.floatValue;
                    bool configured = val < 1f;
                    StatusRowOptional($"Mesh simplification ({val:P0})", configured);
                }
            }

            // ── Project-level state (also fixed by this preset) ──
            GUILayout.Space(2);
            EditorGUILayout.LabelField("Project prerequisites", EditorStyles.miniLabel);
            bool buildTargetIsAndroid = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;
            bool platformIsPlainAndroid = IsActivePlatformPlainAndroid();
            StatusRowOptional($"Active platform = plain Android (current: {DescribeActivePlatform()})", platformIsPlainAndroid);
            StatusRowOptional("URP pipeline asset (Android XR: HDR off, post-processing off)", _urpConfigured);
            StatusRowOptional("AR Session + AROcclusionManager", _arSession != null && _arOcclusion != null);
            StatusRowOptional("XR camera (MainCamera, transparent clear, passthrough on, Floor)", _xrCameraConfigured);
            StatusRowOptional("Android manifest additions (CAMERA + cleartext, no Quest entries)",
                              _androidXRManifest && _cleartextAllowed && !_questManifestLeftovers);
            StatusRowOptional("Player Settings: Allow HTTP", _insecureHttpAllowed);
            StatusRowOptional($"Android XR project checks ({_vrOutstanding.Count} outstanding)", _vrOutstanding.Count == 0);
            StatusRowOptional("xatlas native plugins (Android + Editor)", _xatlasAndroid && _xatlasEditor);

            bool triplanarAttached = _triplanarCache != null;
            if (triplanarAttached)
            {
                var prev = GUI.color;
                GUI.color = COL_WARN;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(12);
                GUILayout.Label("\u26A0", EditorStyles.boldLabel, GUILayout.Width(18));
                GUI.color = prev;
                GUILayout.Label("TriplanarCache is attached (\u2212240 MB GPU if removed)", GUILayout.ExpandWidth(true));
                prev = GUI.color;
                GUI.color = COL_WARN;
                GUILayout.Label("Optional", EditorStyles.miniLabel, GUILayout.Width(60));
                GUI.color = prev;
                EditorGUILayout.EndHorizontal();
            }

            bool sceneMissing   = !hasCameraManager || !hasPCAProvider || !hasRefinement || !hasRoomUnderstanding
                                  || _session == null;
            bool projectMissing = !buildTargetIsAndroid
                                  || !platformIsPlainAndroid
                                  || !_urpConfigured
                                  || _arSession == null || _arOcclusion == null
                                  || !_xrCameraConfigured
                                  || !_androidXRManifest || !_cleartextAllowed || _questManifestLeftovers
                                  || !_insecureHttpAllowed
                                  || _vrOutstanding.Count > 0
                                  || !_xatlasAndroid || !_xatlasEditor;

            if (sceneMissing || projectMissing)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_gameReadyFixInProgress))
                {
                    if (GUILayout.Button("Apply Game-Ready Setup", GUILayout.Width(220)))
                        FixGameReadyModules();
                }
                EditorGUILayout.EndHorizontal();

                if (_gameReadyFixInProgress)
                    EditorGUILayout.HelpBox("Game-Ready setup in progress (Android XR project checks + scene)\u2026", MessageType.Info);
            }

            EndSection();
        }

        void DrawDebugPreset()
        {
            BeginSection("DEBUG PRESET");
            EditorGUILayout.HelpBox(
                "Development tools: debug HUD, input handler, camera/depth overlays, " +
                "and VR input pipeline for interacting with the debug menu. " +
                "Overlays are added disabled by default.",
                MessageType.Info);

            bool hasInput = _inputHandler != null;
            bool hasDebug = _debugMenu != null;
            bool hasCamOverlay = _cameraDebug != null;
            bool hasDepthOverlay = _depthDebug != null;

            StatusRowOptional("RoomScanInputHandler (VR controls)", hasInput);
            StatusRowOptional("DebugMenuController (HUD)", hasDebug);
            StatusRowOptional("CameraDebugOverlay (disabled)", hasCamOverlay);
            StatusRowOptional("DepthDebugOverlay (disabled)", hasDepthOverlay);

            GUILayout.Space(4);
            EditorGUILayout.LabelField("VR Input (for debug menu buttons)", EditorStyles.miniLabel);
            StatusRowOptional("EventSystem + XRUIInputModule", _eventSystem != null && _xrInputModule != null);
            StatusRowOptional("VRDocumentRaycaster (UI pointer)", _vrRaycaster != null);
            StatusRowOptional("ControllerRayDriver (laser + cursor)", _rayDriver != null);
            StatusRowOptional("PanelInputConfiguration", _panelInputConfig != null);

            bool debugMissing = !hasInput || !hasDebug || !hasCamOverlay || !hasDepthOverlay
                                || _eventSystem == null || _xrInputModule == null
                                || _vrRaycaster == null || _rayDriver == null
                                || _panelInputConfig == null;
            if (debugMissing)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add Debug Modules", GUILayout.Width(200)))
                    FixDebugModules();
                EditorGUILayout.EndHorizontal();
            }

            EndSection();
        }

        // Tracks whether either the Game-Ready preset or Setup Everything is
        // currently running, so the matching buttons can disable themselves and
        // we never re-enter the async orchestrator while it is mid-run.
        bool _gameReadyFixInProgress;

        async void FixGameReadyModules()
        {
            if (_gameReadyFixInProgress) return;
            _gameReadyFixInProgress = true;

            try
            {
                if (TrySwitchToAndroidBuildTarget("Game-Ready Setup")) return;

                EditorUtility.DisplayProgressBar("Game-Ready Setup",
                    "Auditing VR project settings\u2026", 0.05f);
                VRProjectBootstrap.Audit();

                // URP first — shaders fall back to magenta until the
                // pipeline asset exists and is wired into GraphicsSettings,
                // so any later step that touches a Material/Shader needs
                // this in place.
                EditorUtility.DisplayProgressBar("Game-Ready Setup",
                    "Ensuring URP pipeline + Android XR defaults\u2026", 0.10f);
                EnsureURPSetup();

                EditorUtility.DisplayProgressBar("Game-Ready Setup",
                    "Fixing Android XR prerequisites (XR Plug-in, OpenXR features\u2026)", 0.15f);
                await VRProjectBootstrap.FixAllAsync(CheckSeverity.Outstanding);

                // Idempotent: CAMERA + cleartext module, Quest entries
                // stripped from any custom main manifest.
                EditorUtility.DisplayProgressBar("Game-Ready Setup",
                    "Updating Android manifest additions + Player Settings\u2026", 0.50f);
                EnsureAndroidXRManifest();
                if (PlayerSettings.insecureHttpOption == InsecureHttpOption.NotAllowed)
                {
                    PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                    Debug.Log("[RoomScan Setup] Set Player Settings > insecureHttpOption to AlwaysAllowed");
                }

                // XR Origin rig + ARCameraManager. Done before AR session so
                // AROcclusionManager can latch onto the new rig camera.
                EditorUtility.DisplayProgressBar("Game-Ready Setup",
                    "Setting up XR Origin rig\u2026", 0.60f);
                EnsureXRRig();
                Refresh();

                EditorUtility.DisplayProgressBar("Game-Ready Setup",
                    "Setting up AR session + occlusion\u2026", 0.65f);
                if (_arSession == null) FixARSession();
                if (_cameraRig != null && _arOcclusion == null) FixAROcclusion();

                EditorUtility.DisplayProgressBar("Game-Ready Setup",
                    "Adding game-ready scene components\u2026", 0.80f);
                AddGameReadyComponentsToRoot();

                EditorUtility.DisplayProgressBar("Game-Ready Setup",
                    "Wiring shaders\u2026", 0.90f);
                FixShaderWiring();

                RefreshNativePlugins();
                bool xatlasMissing = !_xatlasAndroid || !_xatlasEditor;
                if (xatlasMissing)
                {
                    EditorUtility.DisplayProgressBar("Game-Ready Setup",
                        "Starting xatlas plugin build (background)\u2026", 0.97f);
                    BuildXAtlasPlugin();
                }

                Debug.Log("[RoomScan Setup] Game-Ready setup complete." +
                    (xatlasMissing ? " (xatlas build running in background)" : ""));
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RoomScan Setup] Game-Ready setup failed: {ex}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _gameReadyFixInProgress = false;
                MarkDirty();
                Refresh();
                Repaint();
            }
        }

        // Pure scene-component piece of the game-ready preset, callable
        // synchronously from the orchestrator above without owning Refresh().
        void AddGameReadyComponentsToRoot()
        {
            var root = FindOrCreateRoot();

            if (root.GetComponent<RoomScanner>() == null)
                Undo.AddComponent<RoomScanner>(root);

            // ARCameraManager sits on the XR camera (passthrough on
            // Android XR); PassthroughCameraProvider on the root is the
            // scanner's camera provider. Both, plus ARSession +
            // AROcclusionManager, spam errors in Editor play mode without
            // an XR loader; that's expected, build to device.
            EnsureARCameraManager();
            if (root.GetComponent<PassthroughCameraProvider>() == null)
                Undo.AddComponent<PassthroughCameraProvider>(root);

            if (root.GetComponent<TextureRefinement>() == null)
                Undo.AddComponent<TextureRefinement>(root);
            if (root.GetComponent<RoomUnderstanding>() == null)
                Undo.AddComponent<RoomUnderstanding>(root);

            // RoomScanSession: public game-dev facade (StartScanAsync / UnloadActiveScanAsync / FinalizeScanAsync /
            // LoadLatestAsync / HasSavedScan / ProgressUpdated). Without it,
            // game code that follows the documented public-API path cannot
            // find RoomScanSession.Instance and bails.
            if (root.GetComponent<RoomScanSession>() == null)
                Undo.AddComponent<RoomScanSession>(root);

            ApplyGameReadyScanDefaults(root);

            foreach (var c in root.GetComponents<Component>())
                WireComponent(c);

            DisableIdleScanHardware();
        }

        /// <summary>
        /// Stamp the game-proven scan knobs onto an existing RoomScan root
        /// (extract 8 Hz, throttled keyframes, 50% post-bake simplify).
        /// Confine-to-room is host art direction and is left alone.
        /// New components already get these from field defaults; this
        /// covers scenes serialized against older 30 Hz / burst-keyframe
        /// values.
        /// </summary>
        void ApplyGameReadyScanDefaults(GameObject root)
        {
            var scanner = root.GetComponent<RoomScanner>();
            if (scanner != null)
            {
                var so = new SerializedObject(scanner);
                var hz = so.FindProperty("meshExtractionHz");
                if (hz != null) hz.floatValue = 8f;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(scanner);
            }

            var kf = root.GetComponent<KeyframeCollector>();
            if (kf != null)
            {
                var so = new SerializedObject(kf);
                var move = so.FindProperty("moveThreshold");
                if (move != null) move.floatValue = 0.15f;
                var rot = so.FindProperty("rotateThresholdDeg");
                if (rot != null) rot.floatValue = 10f;
                var interval = so.FindProperty("minCaptureInterval");
                if (interval != null) interval.floatValue = 0.25f;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(kf);
            }

            var tr = root.GetComponent<TextureRefinement>();
            if (tr != null)
            {
                var so = new SerializedObject(tr);
                var simplifyProp = so.FindProperty("postBakeSimplificationRatio");
                if (simplifyProp != null && simplifyProp.floatValue >= 1f)
                {
                    simplifyProp.floatValue = 0.5f;
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(tr);
                    Debug.Log("[RoomScan Setup] Set postBakeSimplificationRatio to 0.5 for game-ready mesh");
                }
            }
        }

        /// <summary>
        /// If the active platform is not plain Android (a different target,
        /// a derived Android platform such as Android XR, or a custom build
        /// profile), switches to plain Android, which triggers a domain
        /// reload and aborts the current async pipeline, and returns true so
        /// the caller bails out cleanly. The user is told via dialog to
        /// re-click after the reload. Returns false (carry on) when already
        /// on plain Android, or when no switch could be issued; the platform
        /// row then stays red with instructions in the console.
        /// </summary>
        bool TrySwitchToAndroidBuildTarget(string flowName)
        {
            if (IsActivePlatformPlainAndroid())
                return false;

            EditorUtility.ClearProgressBar();

            // Galaxy XR builds use the plain Android platform with the OpenXR
            // Android XR features (RoomScanSetupWizard.BuildProfile.cs says
            // why the derived platforms are avoided).
            EditorUtility.DisplayDialog(flowName,
                "Active platform is " + DescribeActivePlatform() + ".\n\n" +
                "Switching to the plain Android platform (Samsung Galaxy XR) now — this " +
                "triggers a domain reload and aborts the rest of this run.\n\n" +
                "Click \"" + flowName + "\" again after Unity finishes reloading to " +
                "apply the remaining fixes.",
                "Switch and reload");

            if (!TryActivatePlainAndroidPlatform())
            {
                Debug.LogWarning("[RoomScan Setup] Could not switch to the plain Android platform. " +
                                 "Select Android in File > Build Profiles by hand; continuing with " +
                                 "the rest of " + flowName + ".");
                return false;
            }

            // Drop the in-progress flag — the domain reload will wipe state
            // anyway, but if for some reason it doesn't fire we don't want
            // to leave the wizard locked out forever.
            _gameReadyFixInProgress = false;
            return true;
        }

        void FixDebugModules()
        {
            var root = FindOrCreateRoot();

            if (root.GetComponent<RoomScanner>() == null)
                Undo.AddComponent<RoomScanner>(root);

            if (root.GetComponent<RoomScanInputHandler>() == null)
                Undo.AddComponent<RoomScanInputHandler>(root);

            if (root.GetComponent<CameraDebugOverlay>() == null)
            {
                var c = Undo.AddComponent<CameraDebugOverlay>(root);
                c.enabled = false;
            }
            if (root.GetComponent<DepthDebugOverlay>() == null)
            {
                var c = Undo.AddComponent<DepthDebugOverlay>(root);
                c.enabled = false;
            }

            if (FindAny<DebugMenuController>() == null)
            {
                var debugGo = new GameObject("DebugMenu");
                debugGo.transform.SetParent(root.transform);
                Undo.RegisterCreatedObjectUndo(debugGo, "Create DebugMenu");
                Undo.AddComponent<UIDocument>(debugGo);
                Undo.AddComponent<DebugMenuController>(debugGo);
            }
            EnsureDebugMenuAssets();

            foreach (var c in root.GetComponents<Component>())
                WireComponent(c);

            EnsureVRInputInfrastructure();

            MarkDirty();
            Refresh();
        }

        /// <summary>
        /// Static entry point for ensuring VR input infrastructure exists.
        /// Called by <see cref="RoomScannerEditor"/> when adding the Debug Menu module.
        /// </summary>
        internal static void EnsureVRInput()
        {
            // EventSystem
            var es = FindAny<EventSystem>();
            if (es == null)
            {
                var esGo = new GameObject("EventSystem");
                Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");
                es = Undo.AddComponent<EventSystem>(esGo);
            }

            if (es.GetComponent<XRUIInputModule>() == null)
            {
                var standalone = es.GetComponent<StandaloneInputModule>();
                if (standalone != null) Undo.DestroyObjectImmediate(standalone);
                Undo.AddComponent<XRUIInputModule>(es.gameObject);
            }

            if (es.GetComponent<PanelInputConfiguration>() == null)
            {
                var pic = Undo.AddComponent<PanelInputConfiguration>(es.gameObject);
                var so = new SerializedObject(pic);
                SetBool(so, "m_DefaultEventCameraIsMainCamera", true);
                SetBool(so, "m_AutoCreatePanelComponents", true);
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(pic);
            }

            if (es.GetComponent<VRDocumentRaycaster>() == null)
                Undo.AddComponent<VRDocumentRaycaster>(es.gameObject);
            var rayDriver = es.GetComponent<ControllerRayDriver>();
            if (rayDriver == null)
                rayDriver = Undo.AddComponent<ControllerRayDriver>(es.gameObject);
            WireComponent(rayDriver);
        }

        void EnsureVRInputInfrastructure() => EnsureVRInput();

        static void SetBool(SerializedObject so, string fieldName, bool value)
        {
            var prop = so.FindProperty(fieldName);
            if (prop != null) prop.boolValue = value;
        }

        // -- Shader / Material Wiring ------------------------------------

        void DrawShaderWiring()
        {
            BeginSection("SHADER & MATERIAL WIRING");

            bool needsFix = false;

            // Core — always present
            if (_depthCapture != null)   { StatusRow("DepthCapture compute shaders", _depthCaptureWired); needsFix |= !_depthCaptureWired; }
            if (_volumeIntegrator != null){ StatusRow("VolumeIntegrator compute shader", _volumeWired);    needsFix |= !_volumeWired; }
            if (_meshExtractor != null)  { StatusRow("MeshExtractor scan material", _meshMatWired);        needsFix |= !_meshMatWired; }
            if (_meshExtractor != null)  { StatusRow("SurfaceNetsExtract compute shader", _computeShaderWired); needsFix |= !_computeShaderWired; }

            // Optional — only show if the module is attached
            if (_triplanarCache != null) { StatusRow("TriplanarCache bake compute", _triplanarWired);      needsFix |= !_triplanarWired; }

            if (_textureRefinement != null)   { StatusRow("RefinedMesh shader (texture refine)", _refinedShaderWired); needsFix |= !_refinedShaderWired; }
            if (_textureRefinement != null)   { StatusRow("OcclusionMesh shader (MR occluder)", _occlusionShaderWired); needsFix |= !_occlusionShaderWired; }
            if (_textureRefinement != null)   { StatusRow("AtlasBakeCompute (GPU bake)", _atlasBakeComputeWired);      needsFix |= !_atlasBakeComputeWired; }
            if (_roomScanner != null)        { StatusRow("DebugOverlay shader (scene viz)", _debugOverlayWired);     needsFix |= !_debugOverlayWired; }
            DrawGSplatShaderStatus(ref needsFix);
            DrawAIDetectionShaderStatus(ref needsFix);

            if (needsFix)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Wire All Shaders", GUILayout.Width(160)))
                    FixShaderWiring();
                EditorGUILayout.EndHorizontal();
            }

            EndSection();
        }

        void FixShaderWiring()
        {
            WireComponent(_depthCapture);
            WireComponent(_volumeIntegrator);
            WireComponent(_meshExtractor);
            WireComponent(_triplanarCache);
            WireComponent(_depthDebug);
            WireComponent(_roomScanner);

            var tr = _roomScanner != null ? _roomScanner.GetComponent<TextureRefinement>() : null;
            WireComponent(tr);
            WireGSplatComponents();
            WireAIDetectionComponents();

            var rayDriver = FindAny<UI.ControllerRayDriver>();
            WireComponent(rayDriver);

            MarkDirty();
            Refresh();
        }

        static void AssignCompute(SerializedObject so, string fieldName, string assetPath)
        {
            AssignAsset<ComputeShader>(so, fieldName, assetPath);
        }

        static void AssignAsset<T>(SerializedObject so, string fieldName, string assetPath) where T : Object
        {
            var prop = so.FindProperty(fieldName);
            if (prop == null) return;
            if (prop.objectReferenceValue != null) return;

            var asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset != null)
                prop.objectReferenceValue = asset;
            else
                Debug.LogWarning($"[RoomScan Setup] Could not find {assetPath}");
        }

        static Material GetOrCreateScanMaterial()
        {
            const string pkgMatPath = "Packages/com.genesis.roomscan/Runtime/Materials/ScanMesh.mat";
            var pkgMat = AssetDatabase.LoadAssetAtPath<Material>(pkgMatPath);
            if (pkgMat != null) return pkgMat;

            // Fallback: create in project if package material not found
            const string matPath = "Assets/RoomScan/ScanMesh.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Genesis/ScanMeshVertexColor");
            if (shader == null)
            {
                Debug.LogWarning("[RoomScan Setup] Shader 'Genesis/ScanMeshVertexColor' not found");
                return null;
            }

            if (!AssetDatabase.IsValidFolder("Assets/RoomScan"))
                AssetDatabase.CreateFolder("Assets", "RoomScan");

            var mat = new Material(shader) { name = "ScanMesh", enableInstancing = true };
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.SaveAssets();
            return mat;
        }

        // -- Native Plugins -----------------------------------------------

        bool _xatlasAndroid, _xatlasEditor;

        void RefreshNativePlugins()
        {
            string pkgRoot = "Packages/com.genesis.roomscan/Runtime";
            _xatlasAndroid = System.IO.File.Exists(
                Path.GetFullPath(Path.Combine(pkgRoot, "Plugins/Android/libxatlas.so")));
#if UNITY_EDITOR_WIN
            _xatlasEditor = System.IO.File.Exists(
                Path.GetFullPath(Path.Combine(pkgRoot, "Plugins/Windows/xatlas.dll")));
#elif UNITY_EDITOR_LINUX
            _xatlasEditor = System.IO.File.Exists(
                Path.GetFullPath(Path.Combine(pkgRoot, "Plugins/Linux/libxatlas.so")));
#else
            _xatlasEditor = System.IO.File.Exists(
                Path.GetFullPath(Path.Combine(pkgRoot, "Plugins/macOS/libxatlas.bundle")));
#endif
        }

        void DrawNativePlugins()
        {
            RefreshNativePlugins();
            BeginSection("NATIVE PLUGINS");
            StatusRow("xatlas (Android ARM64)", _xatlasAndroid);
#if UNITY_EDITOR_WIN
            StatusRow("xatlas (Windows Editor)", _xatlasEditor);
#elif UNITY_EDITOR_LINUX
            StatusRow("xatlas (Linux Editor)", _xatlasEditor);
#else
            StatusRow("xatlas (macOS Editor)", _xatlasEditor);
#endif

            if (!_xatlasAndroid || !_xatlasEditor)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Build xatlas Plugin", GUILayout.Width(200)))
                    BuildXAtlasPlugin();
                EditorGUILayout.EndHorizontal();
            }
            EndSection();
        }

        /// <summary>
        /// Wires shader/compute/material references on a freshly added component.
        /// Called by both the setup wizard and the RoomScannerEditor "Add Module" dropdown.
        /// </summary>
        internal static void WireComponent(Component component)
        {
            if (component == null) return;

            const string PKG_SHADERS = "Packages/com.genesis.roomscan/Runtime/Shaders/";

            switch (component)
            {
                case DepthCapture dc:
                {
                    var so = new SerializedObject(dc);
                    AssignCompute(so, "depthNormalCompute", PKG_SHADERS + "DepthNormals.compute");
                    AssignCompute(so, "depthDilationCompute", PKG_SHADERS + "DepthDilation.compute");
                    AssignCompute(so, "bilateralFilterCompute", PKG_SHADERS + "BilateralDepthFilter.compute");
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(dc);
                    break;
                }
                case VolumeIntegrator vi:
                {
                    var so = new SerializedObject(vi);
                    AssignCompute(so, "compute", PKG_SHADERS + "VolumeIntegration.compute");
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(vi);
                    break;
                }
                case MeshExtractor me:
                {
                    var so = new SerializedObject(me);
                    var prop = so.FindProperty("scanMeshMaterial");
                    if (prop != null && prop.objectReferenceValue == null)
                    {
                        Material mat = GetOrCreateScanMaterial();
                        if (mat != null) prop.objectReferenceValue = mat;
                    }
                    AssignCompute(so, "surfaceNetsCompute", PKG_SHADERS + "SurfaceNetsExtract.compute");
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(me);
                    break;
                }
                case TriplanarCache tc:
                {
                    var so = new SerializedObject(tc);
                    AssignCompute(so, "bakeCompute", PKG_SHADERS + "TriplanarBake.compute");
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(tc);
                    break;
                }
                case TextureRefinement tr:
                {
                    var so = new SerializedObject(tr);
                    AssignAsset<Shader>(so, "refinedMeshShader", PKG_SHADERS + "RefinedMesh.shader");
                    AssignAsset<Shader>(so, "refinedMeshBackfaceShader", PKG_SHADERS + "RefinedMeshBackface.shader");
                    AssignAsset<Shader>(so, "occlusionMeshShader", PKG_SHADERS + "OcclusionMesh.shader");
                    AssignAsset<ComputeShader>(so, "atlasBakeCompute", PKG_SHADERS + "AtlasBakeCompute.compute");
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(tr);
                    break;
                }
                case DepthDebugOverlay dd:
                {
                    var so = new SerializedObject(dd);
                    AssignAsset<Shader>(so, "depthVisualizeShader", PKG_SHADERS + "DepthVisualize.shader");
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(dd);
                    break;
                }
                case RoomScanner rs:
                {
                    var so = new SerializedObject(rs);
                    AssignAsset<Shader>(so, "debugOverlayShader", PKG_SHADERS + "DebugOverlay.shader");
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(rs);
                    break;
                }
                case UI.ControllerRayDriver crd:
                {
                    var so = new SerializedObject(crd);
                    AssignAsset<Shader>(so, "overlayShader", PKG_SHADERS + "DebugOverlay.shader");
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(crd);
                    break;
                }
            }

#if HAS_GAUSSIAN_SPLATTING
            WireGSplatComponent(component);
#endif
#if HAS_AI_INFERENCE
            WireAIDetectionComponent(component);
#endif
        }

        internal static void BuildXAtlasPlugin()
        {
            string pkgRoot = Path.GetFullPath("Packages/com.genesis.roomscan/Runtime");
            string srcDir = Path.Combine(pkgRoot, "Native/xatlas");
            string srcApi = Path.Combine(srcDir, "xatlas_c_api.cpp");
            string srcImpl = Path.Combine(srcDir, "xatlas.cpp");
            string meshoptDir = Path.Combine(pkgRoot, "Native/meshoptimizer");
            string srcSimplifier = Path.Combine(meshoptDir, "simplifier.cpp");

            if (!System.IO.File.Exists(srcApi) || !System.IO.File.Exists(srcImpl))
            {
                EditorUtility.DisplayDialog("Build xatlas",
                    $"Source files not found in:\n{srcDir}\n\nExpected xatlas.cpp and xatlas_c_api.cpp",
                    "OK");
                return;
            }

            bool hasMeshopt = System.IO.File.Exists(srcSimplifier);
            if (!hasMeshopt)
                Debug.LogWarning("[RoomScan Setup] meshoptimizer sources not found — building without mesh simplification");

            string meshoptSrc = hasMeshopt ? $" \"{srcSimplifier}\"" : "";
            string meshoptInc = hasMeshopt ? $" -I\"{meshoptDir}\"" : "";

            var builds = new System.Collections.Generic.List<(string label, string exe, string args, string outAssetPath)>();

            // Host editor plugin (platform-specific)
#if UNITY_EDITOR_WIN
            {
                string clExe = FindMsvcCompiler();
                if (clExe != null)
                {
                    string outDir = Path.Combine(pkgRoot, "Plugins/Windows");
                    Directory.CreateDirectory(outDir);
                    string outPath = Path.Combine(outDir, "xatlas.dll");
                    string incFlags = hasMeshopt ? $" /I\"{meshoptDir}\"" : "";
                    string allSrc = $"\"{srcApi}\" \"{srcImpl}\"" + (hasMeshopt ? $" \"{srcSimplifier}\"" : "");
                    string bArgs = $"/nologo /O2 /std:c++14 /EHsc /LD{incFlags} {allSrc} /Fe:\"{outPath}\" /link /DLL";
                    builds.Add(("Windows xatlas", clExe, bArgs,
                        "Packages/com.genesis.roomscan/Runtime/Plugins/Windows/xatlas.dll"));
                }
                else
                {
                    string clangExe = FindHostClang();
                    if (clangExe != null)
                    {
                        string outDir = Path.Combine(pkgRoot, "Plugins/Windows");
                        Directory.CreateDirectory(outDir);
                        string outPath = Path.Combine(outDir, "xatlas.dll");
                        string bArgs = $"-shared -O2 -std=c++11{meshoptInc} " +
                                       $"-o \"{outPath}\" \"{srcApi}\" \"{srcImpl}\"{meshoptSrc}";
                        builds.Add(("Windows xatlas", clangExe, bArgs,
                            "Packages/com.genesis.roomscan/Runtime/Plugins/Windows/xatlas.dll"));
                    }
                    else
                    {
                        Debug.LogError("[RoomScan Setup] No C++ compiler found. Install Visual Studio " +
                            "with C++ Desktop workload, or add clang++/g++ to your PATH.");
                    }
                }
            }
#elif UNITY_EDITOR_LINUX
            {
                string outDir = Path.Combine(pkgRoot, "Plugins/Linux");
                Directory.CreateDirectory(outDir);
                string outPath = Path.Combine(outDir, "libxatlas.so");
                string bArgs = $"-shared -O2 -fPIC -std=c++11 -fvisibility=hidden{meshoptInc} " +
                               $"-o \"{outPath}\" \"{srcApi}\" \"{srcImpl}\"{meshoptSrc}";
                string compiler = FindHostClang() ?? "g++";
                builds.Add(("Linux xatlas", compiler, bArgs,
                    "Packages/com.genesis.roomscan/Runtime/Plugins/Linux/libxatlas.so"));
            }
#else // macOS
            {
                string outDir = Path.Combine(pkgRoot, "Plugins/macOS");
                Directory.CreateDirectory(outDir);
                string outPath = Path.Combine(outDir, "libxatlas.bundle");
                string bArgs = $"-shared -O2 -fPIC -std=c++11 -fvisibility=hidden{meshoptInc} " +
                               $"-o \"{outPath}\" \"{srcApi}\" \"{srcImpl}\"{meshoptSrc}";
                builds.Add(("macOS xatlas", "clang++", bArgs,
                    "Packages/com.genesis.roomscan/Runtime/Plugins/macOS/libxatlas.bundle"));
            }
#endif

            // Android ARM64 (cross-compile from any host)
            {
                string ndkClang = FindNdkClang();
                if (ndkClang != null)
                {
                    string outDir = Path.Combine(pkgRoot, "Plugins/Android");
                    Directory.CreateDirectory(outDir);
                    string outPath = Path.Combine(outDir, "libxatlas.so");
                    string bArgs = $"-shared -O2 -fPIC -std=c++11 -fvisibility=hidden{meshoptInc} " +
                                   $"-o \"{outPath}\" \"{srcApi}\" \"{srcImpl}\"{meshoptSrc}";
                    builds.Add(("Android xatlas", ndkClang, bArgs,
                        "Packages/com.genesis.roomscan/Runtime/Plugins/Android/libxatlas.so"));
                }
            }

            if (builds.Count == 0)
            {
                Debug.LogError("[RoomScan Setup] No build targets available");
                return;
            }

            StartAsyncBuilds(builds);
        }

        static string FindNdkClang()
        {
            string ndkPath = null;
            try
            {
                ndkPath = UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath;
            }
            catch
            {
                Debug.LogWarning("[RoomScan Setup] Android NDK path not configured. Skipping Android build.");
                return null;
            }

            if (string.IsNullOrEmpty(ndkPath) || !Directory.Exists(ndkPath))
            {
                Debug.LogWarning($"[RoomScan Setup] NDK not found at: {ndkPath}");
                return null;
            }

            string prebuilt = Path.Combine(ndkPath, "toolchains/llvm/prebuilt");
            if (!Directory.Exists(prebuilt)) return null;

            string[] hosts = Directory.GetDirectories(prebuilt);
            if (hosts.Length == 0) return null;

            string binDir = Path.Combine(hosts[0], "bin");
            // Windows NDK ships .cmd wrappers; Unix has bare executables
            string[] candidates = {
                Path.Combine(binDir, "aarch64-linux-android31-clang++.cmd"),
                Path.Combine(binDir, "aarch64-linux-android31-clang++.exe"),
                Path.Combine(binDir, "aarch64-linux-android31-clang++"),
            };
            foreach (string c in candidates)
                if (System.IO.File.Exists(c)) return c;

            Debug.LogWarning($"[RoomScan Setup] NDK clang++ not found in {binDir}");
            return null;
        }

        static string FindHostClang()
        {
            // Check common locations for clang++ on the host
            string[] candidates;
#if UNITY_EDITOR_WIN
            candidates = new[] { "clang++.exe", "clang++", "g++.exe" };
#else
            candidates = new[] { "clang++", "g++" };
#endif
            foreach (string name in candidates)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = name, Arguments = "--version",
                        UseShellExecute = false, RedirectStandardOutput = true,
                        RedirectStandardError = true, CreateNoWindow = true
                    };
                    using (var p = System.Diagnostics.Process.Start(psi))
                    {
                        p.WaitForExit(3000);
                        if (p.ExitCode == 0) return name;
                    }
                }
                catch { /* not found, try next */ }
            }
            return null;
        }

#if UNITY_EDITOR_WIN
        static string FindMsvcCompiler()
        {
            // Use vswhere to locate MSVC cl.exe
            string vswhere = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio/Installer/vswhere.exe");
            if (!System.IO.File.Exists(vswhere)) return null;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 " +
                                "-property installationPath",
                    UseShellExecute = false, RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string vsPath = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(5000);
                    if (string.IsNullOrEmpty(vsPath)) return null;

                    string vcToolsDir = Path.Combine(vsPath, "VC/Tools/MSVC");
                    if (!Directory.Exists(vcToolsDir)) return null;

                    var versions = Directory.GetDirectories(vcToolsDir);
                    if (versions.Length == 0) return null;

                    System.Array.Sort(versions);
                    string latest = versions[versions.Length - 1];
                    string cl = Path.Combine(latest, "bin/Hostx64/x64/cl.exe");
                    return System.IO.File.Exists(cl) ? cl : null;
                }
            }
            catch { return null; }
        }
#endif

        // Async build state
        static System.Collections.Generic.List<(string label, System.Diagnostics.Process proc, string outAssetPath,
            System.Text.StringBuilder stdout, System.Text.StringBuilder stderr)> _activeBuilds;
        static int _totalBuilds;

        static void StartAsyncBuilds(
            System.Collections.Generic.List<(string label, string exe, string args, string outAssetPath)> builds)
        {
            _activeBuilds = new();
            _totalBuilds = builds.Count;

            foreach (var (label, exe, args, outAssetPath) in builds)
            {
                try
                {
                    string fileName = exe;
                    string arguments = args;

                    // .cmd/.bat files on Windows cannot be started directly with
                    // UseShellExecute=false; route through cmd.exe instead.
                    if (exe.EndsWith(".cmd", System.StringComparison.OrdinalIgnoreCase) ||
                        exe.EndsWith(".bat", System.StringComparison.OrdinalIgnoreCase))
                    {
                        fileName = "cmd.exe";
                        arguments = $"/c \"\"{exe}\" {args}\"";
                    }

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    var proc = System.Diagnostics.Process.Start(psi);
                    var stdoutBuf = new System.Text.StringBuilder();
                    var stderrBuf = new System.Text.StringBuilder();
                    proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuf.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuf.AppendLine(e.Data); };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    _activeBuilds.Add((label, proc, outAssetPath, stdoutBuf, stderrBuf));
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[RoomScan Setup] Failed to start {label}: {e.Message}");
                }
            }

            if (_activeBuilds.Count == 0)
            {
                Debug.LogError("[RoomScan Setup] No builds started");
                return;
            }

            EditorApplication.update += PollXAtlasBuilds;
            EditorUtility.DisplayProgressBar("Building xatlas", "Compiling native plugins...", 0f);
        }

        static void PollXAtlasBuilds()
        {
            if (_activeBuilds == null) return;

            int done = 0;
            foreach (var (label, proc, _, _, _) in _activeBuilds)
                if (proc.HasExited) done++;

            float progress = (float)done / _totalBuilds;
            string building = done < _totalBuilds
                ? $"Compiling... ({done}/{_totalBuilds} done)"
                : "Finishing...";
            EditorUtility.DisplayProgressBar("Building xatlas", building, progress);

            if (done < _totalBuilds) return;

            // All done
            EditorApplication.update -= PollXAtlasBuilds;
            EditorUtility.ClearProgressBar();

            bool allOk = true;
            var results = new System.Text.StringBuilder();

            foreach (var (label, proc, outAssetPath, _, stderrBuf) in _activeBuilds)
            {
                string stderr = stderrBuf.ToString();
                bool ok = proc.ExitCode == 0;
                allOk &= ok;
                results.AppendLine($"  {label}: {(ok ? "OK" : $"FAILED (exit {proc.ExitCode})")}");

                if (!ok)
                    Debug.LogError($"[RoomScan Setup] {label} build failed (exit {proc.ExitCode}):\n{stderr}");
                else if (!string.IsNullOrWhiteSpace(stderr))
                    Debug.LogWarning($"[RoomScan Setup] {label} warnings:\n{stderr}");
                else
                    Debug.Log($"[RoomScan Setup] {label} build succeeded");

                proc.Dispose();
            }

            AssetDatabase.Refresh();

            // Configure plugin importers after AssetDatabase sees the new files
            EditorApplication.delayCall += () =>
            {
                foreach (var (label, _, outAssetPath, _, _) in _activeBuilds)
                    ConfigurePluginImporter(outAssetPath);
                _activeBuilds = null;
            };

            if (allOk)
                Debug.Log($"[RoomScan Setup] xatlas build complete:\n{results}");
        }

        static void ConfigurePluginImporter(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[RoomScan Setup] PluginImporter not found for {assetPath}");
                return;
            }

            bool isAndroid = assetPath.Contains("/Android/");
            bool isWindows = assetPath.Contains("/Windows/");
            bool isLinux = assetPath.Contains("/Linux/");

            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(!isAndroid);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, isAndroid);

            string platformLabel;
            if (isAndroid)
            {
                importer.SetPlatformData(BuildTarget.Android, "CPU", "ARM64");
                platformLabel = "Android ARM64";
            }
            else if (isWindows)
            {
                importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
                importer.SetEditorData("CPU", "AnyCPU");
                importer.SetEditorData("OS", "Windows");
                platformLabel = "Windows Editor";
            }
            else if (isLinux)
            {
                importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, true);
                importer.SetEditorData("CPU", "AnyCPU");
                importer.SetEditorData("OS", "Linux");
                platformLabel = "Linux Editor";
            }
            else
            {
                importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);
                importer.SetEditorData("CPU", "AnyCPU");
                importer.SetEditorData("OS", "OSX");
                platformLabel = "macOS Editor";
            }

            importer.SaveAndReimport();
            Debug.Log($"[RoomScan Setup] Configured plugin importer: {assetPath} ({platformLabel})");
        }

        // -- Master Button ------------------------------------------------

        void DrawMasterButton()
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14,
                fixedHeight = 36
            };

            using (new EditorGUI.DisabledScope(_gameReadyFixInProgress))
            {
                if (GUILayout.Button("\u2261  Setup Everything", style))
                    SetupEverything();
            }
        }

        async void SetupEverything()
        {
            if (_gameReadyFixInProgress) return;

            _gameReadyFixInProgress = true;
            try
            {
                if (TrySwitchToAndroidBuildTarget("Setup Everything")) return;

                EditorUtility.DisplayProgressBar("Setup Everything",
                    "Fixing Android XR prerequisites (Outstanding + Recommended)\u2026", 0.05f);
                VRProjectBootstrap.Audit();

                // URP must exist before anything else so shaders resolve.
                EditorUtility.DisplayProgressBar("Setup Everything",
                    "Ensuring URP pipeline + Android XR defaults\u2026", 0.10f);
                EnsureURPSetup();

                await VRProjectBootstrap.FixAllAsync(CheckSeverity.Recommended);

                // XR Origin rig + ARCameraManager — does the right thing
                // whether or not a rig is already present. Done before AR
                // session so AROcclusionManager can attach to the rig camera.
                EditorUtility.DisplayProgressBar("Setup Everything",
                    "Setting up XR Origin rig\u2026", 0.30f);
                EnsureXRRig();
                Refresh();

                EditorUtility.DisplayProgressBar("Setup Everything",
                    "Setting up AR session + occlusion\u2026", 0.35f);
                if (_arSession == null) FixARSession();
                if (_arOcclusion == null) FixAROcclusion();

                // Idempotent: CAMERA + cleartext module, Quest entries
                // stripped from any custom main manifest.
                EditorUtility.DisplayProgressBar("Setup Everything",
                    "Updating Android manifest additions + Player Settings\u2026", 0.50f);
                EnsureAndroidXRManifest();
                if (!_insecureHttpAllowed)
                {
                    PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                    Debug.Log("[RoomScan Setup] Set Player Settings > insecureHttpOption to AlwaysAllowed");
                }

                EditorUtility.DisplayProgressBar("Setup Everything",
                    "Adding all components + wiring shaders\u2026", 0.75f);
                FixComponents();
                FixShaderWiring();

                RefreshNativePlugins();
                bool xatlasMissing = !_xatlasAndroid || !_xatlasEditor;
                if (xatlasMissing)
                {
                    EditorUtility.DisplayProgressBar("Setup Everything",
                        "Starting xatlas plugin build (background)\u2026", 0.95f);
                    BuildXAtlasPlugin();
                }

                Debug.Log("[RoomScan Setup] Scene setup complete." +
                    (xatlasMissing ? " (xatlas build running in background)" : ""));
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RoomScan Setup] Setup Everything failed: {ex}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _gameReadyFixInProgress = false;
                MarkDirty();
                Refresh();
                Repaint();
            }
        }

        // =================================================================
        //  GUI HELPERS
        // =================================================================

        void BeginSection(string title)
        {
            GUILayout.Space(6);
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(22));
            EditorGUI.DrawRect(rect, COL_SECT);
            var labelRect = new Rect(rect.x + 8, rect.y + 2, rect.width - 16, rect.height);
            var prev = GUI.color;
            GUI.color = Color.white;
            GUI.Label(labelRect, title, EditorStyles.boldLabel);
            GUI.color = prev;
        }

        static void EndSection() => GUILayout.Space(2);

        void StatusRow(string label, bool ok)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);

            string icon = ok ? "\u2713" : "\u2717";
            Color col = ok ? COL_OK : COL_MISS;
            string detail = ok ? "OK" : "Missing";

            var prev = GUI.color;
            GUI.color = col;
            GUILayout.Label(icon, EditorStyles.boldLabel, GUILayout.Width(18));
            GUI.color = prev;

            GUILayout.Label(label, GUILayout.ExpandWidth(true));

            prev = GUI.color;
            GUI.color = col;
            GUILayout.Label(detail, EditorStyles.miniLabel, GUILayout.Width(60));
            GUI.color = prev;

            EditorGUILayout.EndHorizontal();
        }

        void StatusRowOptional(string label, bool attached)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);

            string icon = attached ? "\u2713" : "\u2022";
            Color col = attached ? COL_OK : COL_INFO;
            string detail = attached ? "OK" : "Not Added";

            var prev = GUI.color;
            GUI.color = col;
            GUILayout.Label(icon, EditorStyles.boldLabel, GUILayout.Width(18));
            GUI.color = prev;

            GUILayout.Label(label, GUILayout.ExpandWidth(true));

            prev = GUI.color;
            GUI.color = col;
            GUILayout.Label(detail, EditorStyles.miniLabel, GUILayout.Width(60));
            GUI.color = prev;

            EditorGUILayout.EndHorizontal();
        }

        // =================================================================
        //  UTILITY
        // =================================================================

        static T FindAny<T>() where T : Object =>
            Object.FindAnyObjectByType<T>(FindObjectsInactive.Include);

        static Component FindComponentByTypeName(string typeName)
        {
            foreach (var root in SceneRoots())
            {
                var found = root.GetComponentsInChildren<Component>(true)
                    .FirstOrDefault(c => c != null && c.GetType().Name == typeName);
                if (found != null) return found;
            }
            return null;
        }

        static bool AreFieldsAssigned(Object target, params string[] fieldNames)
        {
            var so = new SerializedObject(target);
            foreach (string name in fieldNames)
            {
                var prop = so.FindProperty(name);
                if (prop == null || prop.objectReferenceValue == null)
                    return false;
            }
            return true;
        }

        static GameObject FindByName(string exact)
        {
            foreach (var root in SceneRoots())
            {
                var t = DeepFind(root.transform,
                    tr => tr.name.Equals(exact, System.StringComparison.Ordinal));
                if (t != null) return t.gameObject;
            }
            return null;
        }

        static Transform DeepFind(Transform root, System.Func<Transform, bool> pred)
        {
            if (pred(root)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var hit = DeepFind(root.GetChild(i), pred);
                if (hit != null) return hit;
            }
            return null;
        }

        static GameObject[] SceneRoots() =>
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

        static void MarkDirty() =>
            EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        /// <summary>
        /// Sets m_RenderMode to 1 (WorldSpace). The public renderMode property
        /// and PanelRenderMode enum are internal in Unity 6000.3.
        /// </summary>
        static void SetPanelRenderModeWorldSpace(PanelSettings panel)
        {
            const int WorldSpace = 1;
            var so = new SerializedObject(panel);
            var prop = so.FindProperty("m_RenderMode");
            if (prop != null && prop.intValue != WorldSpace)
            {
                prop.intValue = WorldSpace;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(panel);
            }
        }

        internal static void EnsureDebugMenuAssets()
        {
            var ctrl = FindAny<DebugMenuController>();
            if (ctrl == null) return;

            var uiDoc = ctrl.GetComponent<UIDocument>();
            if (uiDoc == null) return;

            Undo.RecordObject(uiDoc, "Assign DebugMenu UIDocument assets");

            if (uiDoc.visualTreeAsset == null)
            {
                var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                    "Packages/com.genesis.roomscan/Runtime/UI/DebugMenu.uxml");
                if (uxml != null) uiDoc.visualTreeAsset = uxml;
            }

            if (uiDoc.panelSettings == null)
            {
                var panel = FindOrCreatePanelSettings();
                if (panel != null) uiDoc.panelSettings = panel;
            }

            // Ensure PanelSettings is configured for world-space VR rendering.
            // renderMode / PanelRenderMode are internal in 6000.3; use SerializedObject.
            if (uiDoc.panelSettings != null)
                SetPanelRenderModeWorldSpace(uiDoc.panelSettings);

            // World-space UIDocument properties:
            // - Dynamic size mode: panel auto-sizes to the content layout (480×640 from USS)
            // - Pivot = Center: transform position = center of the visible panel
            // - PivotReferenceSize = Layout: pivot calculated from root element layout, not bounding box
            uiDoc.worldSpaceSizeMode = UIDocument.WorldSpaceSizeMode.Dynamic;
            uiDoc.pivot = Pivot.Center;
            uiDoc.pivotReferenceSize = PivotReferenceSize.Layout;

            // 480px / 100 PPU = 4.8 local units. Scale 0.08 → 0.384m wide.
            const float worldScale = 0.08f;
            if (Mathf.Abs(ctrl.transform.localScale.x - worldScale) > 0.01f)
            {
                Undo.RecordObject(ctrl.transform, "Scale DebugMenu for VR");
                ctrl.transform.localScale = Vector3.one * worldScale;
            }

            EditorUtility.SetDirty(uiDoc);
        }

        static PanelSettings FindOrCreatePanelSettings()
        {
            const string assetName = "DebugMenuPanelSettings";

            string[] guids = AssetDatabase.FindAssets($"t:PanelSettings {assetName}");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                var existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
                if (existing != null) return existing;
            }

            const string dir = "Assets/Settings";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets", "Settings");

            const string assetPath = dir + "/" + assetName + ".asset";
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            AssetDatabase.CreateAsset(panel, assetPath);
            SetPanelRenderModeWorldSpace(panel);
            AssetDatabase.SaveAssets();
            Debug.Log($"[RoomScanWizard] Created PanelSettings (WorldSpace) at {assetPath}");
            return panel;
        }

        static string GetLanIp()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;

                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                            continue;
                        string ip = addr.Address.ToString();
                        if (ip.StartsWith("127.")) continue;
                        return ip;
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[RoomScanWizard] Failed to detect LAN IP: {e.Message}");
            }
            return null;
        }
    }
}
