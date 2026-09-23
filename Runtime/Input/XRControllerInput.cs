using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Controller buttons this package can bind actions to.
    ///
    /// Replaces <c>OVRInput.Button</c>. Where OVRInput collapsed both
    /// controllers into one enum (One/Two = right A/B, Three/Four = left X/Y),
    /// these names say which hand they belong to.
    /// </summary>
    public enum ScanButton
    {
        None = 0,

        /// <summary>Right controller "A".</summary>
        RightPrimary,
        /// <summary>Right controller "B".</summary>
        RightSecondary,
        /// <summary>Left controller "X".</summary>
        LeftPrimary,
        /// <summary>Left controller "Y".</summary>
        LeftSecondary,

        LeftThumbstickClick,
        RightThumbstickClick,

        LeftTrigger,
        RightTrigger,
        LeftGrip,
        RightGrip,

        /// <summary>
        /// Left controller menu (≡). The right-hand system button is reserved by
        /// the runtime and is deliberately not exposed here.
        /// </summary>
        LeftMenu,
    }

    /// <summary>
    /// Edge-triggered controller button polling over <see cref="InputDevices"/>
    /// (OpenXR), replacing <c>OVRInput.GetDown</c>.
    ///
    /// <see cref="InputDevices"/> only reports the current level, so this caches
    /// last frame's state and derives the rising edge. State is refreshed lazily
    /// on the first query of each frame, so callers do not need to drive it.
    /// </summary>
    public static class XRControllerInput
    {
        private static readonly List<InputDevice> s_Scratch = new();

        private static InputDevice s_Left;
        private static InputDevice s_Right;

        private static int s_StateFrame = -1;
        private static uint s_Current;
        private static uint s_Previous;

        /// <summary>True on the frame <paramref name="button"/> is first pressed.</summary>
        public static bool GetDown(ScanButton button)
        {
            if (button == ScanButton.None) return false;
            EnsureState();
            uint bit = 1u << (int)button;
            return (s_Current & bit) != 0 && (s_Previous & bit) == 0;
        }

        /// <summary>True while <paramref name="button"/> is held.</summary>
        public static bool Get(ScanButton button)
        {
            if (button == ScanButton.None) return false;
            EnsureState();
            return (s_Current & (1u << (int)button)) != 0;
        }

        /// <summary>True on the frame <paramref name="button"/> is released.</summary>
        public static bool GetUp(ScanButton button)
        {
            if (button == ScanButton.None) return false;
            EnsureState();
            uint bit = 1u << (int)button;
            return (s_Current & bit) == 0 && (s_Previous & bit) != 0;
        }

        private static void EnsureState()
        {
            int frame = Time.frameCount;
            if (frame == s_StateFrame) return;

            // A skipped frame means the "previous" level we hold is stale, but the
            // rising edge we would synthesise from it is the one the caller missed
            // anyway, so carrying it forward is the correct behaviour.
            s_StateFrame = frame;
            s_Previous = s_Current;
            s_Current = Sample();
        }

        private static uint Sample()
        {
            RefreshDevices();

            uint mask = 0;
            Set(ref mask, ScanButton.RightPrimary, s_Right, CommonUsages.primaryButton);
            Set(ref mask, ScanButton.RightSecondary, s_Right, CommonUsages.secondaryButton);
            Set(ref mask, ScanButton.LeftPrimary, s_Left, CommonUsages.primaryButton);
            Set(ref mask, ScanButton.LeftSecondary, s_Left, CommonUsages.secondaryButton);

            Set(ref mask, ScanButton.LeftThumbstickClick, s_Left, CommonUsages.primary2DAxisClick);
            Set(ref mask, ScanButton.RightThumbstickClick, s_Right, CommonUsages.primary2DAxisClick);

            Set(ref mask, ScanButton.LeftTrigger, s_Left, CommonUsages.triggerButton);
            Set(ref mask, ScanButton.RightTrigger, s_Right, CommonUsages.triggerButton);
            Set(ref mask, ScanButton.LeftGrip, s_Left, CommonUsages.gripButton);
            Set(ref mask, ScanButton.RightGrip, s_Right, CommonUsages.gripButton);

            Set(ref mask, ScanButton.LeftMenu, s_Left, CommonUsages.menuButton);
            return mask;
        }

        private static void Set(ref uint mask, ScanButton button, InputDevice device,
                                InputFeatureUsage<bool> usage)
        {
            if (device.isValid && device.TryGetFeatureValue(usage, out bool pressed) && pressed)
                mask |= 1u << (int)button;
        }

        private static void RefreshDevices()
        {
            if (!s_Left.isValid)
                s_Left = FindController(InputDeviceCharacteristics.Left);
            if (!s_Right.isValid)
                s_Right = FindController(InputDeviceCharacteristics.Right);
        }

        private static InputDevice FindController(InputDeviceCharacteristics hand)
        {
            s_Scratch.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Controller | hand,
                s_Scratch);
            return s_Scratch.Count > 0 ? s_Scratch[0] : default;
        }
    }
}
