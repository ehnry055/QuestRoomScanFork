using System;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Genesis.RoomScan
{
    /// <summary>
    /// RGB camera provider for Samsung Galaxy XR (Android XR): frames from the
    /// world-facing colour camera through Unity's <see cref="WebCamTexture"/>,
    /// which Unity backs with Camera2 on Android.
    ///
    /// <para>
    /// Android XR's AR Foundation camera subsystem delivers no images,
    /// intrinsics or timestamps (it only switches passthrough on and off), so
    /// this provider does not use <c>ARCameraManager</c> at all and never
    /// enables or disables it. On the SM-I610, Camera2 ids 0 and 2 are the
    /// back (world-facing) colour cameras: 3000×3000 sensor, 1.45 mm lens on a
    /// 2.4 × 2.4 mm sensor, AE ranges [15,24] and [24,24], and no published
    /// lens calibration or lens pose. The first back-facing device (expected:
    /// id 0) is opened at a square size by default, so the frame covers the
    /// sensor's full field of view.
    /// </para>
    ///
    /// <para>
    /// <b>Intrinsics are estimated, not calibrated</b>:
    /// fx = fy = (focal mm / sensor mm) × S and the principal point is the
    /// image centre, where S is the longer side of the delivered frame. The
    /// values are expressed in this package's convention (see
    /// <see cref="SensorResolution"/>): pixels at the delivered scale,
    /// principal point with a bottom-left origin, and a non-square frame
    /// treated as a centre crop of a square S×S sensor.
    /// </para>
    ///
    /// <para>
    /// <b>Pose</b> is the tracked XR camera (head) pose plus the serialized
    /// head-to-lens offset, which defaults to zero and must be calibrated on
    /// the device. It is world space, per <see cref="ICameraProvider"/>.
    /// </para>
    ///
    /// Frame delivery runs only between <see cref="StartCapture"/> (scan
    /// start) and <see cref="StopCapture"/>. The CAMERA permission is
    /// normally requested by <see cref="RoomScanner.StartScanningAsync"/>
    /// through <see cref="AndroidRuntimePermission"/> before capture starts;
    /// hosts can front-load it with <see cref="RequestCameraPermissionAsync"/>.
    /// Denied permission or no back-facing camera leaves the provider
    /// not-playing, and the scan runs depth-only.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class PassthroughCameraProvider : MonoBehaviour, ICameraProvider, ICameraFrameTiming
    {
        /// <summary>The Android runtime permission required for camera frames
        /// (<c>android.permission.CAMERA</c>).</summary>
        public const string CameraPermissionId = AndroidRuntimePermission.Camera;

        // WebCamTexture reports this placeholder size until the first real
        // frame has arrived.
        private const int PlaceholderSize = 16;
        private const int PoseHistoryLength = 128;

        [Header("Camera stream")]
        [SerializeField, Tooltip(
            "Exact WebCamTexture device name to open. Empty = the first " +
            "back-facing device (Camera2 id 0 on Galaxy XR). The device list " +
            "is logged when capture starts.")]
        private string deviceName = "";

        [SerializeField, Tooltip(
            "Requested frame size. Unity picks the closest supported stream. " +
            "Square sizes (1080x1080, 1500x1500, 2400x2400, 3000x3000) keep " +
            "the sensor's full field of view; 4:3 and 16:9 sizes are centre crops.")]
        private Vector2Int requestedSize = new(1080, 1080);

        [SerializeField, Tooltip("Requested frame rate. The camera's AE ranges are [15,24] and [24,24].")]
        private int requestedFps = 24;

        [Header("Lens (estimated: no calibration is published)")]
        [SerializeField, Tooltip("Lens focal length in mm (Camera2 LENS_INFO_AVAILABLE_FOCAL_LENGTHS: 1.45).")]
        private float lensFocalLengthMm = 1.45f;

        [SerializeField, Tooltip(
            "Physical width of the full sensor in mm (Camera2 SENSOR_INFO_PHYSICAL_SIZE: 2.4 x 2.4). " +
            "fx = fy = focal / sensor × the frame's longer side in pixels.")]
        private float sensorSizeMm = 2.4f;

        [Header("Image orientation")]
        [SerializeField, Tooltip(
            "Flip frames vertically. Combined (XOR) with WebCamTexture.videoVerticallyMirrored. " +
            "Toggle on the device if colour lands upside down.")]
        private bool flipVertical;

        [SerializeField, Tooltip("Flip frames horizontally (mirror). Toggle on the device if colour lands mirrored.")]
        private bool flipHorizontal;

        [SerializeField, Tooltip(
            "Fold WebCamTexture.videoRotationAngle into the camera pose as a roll about the " +
            "optical axis. Expected to be 0 on Galaxy XR (sensor orientation 0).")]
        private bool applyReportedRotation = true;

        [Header("Head-to-camera extrinsic (calibrate on the device)")]
        [SerializeField, Tooltip(
            "Lens position in the tracked XR camera's (head's) local frame, metres " +
            "(+X right, +Y up, +Z forward). Default zero = head centre; the real " +
            "world-facing camera sits a few centimetres off it.")]
        private Vector3 cameraLocalPosition = Vector3.zero;

        [SerializeField, Tooltip(
            "Lens orientation relative to the tracked XR camera (head), Euler degrees. " +
            "Default zero = looking where the head looks.")]
        private Vector3 cameraLocalEulerAngles = Vector3.zero;

        [Header("Timing")]
        [SerializeField, Range(0f, 0.25f), Tooltip(
            "Seconds between a frame's capture and the head pose it is paired with: camera " +
            "pipeline latency plus the tracked pose's display-time prediction. The pose is " +
            "looked up this far back from the tick the frame arrived on. 0 = the arrival " +
            "tick's pose. Calibrate on the device (colour smears sideways under head motion " +
            "when it is wrong).")]
        private float captureLatencySeconds;

        [Header("Editor")]
        [SerializeField, Tooltip(
            "Open a webcam in Editor Play Mode. Off by default so Play Mode never turns on " +
            "the development machine's camera.")]
        private bool useInEditor;

        private WebCamTexture _webcam;
        private RenderTexture _frame;
        private int _frameWidth;
        private int _frameHeight;
        private int _receivedFrames;
        private int _latestFrame = -1;
        private int _polledFrame = -1;
        private double _frameTimeSeconds;
        private Pose _framePose = Pose.identity;
        private bool _capturing;
        private int _startGeneration;
        private bool _loggedFirstFrame;
        private bool _warnedNoHead;

        private Transform _head;
        private float _nextHeadSearch;
        private readonly double[] _poseTimes = new double[PoseHistoryLength];
        private readonly Pose[] _poses = new Pose[PoseHistoryLength];
        private int _poseCount;
        private int _poseNext;

        /// <inheritdoc />
        /// <remarks>True only on the tick a new camera frame arrived.</remarks>
        public bool IsReady
        {
            get
            {
                Poll();
                return IsPlaying && _latestFrame == Time.frameCount;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// True while the camera is running <b>and</b> at least one real frame
        /// has arrived since <see cref="StartCapture"/>. A camera that never
        /// delivers (permission denied, no device, still opening) reads false,
        /// so <see cref="RoomScanner"/> keeps the normal-colour fallback on.
        /// </remarks>
        public bool IsPlaying
        {
            get
            {
                Poll();
                return _capturing && _webcam != null && _webcam.isPlaying
                    && _receivedFrames > 0 && _frame != null;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// A package-owned sRGB <see cref="RenderTexture"/> holding a copy of the
        /// latest camera frame (orientation flips applied). Stable until the
        /// next frame arrives. Null while not playing.
        /// </remarks>
        public Texture CurrentFrame => IsPlaying ? _frame : null;

        /// <summary>
        /// World-space pose of the camera for <see cref="CurrentFrame"/>, fixed
        /// when that frame arrived: the tracked XR camera (head) pose
        /// <c>captureLatencySeconds</c> before arrival, times the
        /// serialized head-to-lens offset (and the reported image rotation).
        /// Consumers must not apply a tracking-to-world transform on top.
        /// Identity while not playing.
        /// </summary>
        public Pose CameraPose => IsPlaying ? _framePose : Pose.identity;

        /// <inheritdoc />
        /// <remarks>
        /// Estimated capture time on <see cref="Time.realtimeSinceStartupAsDouble"/>:
        /// the realtime of the tick the frame was first seen, minus
        /// <c>captureLatencySeconds</c>. <see cref="WebCamTexture"/> does
        /// not expose the Camera2 SENSOR_TIMESTAMP, so this is an arrival time.
        /// It trails the true exposure by the camera pipeline latency
        /// (tens of milliseconds) plus up to one app frame of polling, and
        /// frames that land between two ticks share the later tick's time.
        /// Only differences are meaningful.
        /// </remarks>
        public double FrameTimeSeconds =>
            IsPlaying ? _frameTimeSeconds : Time.realtimeSinceStartupAsDouble;

        /// <inheritdoc />
        /// <remarks>fx = fy = (lens focal mm / sensor mm) × S, S = longer side
        /// of the delivered frame. Estimated; not a calibration.</remarks>
        public Vector2 FocalLength
        {
            get
            {
                float f = FocalPixels(SquareSide);
                return new Vector2(f, f);
            }
        }

        /// <inheritdoc />
        /// <remarks>Centre of the S×S sensor square (bottom-left origin), which
        /// is the centre of the delivered frame after the centre crop.</remarks>
        public Vector2 PrincipalPoint
        {
            get
            {
                float c = SquareSide * 0.5f;
                return new Vector2(c, c);
            }
        }

        /// <summary>
        /// The full-field-of-view sensor square (S, S) at the delivered scale,
        /// S = the delivered frame's longer side. The physical sensor is square
        /// (3000×3000), Camera2 centre-crops non-square streams to their aspect
        /// before scaling, and the package's projection code treats a frame
        /// smaller than <see cref="SensorResolution"/> as exactly that centre
        /// crop (<c>VolumeIntegration.compute</c>, <c>TriplanarBake.compute</c>,
        /// <c>TextureRefinement</c>). Expressing the sensor at the delivered
        /// scale keeps focal length in delivered pixels for every consumer.
        /// </summary>
        public Vector2 SensorResolution
        {
            get
            {
                float s = SquareSide;
                return new Vector2(s, s);
            }
        }

        /// <inheritdoc />
        public Vector2 CurrentResolution
        {
            get
            {
                Vector2Int size = DeliveredSize;
                return new Vector2(size.x, size.y);
            }
        }

        private Vector2Int DeliveredSize =>
            _frameWidth > 0 && _frameHeight > 0
                ? new Vector2Int(_frameWidth, _frameHeight)
                : new Vector2Int(Mathf.Max(1, requestedSize.x), Mathf.Max(1, requestedSize.y));

        private float SquareSide
        {
            get
            {
                Vector2Int size = DeliveredSize;
                return Mathf.Max(size.x, size.y);
            }
        }

        private float FocalPixels(float squareSide) =>
            sensorSizeMm > 0f ? lensFocalLengthMm / sensorSizeMm * squareSide : squareSide;

        /// <summary>
        /// True when the user has granted <c>android.permission.CAMERA</c>.
        /// Always true outside Android device builds.
        /// </summary>
        public static bool HasCameraPermission => AndroidRuntimePermission.Has(CameraPermissionId);

        /// <summary>
        /// Requests <c>android.permission.CAMERA</c> through the package's
        /// serialized permission queue and resolves once the user accepts,
        /// denies, or dismisses the system dialog. Always resolves <c>true</c>
        /// outside Android device builds, and immediately if already granted.
        /// </summary>
        public static Task<bool> RequestCameraPermissionAsync()
            => AndroidRuntimePermission.RequestAsync(CameraPermissionId);

        /// <inheritdoc />
        public void StartCapture()
        {
            if (_capturing) return;
            _capturing = true;
            int generation = ++_startGeneration;
            _ = StartCaptureAsync(generation);
        }

        private async Task StartCaptureAsync(int generation)
        {
            try
            {
                if (Application.isEditor && !useInEditor)
                {
                    Logger.Info("PassthroughCameraProvider: Editor webcam disabled (useInEditor off) — scanning depth-only.");
                    return;
                }

                if (!AndroidRuntimePermission.Has(CameraPermissionId))
                {
                    // RoomScanner.StartScanningAsync asks before StartCapture,
                    // so this only asks for hosts that start capture directly.
                    // A denial earlier in this process is respected rather
                    // than putting the dialog straight back up.
                    if (AndroidRuntimePermission.DeniedThisSession(CameraPermissionId))
                    {
                        Logger.Warning("CAMERA not granted — no RGB frames; scanning depth-only.");
                        return;
                    }
                    bool granted = await AndroidRuntimePermission.RequestAsync(CameraPermissionId);
                    if (!granted)
                    {
                        Logger.Warning("CAMERA denied — no RGB frames; scanning depth-only.");
                        return;
                    }
                }

                // Stopped (or stopped and restarted) while the dialog was up.
                if (!_capturing || generation != _startGeneration || this == null) return;

                OpenCamera();
            }
            catch (Exception e)
            {
                Logger.Error($"PassthroughCameraProvider: camera start failed: {e.Message}");
                CloseCamera();
            }
        }

        private void OpenCamera()
        {
            WebCamDevice[] devices = WebCamTexture.devices;
            LogDevices(devices);

            int pick = -1;
            for (int i = 0; i < devices.Length; i++)
            {
                if (!string.IsNullOrEmpty(deviceName))
                {
                    if (devices[i].name == deviceName) { pick = i; break; }
                }
                else if (!devices[i].isFrontFacing && devices[i].kind != WebCamKind.ColorAndDepth)
                {
                    pick = i;
                    break;
                }
            }

            if (pick < 0)
            {
                Logger.Warning(string.IsNullOrEmpty(deviceName)
                    ? "PassthroughCameraProvider: no back-facing camera — no RGB frames; scanning depth-only."
                    : $"PassthroughCameraProvider: camera '{deviceName}' not found — no RGB frames; scanning depth-only.");
                return;
            }

            string name = devices[pick].name;
            _webcam = new WebCamTexture(name,
                Mathf.Max(1, requestedSize.x), Mathf.Max(1, requestedSize.y), Mathf.Max(1, requestedFps))
            {
                name = "RoomScan WebCamTexture"
            };
            _webcam.Play();
            _receivedFrames = 0;
            _loggedFirstFrame = false;
            Logger.Info($"PassthroughCameraProvider: opened '{name}' " +
                        $"(requested {requestedSize.x}x{requestedSize.y} @ {requestedFps} fps).");
        }

        private static void LogDevices(WebCamDevice[] devices)
        {
            var sb = new System.Text.StringBuilder("PassthroughCameraProvider: cameras:");
            if (devices.Length == 0) sb.Append(" none");
            foreach (var d in devices)
            {
                sb.Append($" ['{d.name}' {(d.isFrontFacing ? "front" : "back")} {d.kind}");
                Resolution[] res = null;
                try { res = d.availableResolutions; } catch { /* not reported on every platform */ }
                if (res != null && res.Length > 0)
                {
                    sb.Append(':');
                    foreach (var r in res) sb.Append($" {r.width}x{r.height}");
                }
                sb.Append(']');
            }
            Logger.Info(sb.ToString());
        }

        /// <inheritdoc />
        /// <remarks>Stops the camera only. It never touches
        /// <c>ARCameraManager</c>, which on Android XR controls passthrough.</remarks>
        public void StopCapture()
        {
            if (!_capturing) return;
            _capturing = false;
            _startGeneration++;
            CloseCamera();
        }

        private void CloseCamera()
        {
            if (_webcam != null)
            {
                if (_webcam.isPlaying) _webcam.Stop();
                Destroy(_webcam);
                _webcam = null;
            }
            _receivedFrames = 0;
            _latestFrame = -1;
            _frameTimeSeconds = 0;
            _framePose = Pose.identity;
            _poseCount = 0;
            _poseNext = 0;
        }

        private void Update() => Poll();

        /// <summary>
        /// Once per app frame while capturing: records the head pose, and when
        /// the camera has delivered a new frame copies it into <c>_frame</c> and fixes
        /// its pose and time. Called from Update (execution order -50, before
        /// <see cref="RoomScanner"/>) and from the state getters, so the order
        /// callers read in does not matter.
        /// </summary>
        private void Poll()
        {
            int tick = Time.frameCount;
            if (_polledFrame == tick) return;
            _polledFrame = tick;

            if (!_capturing) return;

            double now = Time.realtimeSinceStartupAsDouble;
            bool haveHead = RecordHeadPose(now);

            if (_webcam == null || !_webcam.isPlaying || !_webcam.didUpdateThisFrame)
                return;

            int w = _webcam.width;
            int h = _webcam.height;
            if (w <= PlaceholderSize || h <= PlaceholderSize) return;

            if (!haveHead)
            {
                if (!_warnedNoHead)
                {
                    _warnedNoHead = true;
                    Logger.Warning("PassthroughCameraProvider: no XR camera to pose frames with — dropping RGB frames.");
                }
                return;
            }

            EnsureFrameTarget(w, h);
            bool flipV = flipVertical ^ _webcam.videoVerticallyMirrored;
            var scale = new Vector2(flipHorizontal ? -1f : 1f, flipV ? -1f : 1f);
            var offset = new Vector2(flipHorizontal ? 1f : 0f, flipV ? 1f : 0f);
            Graphics.Blit(_webcam, _frame, scale, offset);

            _frameWidth = w;
            _frameHeight = h;
            _frameTimeSeconds = now - captureLatencySeconds;
            _framePose = ComputeCameraPose(SampleHeadPose(_frameTimeSeconds));
            _latestFrame = tick;
            _receivedFrames++;

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Vector2 f = FocalLength;
                Logger.Info($"PassthroughCameraProvider: first frame {w}x{h} " +
                            $"(format {_webcam.graphicsFormat}, rotation {_webcam.videoRotationAngle}°, " +
                            $"vMirrored {_webcam.videoVerticallyMirrored}) fx=fy={f.x:F1} " +
                            $"c=({PrincipalPoint.x:F1},{PrincipalPoint.y:F1}) S={SquareSide:F0}.");
            }
        }

        private void EnsureFrameTarget(int w, int h)
        {
            if (_frame != null && _frame.width == w && _frame.height == h) return;
            if (_frame != null)
            {
                _frame.Release();
                Destroy(_frame);
            }
            // sRGB to match VolumeIntegrator's own frame copy: WebCamTexture
            // bytes are sRGB-encoded, and a blit between sRGB formats keeps them.
            _frame = new RenderTexture(w, h, 0, GraphicsFormat.R8G8B8A8_SRGB, 0)
            {
                name = "RoomScan RGB Frame",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _frame.Create();
        }

        private Pose ComputeCameraPose(Pose head)
        {
            Quaternion rot = head.rotation * Quaternion.Euler(cameraLocalEulerAngles);
            if (applyReportedRotation && _webcam != null && _webcam.videoRotationAngle != 0)
            {
                // videoRotationAngle is the clockwise turn that makes the
                // image upright, so the delivered image is the upright one
                // turned counter-clockwise. That is what a camera rolled
                // clockwise about its forward axis would see.
                rot *= Quaternion.Euler(0f, 0f, -_webcam.videoRotationAngle);
            }
            return new Pose(head.position + head.rotation * cameraLocalPosition, rot);
        }

        private bool RecordHeadPose(double time)
        {
            Transform head = ResolveHead();
            if (head == null) return false;
            _poseTimes[_poseNext] = time;
            _poses[_poseNext] = new Pose(head.position, head.rotation);
            _poseNext = (_poseNext + 1) % PoseHistoryLength;
            if (_poseCount < PoseHistoryLength) _poseCount++;
            return true;
        }

        /// <summary>Head pose at <paramref name="time"/>, interpolated from the
        /// per-tick history and clamped to its ends.</summary>
        private Pose SampleHeadPose(double time)
        {
            int newest = (_poseNext - 1 + PoseHistoryLength) % PoseHistoryLength;
            if (_poseCount <= 1 || time >= _poseTimes[newest]) return _poses[newest];

            int later = newest;
            for (int n = 1; n < _poseCount; n++)
            {
                int earlier = (newest - n + PoseHistoryLength) % PoseHistoryLength;
                if (_poseTimes[earlier] <= time)
                {
                    double span = _poseTimes[later] - _poseTimes[earlier];
                    float t = span > 1e-6 ? (float)((time - _poseTimes[earlier]) / span) : 1f;
                    return new Pose(
                        Vector3.Lerp(_poses[earlier].position, _poses[later].position, t),
                        Quaternion.Slerp(_poses[earlier].rotation, _poses[later].rotation, t));
                }
                later = earlier;
            }
            return _poses[later];
        }

        private Transform ResolveHead()
        {
            if (_head != null) return _head;
            // Scene search at most once a second while nothing is found.
            if (Time.unscaledTime < _nextHeadSearch) return null;
            _nextHeadSearch = Time.unscaledTime + 1f;
            var origin = FindAnyObjectByType<XROrigin>();
            if (origin != null && origin.Camera != null)
                _head = origin.Camera.transform;
            else if (Camera.main != null)
                _head = Camera.main.transform;
            return _head;
        }

        private void OnDestroy()
        {
            StopCapture();
            CloseCamera();
            if (_frame != null)
            {
                _frame.Release();
                Destroy(_frame);
                _frame = null;
            }
        }
    }
}
