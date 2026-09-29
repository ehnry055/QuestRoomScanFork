// Runtime XR availability check.
//
// Headset-only components (DepthCapture, AROcclusionManager toggling, etc.)
// blow up when entered in the Editor without an XR provider. Production
// builds always have an active loader (OpenXR on Galaxy XR / Android XR), so
// the guard is only meaningful in Edit mode + standalone-without-headset cases.
//
// Kept in one place so every headset-only component can early-out with the
// same wording instead of each one re-implementing the check.

using UnityEngine.XR.Management;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Static helper for "is an XR loader actually running right now?" checks.
    /// Headset-only components should short-circuit when this returns false.
    /// </summary>
    public static class XRRuntimeGuard
    {
        /// <summary>
        /// True when XR Plug-in Management has an active loader for the current
        /// runtime (OpenXR on Galaxy XR / Android XR). False in the
        /// Editor without an XR provider, or on platforms where no loader was
        /// configured for the current build target.
        /// </summary>
        public static bool IsXRActive
        {
            get
            {
                var settings = XRGeneralSettings.Instance;
                if (settings == null) return false;
                var manager = settings.Manager;
                if (manager == null) return false;
                return manager.activeLoader != null;
            }
        }

        /// <summary>
        /// Standardized one-liner that headset-only components can include in
        /// their early-out log so users see a consistent message instead of a
        /// stream of low-level subsystem null-refs.
        /// </summary>
        public const string EditorDisabledMessage =
            "No active XR loader (Editor without an Android XR runtime). " +
            "Build to a Galaxy XR headset to scan.";
    }
}
