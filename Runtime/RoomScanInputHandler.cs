using System;
using System.Collections.Generic;
using UnityEngine;

namespace Genesis.RoomScan
{
    /// <summary>
    /// All actions that can be bound to controller buttons.
    /// </summary>
    public enum ScanAction
    {
        None,
        ToggleScanning,
        FreezeInView,
        UnfreezeInView,
        CycleRenderMode,
        ClearAllData,
        ExportPointCloud,
        StartServerTraining,
        ToggleDebugMenu,
        SaveScan,
        LoadScan,
        ToggleFreezeTint,
    }

    /// <summary>
    /// Maps a single controller button to a <see cref="ScanAction"/>.
    /// Disable individual bindings by setting <see cref="enabled"/> to false,
    /// or remove/replace the entire <see cref="RoomScanInputHandler"/> component.
    /// </summary>
    [Serializable]
    public class ScanInputBinding
    {
        public ScanAction action = ScanAction.None;
        public ScanButton button = ScanButton.None;
        public bool enabled = true;
    }

    /// <summary>
    /// Optional component that polls controller input each frame and calls the
    /// corresponding <see cref="RoomScanner"/> public API methods.
    ///
    /// Clients can:
    ///   - Edit bindings in the Inspector or at runtime via <see cref="Bindings"/>.
    ///   - Disable individual bindings or the entire component.
    ///   - Remove this component entirely and call RoomScanner APIs directly.
    ///   - Add/remove bindings at runtime via <see cref="AddBinding"/>/<see cref="RemoveBinding"/>.
    /// </summary>
    public class RoomScanInputHandler : MonoBehaviour
    {
        [SerializeField, Tooltip("Controller button → action mappings. Editable at runtime.")]
        private List<ScanInputBinding> bindings = new()
        {
            // Left thumbstick click. ScanButton.LeftMenu (left ≡) is left
            // free so host apps can wire it to their own pause menu, the
            // usual convention for XR titles; the right-hand system button
            // belongs to the OS. Galaxy XR controllers use the Oculus Touch
            // interaction profile — confirm these bindings on the device.
            new() { action = ScanAction.ToggleDebugMenu,     button = ScanButton.LeftThumbstickClick, enabled = true },
            new() { action = ScanAction.FreezeInView,        button = ScanButton.RightPrimary,   enabled = true },
            new() { action = ScanAction.UnfreezeInView,      button = ScanButton.RightSecondary,   enabled = true },
            new() { action = ScanAction.CycleRenderMode,     button = ScanButton.LeftPrimary, enabled = true },
            new() { action = ScanAction.StartServerTraining,  button = ScanButton.LeftSecondary,  enabled = false },
            new() { action = ScanAction.ToggleFreezeTint,      button = ScanButton.None,  enabled = false },
        };

        /// <summary>
        /// The live bindings list. Mutate freely at runtime.
        /// </summary>
        public List<ScanInputBinding> Bindings => bindings;

        /// <summary>
        /// Convenience: add a new binding at runtime.
        /// </summary>
        public void AddBinding(ScanAction action, ScanButton button)
        {
            bindings.Add(new ScanInputBinding { action = action, button = button, enabled = true });
        }

        /// <summary>
        /// Remove all bindings for a given action.
        /// </summary>
        public void RemoveBindingsForAction(ScanAction action)
        {
            bindings.RemoveAll(b => b.action == action);
        }

        /// <summary>
        /// Remove all bindings for a given button.
        /// </summary>
        public void RemoveBindingsForButton(ScanButton button)
        {
            bindings.RemoveAll(b => b.button == button);
        }

        /// <summary>
        /// Clear all bindings. After this, no controller input will trigger any action.
        /// </summary>
        public void ClearAllBindings()
        {
            bindings.Clear();
        }

        private void Update()
        {
            var scanner = RoomScanner.Instance;
            if (scanner == null) return;

            for (int i = 0; i < bindings.Count; i++)
            {
                var b = bindings[i];
                if (!b.enabled || b.action == ScanAction.None || b.button == ScanButton.None)
                    continue;

                if (!XRControllerInput.GetDown(b.button))
                    continue;

                ExecuteAction(scanner, b.action);
            }
        }

        private static void ExecuteAction(RoomScanner scanner, ScanAction action)
        {
            Logger.Info($"InputHandler: {action}");
            switch (action)
            {
                case ScanAction.ToggleScanning:
                    scanner.ToggleScanning();
                    break;
                case ScanAction.FreezeInView:
                    scanner.FreezeInView();
                    break;
                case ScanAction.UnfreezeInView:
                    scanner.UnfreezeInView();
                    break;
                case ScanAction.CycleRenderMode:
                    scanner.CycleRenderMode();
                    break;
                case ScanAction.ClearAllData:
                    scanner.ClearAllDataAsync();
                    break;
                case ScanAction.ExportPointCloud:
                    _ = scanner.ExportPointCloudAsync();
                    break;
                case ScanAction.StartServerTraining:
                    scanner.StartServerTraining();
                    break;
                case ScanAction.ToggleDebugMenu:
                    scanner.ToggleDebugMenu();
                    break;
                case ScanAction.SaveScan:
                    _ = scanner.SaveScanAsync();
                    break;
                case ScanAction.LoadScan:
                    if (scanner.DebugMenu != null)
                    {
                        scanner.DebugMenu.Show();
                        scanner.DebugMenu.ShowSavedScans();
                    }
                    break;
                case ScanAction.ToggleFreezeTint:
                    scanner.ShowFreezeTint = !scanner.ShowFreezeTint;
                    break;
            }
        }
    }
}
