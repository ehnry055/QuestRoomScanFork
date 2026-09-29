using UnityEngine;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Interface for providing RGB camera frames and intrinsics to the scan pipeline.
    /// The built-in implementation is <see cref="PassthroughCameraProvider"/>
    /// (Galaxy XR world-facing camera); implement this to plug in another
    /// source and register it with <see cref="RoomScanner.SetCameraProvider"/>.
    /// <para>
    /// Intrinsics convention (shared by every consumer: the integration and
    /// triplanar compute shaders, keyframes, texture refinement, detection):
    /// pixels at the delivered frame's scale, principal point with a
    /// <b>bottom-left</b> origin, camera space +X right / +Y up / +Z forward.
    /// A frame smaller than <see cref="SensorResolution"/> is taken to be a
    /// centre crop of it (aspect crop, then uniform scale).
    /// </para>
    /// </summary>
    public interface ICameraProvider
    {
        /// <summary>True when the provider has a valid frame available this tick.</summary>
        bool IsReady { get; }

        /// <summary>True when the camera is running and has delivered at least
        /// one frame (may not have a new frame every tick). False turns the
        /// scan mesh's normal-colour fallback on.</summary>
        bool IsPlaying { get; }

        /// <summary>The most recent camera RGB frame as a GPU texture.</summary>
        Texture CurrentFrame { get; }

        /// <summary>World-space (Unity scene) pose of the camera for
        /// <see cref="CurrentFrame"/>. Used as is: consumers apply no
        /// tracking-to-world transform.</summary>
        Pose CameraPose { get; }

        /// <summary>Camera intrinsic focal length in pixels (fx, fy).</summary>
        Vector2 FocalLength { get; }

        /// <summary>Camera intrinsic principal point in pixels (cx, cy),
        /// bottom-left origin, in <see cref="SensorResolution"/> space.</summary>
        Vector2 PrincipalPoint { get; }

        /// <summary>Resolution the intrinsics are expressed in: the full
        /// sensor field of view at the scale of the delivered frame.</summary>
        Vector2 SensorResolution { get; }

        /// <summary>Actual delivered frame resolution (may differ from sensor resolution).</summary>
        Vector2 CurrentResolution { get; }

        /// <summary>Begins camera frame acquisition.</summary>
        void StartCapture();

        /// <summary>Stops camera frame acquisition and releases resources.</summary>
        void StopCapture();
    }

    /// <summary>
    /// Optional companion to <see cref="ICameraProvider"/>: the capture time of
    /// <see cref="ICameraProvider.CurrentFrame"/>, in seconds on a monotonic
    /// clock. Lets consumers measure head motion between frames from the
    /// frames' own timestamps rather than from the app frame they were
    /// noticed in, which is quantised to the display rate.
    /// </summary>
    public interface ICameraFrameTiming
    {
        /// <summary>Capture time of the current frame, seconds. Any monotonic
        /// origin; only differences are used.</summary>
        double FrameTimeSeconds { get; }
    }
}
