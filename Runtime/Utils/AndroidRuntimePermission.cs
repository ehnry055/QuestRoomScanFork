using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Genesis.RoomScan
{
    /// <summary>
    /// The one place this package asks Android for a runtime permission.
    /// <para>
    /// Android drops a second <c>RequestUserPermission</c> while another
    /// dialog is still up — no UI, no callback — so every request here is
    /// <b>serialised</b>: a caller's dialog opens only after every earlier
    /// one has been answered, and two callers asking for the same permission
    /// share one pending task. Hosts may front-load requests at boot for
    /// their own UX; <see cref="RoomScanner.StartScanningAsync"/> asks again
    /// for whatever is still missing, which is free once granted.
    /// </para>
    /// <para>
    /// The ids are Android XR's (Samsung Galaxy XR). Every one of them is a
    /// runtime ("dangerous") permission, and a request for one the merged
    /// manifest does not declare is denied at once with no dialog. The
    /// Android XR OpenXR build step declares the scene-understanding and
    /// hand-tracking ones; Unity declares <see cref="Camera"/> when a script
    /// uses <c>WebCamTexture</c>.
    /// </para>
    /// Always resolves granted outside Android device builds. Main thread only.
    /// </summary>
    internal static class AndroidRuntimePermission
    {
        /// <summary>Fine scene understanding: the environment depth
        /// (<c>XR_ANDROID_depth_texture</c>) the scan is built from.
        /// Required; without it there is no scan.</summary>
        public const string Scene = "android.permission.SCENE_UNDERSTANDING_FINE";

        /// <summary>Coarse scene understanding: anchors (and anchor
        /// persistence), planes and light estimation. Optional.</summary>
        public const string Anchors = "android.permission.SCENE_UNDERSTANDING_COARSE";

        /// <summary>The standard Android camera permission, for the
        /// world-facing RGB camera (Camera2 id 0) that
        /// <see cref="PassthroughCameraProvider"/> opens. Optional: the scan
        /// runs depth-only without it.</summary>
        public const string Camera = "android.permission.CAMERA";

        /// <summary>Android XR hand tracking (XR Hands joints, hand
        /// interaction profile). Optional.</summary>
        public const string HandTracking = "android.permission.HAND_TRACKING";

        public static bool Has(string permissionId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(permissionId);
#else
            return true;
#endif
        }

        /// <summary>
        /// True when a request for <paramref name="permissionId"/> made
        /// through this class was answered "deny" earlier in this process
        /// and the permission is still not granted. Lets a second caller
        /// (e.g. a camera provider starting after the scan-start requests)
        /// skip asking again straight after the user said no. Always false
        /// outside Android device builds.
        /// </summary>
        public static bool DeniedThisSession(string permissionId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return _denied.Contains(permissionId)
                && !Permission.HasUserAuthorizedPermission(permissionId);
#else
            return false;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Unity requires the PermissionCallbacks instance to stay reachable
        // until the user answers. A local that goes out of scope can be
        // collected before the OS dialog appears, which presents as "no
        // dialog, immediately denied".
        static PermissionCallbacks _heldCallbacks;

        // Tail of the dialog queue: the next request awaits this before it
        // calls RequestUserPermission.
        static Task _chain = Task.CompletedTask;

        // In-flight request per permission id, so concurrent callers for the
        // same permission share one dialog.
        static readonly Dictionary<string, Task<bool>> _pending = new();

        // Ids the user denied this process (see DeniedThisSession).
        static readonly HashSet<string> _denied = new();
#endif

        public static Task<bool> RequestAsync(string permissionId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(permissionId))
                return Task.FromResult(true);
            if (_pending.TryGetValue(permissionId, out var inFlight))
                return inFlight;

            var tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[permissionId] = tcs.Task;

            Task previous = _chain;
            _chain = tcs.Task;
            _ = ShowAfterAsync(previous, permissionId, tcs);
            return tcs.Task;
#else
            return Task.FromResult(true);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static async Task ShowAfterAsync(
            Task previous, string permissionId, TaskCompletionSource<bool> tcs)
        {
            try { await previous; } catch { /* an earlier request's outcome is its own */ }

            // An earlier dialog (or the OS) may have granted this meanwhile.
            if (Permission.HasUserAuthorizedPermission(permissionId))
            {
                Finish(permissionId, tcs, true);
                return;
            }

            var callbacks = new PermissionCallbacks();
            _heldCallbacks = callbacks;
            callbacks.PermissionGranted += _ => Finish(permissionId, tcs, true);
            callbacks.PermissionDenied += _ => Finish(permissionId, tcs, false);
            // Do not subscribe to PermissionDeniedAndDontAskAgain: Unity
            // documents it as unreliable and then skips PermissionDenied.
            try
            {
                Logger.Info($"Requesting permission: {permissionId}");
                Permission.RequestUserPermission(permissionId, callbacks);
            }
            catch (Exception ex)
            {
                Logger.Error($"Permission request failed ({permissionId}): {ex.Message}");
                Finish(permissionId, tcs, false);
            }
        }

        static void Finish(string permissionId, TaskCompletionSource<bool> tcs, bool granted)
        {
            if (!tcs.TrySetResult(granted)) return;
            _pending.Remove(permissionId);
            _heldCallbacks = null;
            if (granted) _denied.Remove(permissionId);
            else _denied.Add(permissionId);
            Logger.Info($"Permission {(granted ? "granted" : "denied")}: {permissionId}");
        }
#endif
    }
}
