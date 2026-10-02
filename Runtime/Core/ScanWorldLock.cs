using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Keeps Unity world space locked to the physical room for the whole app
    /// session, so the world-space TSDF grid, the live mesh, keyframes and
    /// exports stay on the real surfaces when the OpenXR runtime moves its
    /// tracking origin (<c>XrEventDataReferenceSpaceChangePending</c>; on
    /// Galaxy XR this happens on every wake from headset sleep and several
    /// times a minute while running).
    ///
    /// <para>
    /// <b>How.</b> When the first scan starts, one <see cref="ARAnchor"/> is
    /// created a little in front of the head and its Unity world pose
    /// <c>W0</c> is recorded. The anchor is fixed in the room, so when the
    /// runtime moves the reference space every session-space pose (head,
    /// depth views, camera, controllers, hands) and the anchor's session pose
    /// move together. Each frame this component moves the XR Origin root so
    /// the anchor is back at <c>W0</c>; everything riding on the origin moves
    /// with it and the room stays where it was in world space. The grid,
    /// shaders, exports and persistence are untouched.
    /// </para>
    ///
    /// <para>
    /// <b>Maths.</b> Poses are rigid transforms composed right to left.
    /// <c>R</c> = XR Origin root world pose, <c>L</c> = the session-space
    /// frame (<see cref="XROrigin.CameraFloorOffsetObject"/>, the frame
    /// <see cref="DepthCapture"/> maps depth poses through) relative to the
    /// root, <c>A</c> = <see cref="ARTrackable.pose"/> of the anchor, which in
    /// AR Foundation 6 is session-relative. The anchor's world pose is
    /// <c>W = R·L·A</c>. Lock: <c>W0 = R0·L·A0</c>. After the runtime moves the
    /// reference space by <c>C</c>, every session pose becomes <c>C·x</c>, so
    /// <c>A' = C·A0</c>. Wanted: <c>R'</c> with <c>R'·L·A' = W0</c>, i.e.
    /// <c>R' = W0·A'⁻¹·L⁻¹</c>. Computed without inverting <c>L</c> as a
    /// world-space correction <c>Δ = W0·Wc⁻¹</c>, <c>Wc = R·L·A'</c> (the
    /// anchor's world pose with the current root), applied as <c>R' = Δ·R</c>:
    /// then <c>R'·L·A' = Δ·Wc = W0</c>. Any session pose <c>p</c> (a depth
    /// view, say) then maps to <c>R'·L·C·p = W0·A0⁻¹·p</c>, its pre-change
    /// world pose. Δ's rotation is <c>q = W0.rot·Wc.rot⁻¹</c>, its translation
    /// <c>t = W0.pos − q·Wc.pos</c>; the root becomes
    /// <c>(q·R.pos + t, q·R.rot)</c>. Only the root moves, so the XR Origin's
    /// floor-offset handling on the camera-offset child is left alone (a
    /// change there changes <c>L</c>, and the next frame absorbs it).
    /// <see cref="XROrigin.TrackablesParent"/> is snapped to the camera's
    /// parent every <c>onBeforeRender</c>, so in a standard rig it and the
    /// session frame used here are the same transform.
    /// </para>
    ///
    /// <para>
    /// <b>Update order.</b> <see cref="ARAnchorManager"/> refreshes anchor
    /// poses in its <c>Update</c> at <see cref="ARUpdateOrder.k_AnchorManager"/>;
    /// this runs right after it (<c>k_Anchor + 1</c>), before
    /// <see cref="PassthroughCameraProvider"/> (−50) samples the head,
    /// <see cref="DepthCapture"/> (−40) and <see cref="RoomScanner"/> (0)
    /// integrates. Depth arrives in <c>Application.onBeforeRender</c>
    /// (<see cref="AROcclusionManager"/> fires <c>frameReceived</c> there), so
    /// the view matrices of a frame are built with that frame's compensation
    /// already applied. RoomScanner fuses that frame on the next tick, after
    /// this component has decided whether the next tick is settling.
    /// </para>
    ///
    /// <para>
    /// <b>Settling.</b> A correction alone fixes the steady state; the frames
    /// around a change can still carry a pose from one side and an origin
    /// from the other. <see cref="IsSettling"/> is held for a short window
    /// after a large correction, an <see cref="XRInputSubsystem.trackingOriginUpdated"/>,
    /// an app resume or focus regain, and while the anchor or the AR session
    /// is not tracking. RoomScanner skips TSDF integration, colour feeding and
    /// keyframes while it is set; the scan keeps running.
    /// </para>
    ///
    /// <para>
    /// <b>Failure.</b> No XR Origin, no anchor subsystem (OpenXR feature
    /// "Android XR: Anchors" off) or a disabled <see cref="ARAnchorManager"/> is
    /// logged once per scan start and not retried until the next one. A
    /// transient failure (subsystem not running yet, a failed or throwing
    /// anchor creation, e.g. <c>SCENE_UNDERSTANDING_COARSE</c> not granted yet)
    /// is logged once and retried every <c>relockRetrySeconds</c> while the AR
    /// session tracks, without waiting for a scan start. Until a lock exists
    /// the scan runs uncompensated; the settle gating still applies. Taking a
    /// lock never moves anything: <c>W0</c> is the new anchor's present
    /// (already compensated) world pose.
    /// </para>
    ///
    /// <para>
    /// <b>Lost anchor.</b> While a locked anchor is not tracking, compensation
    /// is held and so is integration. Not-tracking time is counted per frame
    /// (clamped, and restarted on resume), so time asleep never counts. While
    /// the scan runs a locked anchor is never dropped for not tracking (unless
    /// <c>dropLostAnchorWhileScanning</c>): the scan stays paused, with a
    /// reminder every 15 s, rather than fuse depth against an origin that may
    /// not be relocalised. With the scan stopped, an anchor not tracking for
    /// <c>anchorLostSeconds</c> is dropped (the current correction is kept) and
    /// a new lock is taken at the present pose; a scan start does the same.
    /// An anchor removed by the provider is re-made at once.
    /// </para>
    ///
    /// <para>
    /// Added to the RoomScanner GameObject by <see cref="RoomScanner"/> when
    /// missing. While locked it owns the XR Origin root's pose: a host that
    /// moves the rig deliberately calls <see cref="Rebase"/> afterwards.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(ARUpdateOrder.k_Anchor + 1)]
    [DisallowMultipleComponent]
    public class ScanWorldLock : MonoBehaviour
    {
        [Header("Anchor")]
        [SerializeField, Tooltip(
            "Create an ARAnchor at the first scan start and move the XR Origin each frame so " +
            "it stays put in world space. Off: no anchor and no compensation; the settle " +
            "gating around resume and tracking-origin events still applies.")]
        private bool lockToAnchor = true;

        [SerializeField, Min(0f), Tooltip(
            "Horizontal distance (m) in front of the head at which the lock anchor is placed. " +
            "0 puts it at the head.")]
        private float anchorDistance = 0.5f;

        [SerializeField, Min(0.1f), Tooltip(
            "How long (s) a lock attempt waits for the anchor subsystem to be running before " +
            "giving up for this scan start.")]
        private float subsystemWaitSeconds = 3f;

        [SerializeField, Min(0f), Tooltip(
            "With the scan stopped (or at a scan start), drop an anchor that has not been tracking " +
            "for this long (s, app time; sleep does not count) and lock to a new one at the present " +
            "pose. While scanning, a locked anchor is kept and integration stays paused until it " +
            "tracks again, unless dropLostAnchorWhileScanning. 0 = never drop.")]
        private float anchorLostSeconds = 15f;

        [SerializeField, Tooltip(
            "Also drop a lost locked anchor (after anchorLostSeconds) while the scan is running, and " +
            "re-lock at the present pose so the scan continues. Off: the scan stays paused until the " +
            "anchor tracks again, so depth is never fused against an origin that may not be relocalised.")]
        private bool dropLostAnchorWhileScanning;

        [SerializeField, Min(0f), Tooltip(
            "After a transient lock failure, a removed anchor or a dropped one, try to lock again " +
            "every this many seconds (s) while the AR session tracks, without waiting for a scan " +
            "start. 0 = only at scan starts.")]
        private float relockRetrySeconds = 5f;

        [Header("Compensation")]
        [SerializeField, Min(0f), Tooltip(
            "Corrections at the anchor at or below this distance (m) and minCorrectionDegrees " +
            "leave the XR Origin untouched, so sub-millimetre anchor noise does not move the world.")]
        private float minCorrectionMetres = 0.001f;

        [SerializeField, Min(0f), Tooltip("Rotation part of the dead band above (degrees).")]
        private float minCorrectionDegrees = 0.05f;

        [Header("Settle")]
        [SerializeField, Min(0f), Tooltip(
            "Integration pause (s) after a large correction, a tracking-origin update, or once " +
            "the anchor / AR session is tracking again.")]
        private float settleSeconds = 1f;

        [SerializeField, Min(0f), Tooltip(
            "Integration pause (s) after the app resumes or regains focus (wake from headset " +
            "sleep). The runtime's tracking-origin change lands within about a second of it.")]
        private float resumeSettleSeconds = 2f;

        [SerializeField, Min(0f), Tooltip(
            "A correction larger than this (m, at the anchor) in one frame is a jump: it is " +
            "logged and starts a settle window.")]
        private float jumpThresholdMetres = 0.02f;

        [SerializeField, Min(0f), Tooltip("Rotation part of the jump threshold (degrees).")]
        private float jumpThresholdDegrees = 1f;

        // ─────────────────────────────────────────────────────────────
        //  Public state
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// True while TSDF integration, colour feeding and keyframe capture
        /// should be held because the tracking origin just changed or is not
        /// trustworthy. Updated once per frame before <see cref="RoomScanner"/>
        /// reads it; stable for the rest of the frame. Always false while this
        /// component is disabled (only its <c>Update</c> ends a window).
        /// </summary>
        public bool IsSettling => _settling && isActiveAndEnabled;

        /// <summary>Seconds until the current settle window ends; 0 when not settling.</summary>
        public float SettleRemaining => IsSettling ? Mathf.Max(0f, (float)(_settleUntil - Now)) : 0f;

        /// <summary>
        /// True when an anchor is held and its lock pose has been recorded, so
        /// the XR Origin is being compensated every frame.
        /// </summary>
        public bool IsLocked => _hasAnchor && _baselined && _anchor != null;

        /// <summary>The anchor the world is locked to, or null.</summary>
        public ARAnchor Anchor => _hasAnchor ? _anchor : null;

        /// <summary>World pose the anchor is held at (<c>W0</c>). Valid while <see cref="IsLocked"/>.</summary>
        public Pose LockedAnchorWorldPose => _w0;

        /// <summary>XR Origin root world pose when the lock was taken. Valid while <see cref="IsLocked"/>.</summary>
        public Pose OriginPoseAtLock => _originAtLock;

        /// <summary>Corrections applied to the XR Origin since the lock (above the dead band).</summary>
        public int CorrectionCount => _correctionCount;

        // ─────────────────────────────────────────────────────────────
        //  Private state
        // ─────────────────────────────────────────────────────────────

        // Reminder cadence while a lost anchor holds a running scan, and the
        // most one frame may add to the not-tracking time (a frame spanning a
        // pause or a hitch must not count as seconds of lost tracking).
        private const float HoldReminderSeconds = 15f;
        private const float MaxNotTrackingStep = 0.1f;

        private XROrigin _origin;
        private ARAnchorManager _anchorManager;
        private RoomScanner _scanner;
        private ARAnchor _anchor;
        private bool _hasAnchor;
        private bool _baselined;
        private bool _locking;
        private bool _active;
        private bool _warnedNoOrigin;

        // Lock retries: _lockUnavailable = a permanent failure this scan start
        // (no origin / subsystem / enabled manager), cleared by EnsureLocked.
        private bool _lockUnavailable;
        private int _lockFailures;
        private double _nextLockRetry;
        // Anchors dropped in a row without ever reporting Tracking (a provider
        // whose anchors never track would otherwise warn every cycle).
        private int _neverTrackedDrops;

        private Pose _w0 = Pose.identity;
        private Pose _anchorSessionAtLock = Pose.identity;
        private Pose _originAtLock = Pose.identity;
        private int _correctionCount;

        private bool _settling;
        private double _settleUntil;
        private double _settleStart;
        private string _settleReason;

        // Not-tracking time of the held anchor, accumulated per Update (app
        // time, not wall clock: the headset sleeping does not count).
        private bool _anchorNotTracking;
        private float _anchorNotTrackingSeconds;
        private float _nextHoldReminder;
        private bool _loggedAnchorNotTracking;
        private bool _loggedSessionNotTracking;
        private bool _sawPause;
        private bool _sawFocusLoss;

        // Set from XRInputSubsystem.trackingOriginUpdated; consumed in Update.
        private volatile bool _trackingOriginUpdated;
        private readonly List<XRInputSubsystem> _inputSubsystems = new();
        private static readonly List<XRInputSubsystem> s_InputScratch = new();
        private double _nextInputRefresh;

        private static double Now => Time.realtimeSinceStartupAsDouble;

        // ─────────────────────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────────────────────

        private void Start()
        {
            if (!XRRuntimeGuard.IsXRActive)
            {
                enabled = false;
                return;
            }
            RefreshInputSubscriptions();
        }

        private void OnDisable()
        {
            foreach (var s in _inputSubsystems)
                if (s != null) s.trackingOriginUpdated -= OnTrackingOriginUpdated;
            _inputSubsystems.Clear();
            _nextInputRefresh = 0;
            _trackingOriginUpdated = false;

            // Only Update ends a settle window; a window left open here would
            // hold RoomScanner's integration for good. IsSettling also reads
            // false while disabled, and settles cannot start until re-enabled.
            if (_settling)
            {
                _settling = false;
                Logger.Verbose($"WorldLock: disabled while settling ({_settleReason}) — integration no longer held");
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                _sawPause = true;
                return;
            }
            if (!_sawPause) return;
            _sawPause = false;
            _nextInputRefresh = 0;
            ResetAnchorLostTimer();
            if (_active) SettleFor(resumeSettleSeconds, "app resumed");
        }

        private void OnApplicationFocus(bool focus)
        {
            if (!focus)
            {
                _sawFocusLoss = true;
                return;
            }
            if (!_sawFocusLoss) return;
            _sawFocusLoss = false;
            ResetAnchorLostTimer();
            if (_active) SettleFor(resumeSettleSeconds, "focus regained");
        }

        private void Update()
        {
            double now = Now;
            if (now >= _nextInputRefresh)
            {
                _nextInputRefresh = now + 1.0;
                RefreshInputSubscriptions();
            }

            if (_trackingOriginUpdated)
            {
                _trackingOriginUpdated = false;
                if (_active) SettleFor(settleSeconds, "tracking origin updated");
            }

            if (_active) CheckSessionTracking();
            if (_hasAnchor) Compensate(now);
            MaybeRetryLock(now);

            if (_settling && now >= _settleUntil)
            {
                _settling = false;
                Logger.Info($"WorldLock: settled — integration resumed after {now - _settleStart:F1} s " +
                            $"({_settleReason}){DriftSummary()}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by <see cref="RoomScanner"/> at every scan start. Creates the
        /// lock anchor if this app session has none (first scan, or the last
        /// one was removed / lost / never made); otherwise keeps the existing
        /// lock, so a save followed by a start stays in the same world. A held
        /// anchor that has not been tracking for <c>anchorLostSeconds</c> is
        /// replaced by one locked at the present pose. Each call gives a lock
        /// that failed permanently one more attempt. Asynchronous and
        /// non-blocking: until the anchor exists the scan runs uncompensated,
        /// which is continuous because the lock is taken at the present pose.
        /// </summary>
        public void EnsureLocked()
        {
            if (!isActiveAndEnabled) return;
            _active = true;
            if (!lockToAnchor) return;

            // A fresh start: one warning per scan start for a failing lock.
            _lockUnavailable = false;
            _lockFailures = 0;
            _neverTrackedDrops = 0;
            if (_locking) return;

            if (_hasAnchor && _anchor != null)
            {
                if (!(anchorLostSeconds > 0f && _anchorNotTracking && _anchorNotTrackingSeconds >= anchorLostSeconds))
                    return;
                Logger.Warning($"WorldLock: anchor not tracking for {_anchorNotTrackingSeconds:F0} s at scan start — " +
                               "dropped; locking to a new anchor at the current pose (the current correction is kept)");
                DropAnchor();
            }
            _ = LockAsync();
        }

        /// <summary>
        /// Re-records the lock pose at the anchor's current world pose, so a
        /// deliberate move of the XR Origin (teleport, manual recentre) is kept
        /// instead of being compensated away. No-op when not locked.
        /// </summary>
        public void Rebase()
        {
            if (!IsLocked) return;
            TakeBaseline("rebased");
        }

        /// <summary>
        /// Starts or extends a settle window: <see cref="IsSettling"/> stays
        /// true for at least <paramref name="seconds"/> from now. Hosts can call
        /// this around anything they know disturbs tracking. No-op while this
        /// component is disabled.
        /// </summary>
        public void BeginSettle(float seconds, string reason)
        {
            ExtendSettle(seconds, string.IsNullOrEmpty(reason) ? "host request" : reason);
        }

        // ─────────────────────────────────────────────────────────────
        //  Locking
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Periodic re-lock after a transient failure, a removed anchor or a
        /// dropped one, so a scan started once (auto-start) does not stay
        /// uncompensated for the rest of the session. Continuous by
        /// construction: the new lock is taken at the present world pose.
        /// Only while the AR session is tracking (anchor creation fails or
        /// places the anchor badly otherwise).
        /// </summary>
        private void MaybeRetryLock(double now)
        {
            if (!_active || !lockToAnchor || _hasAnchor || _locking || _lockUnavailable) return;
            if (relockRetrySeconds <= 0f || now < _nextLockRetry) return;
            if (ARSession.state == ARSessionState.SessionInitializing) return;
            _ = LockAsync();
        }

        private async Task LockAsync()
        {
            _locking = true;
            try
            {
                if (!ResolveOrigin())
                {
                    _lockUnavailable = true;
                    return;
                }

                var manager = EnsureAnchorManager();
                if (manager == null)
                {
                    _lockUnavailable = true;
                    return;
                }

                // Added this frame: OnEnable has started the subsystem, but give
                // a provider that starts asynchronously a few frames. A null
                // subsystem stays null (SubsystemLifecycleManager only looks it
                // up in OnEnable), and a start failure disables the manager:
                // both are permanent for this scan start, a subsystem that is
                // merely not running yet is retried.
                double deadline = Now + subsystemWaitSeconds;
                while (manager.subsystem == null || !manager.subsystem.running)
                {
                    bool expired = Now >= deadline;
                    if (!manager.isActiveAndEnabled || (expired && manager.subsystem == null))
                    {
                        _lockUnavailable = true;
                        Logger.Warning("WorldLock: no XR anchor subsystem (enable the 'Android XR: Anchors' OpenXR " +
                                       "feature) — scanning without origin compensation until the next scan start; " +
                                       "integration still pauses around resume and tracking-origin events");
                        return;
                    }
                    if (expired)
                    {
                        LockFailed("the XR anchor subsystem is not running");
                        return;
                    }
                    await Awaitable.NextFrameAsync();
                    if (this == null || manager == null) return;
                }

                Pose request = AnchorRequestPose();
                Result<ARAnchor> result = await manager.TryAddAnchorAsync(request);
                if (this == null) return;

                if (!result.status.IsSuccess() || result.value == null)
                {
                    LockFailed($"anchor creation failed ({result.status}; SCENE_UNDERSTANDING_COARSE granted?)");
                    return;
                }

                int attempt = _lockFailures + 1;
                _lockFailures = 0;
                _anchor = result.value;
                _hasAnchor = true;
                _baselined = false;
                ResetAnchorLostTimer();
                string created = $"WorldLock: anchor {_anchor.trackableId} created at world " +
                                 $"({request.position.x:F2}, {request.position.y:F2}, {request.position.z:F2}), " +
                                 $"tracking={_anchor.trackingState}" + (attempt > 1 ? $" (attempt {attempt})" : "");
                if (_neverTrackedDrops >= 2) Logger.Verbose(created);
                else Logger.Info(created);

                if (_anchor.trackingState == TrackingState.Tracking)
                    TakeBaseline("locked");
            }
            catch (Exception e)
            {
                if (this != null) LockFailed($"anchor creation threw {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                _locking = false;
            }
        }

        /// <summary>
        /// A transient lock failure: warns on the first of a run (one per scan
        /// start), Verbose after that, and schedules the next retry.
        /// </summary>
        private void LockFailed(string why)
        {
            _lockFailures++;
            _nextLockRetry = Now + relockRetrySeconds;
            string retry = relockRetrySeconds > 0f
                ? $"retrying every {relockRetrySeconds:F0} s while the AR session tracks"
                : "retried at the next scan start";
            string msg = $"WorldLock: {why} — scanning without origin compensation, {retry}; integration still " +
                         "pauses around resume and tracking-origin events";
            if (_lockFailures == 1) Logger.Warning(msg);
            else Logger.Verbose($"{msg} (attempt {_lockFailures})");
        }

        private bool ResolveOrigin()
        {
            if (_origin == null)
                _origin = FindAnyObjectByType<XROrigin>();
            if (_origin != null) return true;
            if (!_warnedNoOrigin)
            {
                _warnedNoOrigin = true;
                Logger.Warning("WorldLock: no XROrigin in the scene — tracking-origin changes cannot be compensated");
            }
            return false;
        }

        /// <summary>
        /// The <see cref="ARAnchorManager"/> on the XR Origin GameObject
        /// (AR Foundation requires it there: it reads <see cref="XROrigin"/>
        /// with GetComponent), added when missing. An existing disabled one is
        /// the host's choice and is left alone.
        /// </summary>
        private ARAnchorManager EnsureAnchorManager()
        {
            var manager = _anchorManager != null ? _anchorManager : _origin.GetComponent<ARAnchorManager>();
            if (manager == null)
            {
                manager = _origin.gameObject.AddComponent<ARAnchorManager>();
                Logger.Info($"WorldLock: added ARAnchorManager to '{_origin.name}'");
            }
            else if (!manager.isActiveAndEnabled)
            {
                // Also the state AR Foundation leaves it in when the anchor
                // subsystem failed to start.
                Logger.Warning($"WorldLock: ARAnchorManager on '{_origin.name}' is disabled — scanning without " +
                               "origin compensation until the next scan start");
                return null;
            }

            _anchorManager = manager;
            return manager;
        }

        /// <summary>
        /// World pose for a new anchor: <see cref="anchorDistance"/> in front of
        /// the head on the session frame's horizontal plane, yaw-aligned with
        /// the head. Any pose works for the lock; near the user it is well
        /// observed.
        /// </summary>
        private Pose AnchorRequestPose()
        {
            Transform session = SessionSpace();
            Transform head = _origin.Camera != null ? _origin.Camera.transform
                : Camera.main != null ? Camera.main.transform : null;
            if (head == null) return new Pose(session.position, session.rotation);

            Vector3 up = session.up;
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(head.up, up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(session.forward, up);
            forward.Normalize();
            return new Pose(head.position + forward * anchorDistance, Quaternion.LookRotation(forward, up));
        }

        /// <summary>
        /// The session-space frame: the transform tracked poses are local to.
        /// <see cref="XROrigin.CameraFloorOffsetObject"/> first, matching
        /// <see cref="DepthCapture"/>; then the camera's parent; then the root.
        /// </summary>
        private Transform SessionSpace()
        {
            if (_origin.CameraFloorOffsetObject != null) return _origin.CameraFloorOffsetObject.transform;
            if (_origin.Camera != null && _origin.Camera.transform.parent != null)
                return _origin.Camera.transform.parent;
            return _origin.transform;
        }

        private Pose AnchorWorldPose()
        {
            Transform session = SessionSpace();
            Pose a = _anchor.pose;
            return new Pose(session.TransformPoint(a.position), session.rotation * a.rotation);
        }

        private void TakeBaseline(string what)
        {
            _w0 = AnchorWorldPose();
            _anchorSessionAtLock = _anchor.pose;
            Transform root = _origin.transform;
            _originAtLock = new Pose(root.position, root.rotation);
            _baselined = true;
            _neverTrackedDrops = 0;
            Logger.Info($"WorldLock: {what} to anchor {_anchor.trackableId} at world " +
                        $"({_w0.position.x:F2}, {_w0.position.y:F2}, {_w0.position.z:F2}) — XR Origin compensation " +
                        "active for this app session");
        }

        private void DropAnchor()
        {
            var anchor = _anchor;
            ClearAnchor();
            if (anchor == null || _anchorManager == null || !_anchorManager.enabled) return;
            try { _anchorManager.TryRemoveAnchor(anchor); }
            catch (Exception e) { Logger.Verbose($"WorldLock: removing the lost anchor threw {e.Message}"); }
        }

        /// <summary>
        /// Forgets the anchor (the XR Origin keeps its current correction) and
        /// lets <see cref="MaybeRetryLock"/> lock again right away.
        /// </summary>
        private void ClearAnchor()
        {
            _anchor = null;
            _hasAnchor = false;
            _baselined = false;
            ResetAnchorLostTimer();
            _lockFailures = 0;
            _nextLockRetry = 0;
        }

        /// <summary>
        /// Restarts the anchor's not-tracking time. Called on resume and focus
        /// regain: the anchor's state is stale or relocalising right after a
        /// wake, and that must not count against it.
        /// </summary>
        private void ResetAnchorLostTimer()
        {
            _anchorNotTracking = false;
            _anchorNotTrackingSeconds = 0f;
            _nextHoldReminder = 0f;
            _loggedAnchorNotTracking = false;
        }

        private string RelockPlan => relockRetrySeconds > 0f
            ? "locking to a new anchor at the current pose"
            : "a new anchor is locked at the current pose at the next scan start";

        private bool ScanRunning
        {
            get
            {
                if (_scanner == null) _scanner = GetComponent<RoomScanner>();
                if (_scanner == null) _scanner = RoomScanner.Instance;
                return _scanner != null && _scanner.IsScanning;
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Per-frame compensation
        // ─────────────────────────────────────────────────────────────

        private void Compensate(double now)
        {
            if (_anchor == null || _origin == null)
            {
                Logger.Warning($"WorldLock: {(_origin == null ? "XR Origin destroyed" : "anchor removed")} — origin " +
                               $"compensation stopped (the current correction is kept); {RelockPlan}");
                ClearAnchor();
                if (_origin == null) _anchorManager = null;
                return;
            }

            TrackingState state = _anchor.trackingState;
            if (state != TrackingState.Tracking)
            {
                // App time, clamped per frame: a frame spanning a pause (the
                // headset asleep) or a hitch adds at most MaxNotTrackingStep.
                if (!_anchorNotTracking)
                {
                    _anchorNotTracking = true;
                    _anchorNotTrackingSeconds = 0f;
                    _nextHoldReminder = HoldReminderSeconds;
                }
                else
                {
                    _anchorNotTrackingSeconds += Mathf.Min(Time.unscaledDeltaTime, MaxNotTrackingStep);
                }

                // Before the baseline nothing is compensated yet, so a fresh
                // anchor that has not reported tracking does not hold the scan.
                if (_baselined)
                {
                    if (!_loggedAnchorNotTracking)
                    {
                        _loggedAnchorNotTracking = true;
                        Logger.Info($"WorldLock: anchor tracking={state} — compensation held; integration paused " +
                                    "until it tracks again");
                    }
                    ExtendSettle(settleSeconds, $"anchor tracking={state}");
                }

                if (anchorLostSeconds > 0f && _anchorNotTrackingSeconds >= anchorLostSeconds)
                {
                    // A locked anchor is kept while the scan runs: dropping it
                    // would let depth be fused against whatever (possibly not
                    // relocalised) origin is current. An unlocked one holds
                    // nothing, so it is always replaced.
                    bool scanning = ScanRunning;
                    if (!_baselined || !scanning || dropLostAnchorWhileScanning)
                    {
                        string dropped = $"WorldLock: anchor not tracking for {_anchorNotTrackingSeconds:F0} s " +
                                         $"(tracking={state}{(_baselined ? "" : ", never tracked")}" +
                                         $"{(scanning ? ", scanning" : "")}) — dropped (the current correction is " +
                                         $"kept); {RelockPlan}";
                        if (!_baselined && ++_neverTrackedDrops >= 2) Logger.Verbose(dropped);
                        else Logger.Warning(dropped);
                        DropAnchor();
                        return;
                    }
                }

                if (_baselined && _anchorNotTrackingSeconds >= _nextHoldReminder && ScanRunning)
                {
                    _nextHoldReminder = _anchorNotTrackingSeconds + HoldReminderSeconds;
                    Logger.Info($"WorldLock: anchor still tracking={state} after {_anchorNotTrackingSeconds:F0} s — " +
                                "integration still paused; it resumes when the anchor tracks again" +
                                (anchorLostSeconds > 0f ? ", or stop and restart the scan to re-anchor at the current pose" : ""));
                }
                return;
            }

            if (_anchorNotTracking)
            {
                if (_loggedAnchorNotTracking)
                    Logger.Info($"WorldLock: anchor tracking again after {_anchorNotTrackingSeconds:F1} s");
                ResetAnchorLostTimer();
            }

            if (!_baselined)
            {
                TakeBaseline("locked");
                return;
            }

            Pose current = AnchorWorldPose();
            Quaternion q = Quaternion.Normalize(_w0.rotation * Quaternion.Inverse(current.rotation));
            float degrees = AngleDegrees(q);
            float metres = Vector3.Distance(_w0.position, current.position);
            if (metres <= minCorrectionMetres && degrees <= minCorrectionDegrees) return;

            Vector3 t = _w0.position - q * current.position;
            Transform root = _origin.transform;
            root.SetPositionAndRotation(q * root.position + t, Quaternion.Normalize(q * root.rotation));
            _correctionCount++;

            if (metres > jumpThresholdMetres || degrees > jumpThresholdDegrees)
            {
                string reason = _settling ? $"anchor jump, settling since {_settleReason}" : "anchor jump";
                ExtendSettle(settleSeconds, "anchor jump");
                Logger.Info($"WorldLock: origin moved {metres * 100f:F1} cm / {degrees:F2} deg ({reason}) — " +
                            $"compensated; integration paused {SettleRemaining:F1} s");
            }
        }

        /// <summary>
        /// Rotation angle of a unit quaternion in degrees, accurate for tiny
        /// angles (<see cref="Quaternion.Angle"/> reads 0 below ~0.16°, which is
        /// above the dead band).
        /// </summary>
        private static float AngleDegrees(Quaternion q)
        {
            float s = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            return 2f * Mathf.Atan2(s, Mathf.Abs(q.w)) * Mathf.Rad2Deg;
        }

        private string DriftSummary()
        {
            if (!IsLocked) return "";
            Pose a = _anchor.pose;
            float cm = Vector3.Distance(a.position, _anchorSessionAtLock.position) * 100f;
            float deg = AngleDegrees(Quaternion.Normalize(a.rotation * Quaternion.Inverse(_anchorSessionAtLock.rotation)));
            return $"; tracking origin has moved {cm:F1} cm / {deg:F2} deg at the anchor since lock, " +
                   $"{_correctionCount} corrections";
        }

        // ─────────────────────────────────────────────────────────────
        //  Settle triggers
        // ─────────────────────────────────────────────────────────────

        private void CheckSessionTracking()
        {
            // ARSession maps a None / Limited session tracking state to
            // SessionInitializing; every other state is either tracking or no
            // running session at all (which must not hold the scan forever).
            bool notTracking = ARSession.state == ARSessionState.SessionInitializing;
            if (notTracking)
            {
                string reason = $"AR session not tracking ({ARSession.notTrackingReason})";
                if (!_loggedSessionNotTracking)
                {
                    _loggedSessionNotTracking = true;
                    Logger.Info($"WorldLock: {reason} — integration paused until it tracks again");
                }
                ExtendSettle(settleSeconds, reason);
            }
            else if (_loggedSessionNotTracking)
            {
                _loggedSessionNotTracking = false;
                Logger.Info("WorldLock: AR session tracking again");
            }
        }

        /// <summary>Event-style trigger: one log line per event.</summary>
        private void SettleFor(float seconds, string reason)
        {
            if (!isActiveAndEnabled) return;
            ExtendSettle(seconds, reason);
            Logger.Info($"WorldLock: {reason} — integration paused {SettleRemaining:F1} s");
        }

        /// <summary>
        /// Opens or extends the settle window. A no-op while disabled: Unity
        /// can deliver pause / focus messages to a disabled behaviour, and only
        /// <c>Update</c> closes a window.
        /// </summary>
        private void ExtendSettle(float seconds, string reason)
        {
            if (!isActiveAndEnabled) return;
            double now = Now;
            if (!_settling)
            {
                _settling = true;
                _settleStart = now;
                _settleReason = reason;
                _settleUntil = now;
            }
            double until = now + Mathf.Max(0f, seconds);
            if (until > _settleUntil) _settleUntil = until;
        }

        private void RefreshInputSubscriptions()
        {
            SubsystemManager.GetSubsystems(s_InputScratch);

            for (int i = _inputSubsystems.Count - 1; i >= 0; i--)
            {
                var s = _inputSubsystems[i];
                if (s != null && s_InputScratch.Contains(s)) continue;
                if (s != null) s.trackingOriginUpdated -= OnTrackingOriginUpdated;
                _inputSubsystems.RemoveAt(i);
            }

            foreach (var s in s_InputScratch)
            {
                if (s == null || _inputSubsystems.Contains(s)) continue;
                s.trackingOriginUpdated += OnTrackingOriginUpdated;
                _inputSubsystems.Add(s);
            }
            s_InputScratch.Clear();
        }

        private void OnTrackingOriginUpdated(XRInputSubsystem subsystem) => _trackingOriginUpdated = true;
    }
}
