using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Genesis.RoomScan.UI
{
    /// <summary>
    /// Extends <see cref="WorldDocumentRaycaster"/> so that VR controller rays
    /// are used for raycasting against world-space UI Toolkit panels.
    ///
    /// The ray is taken from <see cref="TrackedDeviceEventData.rayPoints"/> when
    /// the event system is driven by XR Interaction Toolkit, and otherwise from
    /// the active <see cref="ControllerRayDriver"/>.
    ///
    /// When neither is available (e.g. in-editor with a mouse), falls back to
    /// the default screen-to-camera-ray conversion.
    ///
    /// Add this component alongside (or instead of) the auto-created
    /// <c>WorldDocumentRaycaster</c> on the EventSystem GameObject.
    /// </summary>
    [AddComponentMenu("UI Toolkit/VR Document Raycaster (Quest)")]
    public class VRDocumentRaycaster : WorldDocumentRaycaster
    {
        [SerializeField, Tooltip("Max ray distance for UI interaction (meters)")]
        private float maxRayDistance = 5f;

        [SerializeField, Tooltip("Physics layers to raycast against")]
        private LayerMask interactionLayers = ~0;

        protected override bool GetWorldRay(
            PointerEventData eventData,
            out Ray worldRay,
            out float maxDistance,
            out int layerMask)
        {
            maxDistance = maxRayDistance;
            layerMask = interactionLayers.value;

            // XRI-driven pointer: the interactor already computed the ray.
            if (eventData is TrackedDeviceEventData tracked &&
                tracked.rayPoints != null && tracked.rayPoints.Count >= 2)
            {
                var start = tracked.rayPoints[0];
                var dir = tracked.rayPoints[1] - start;
                if (dir.sqrMagnitude > 0.000001f)
                {
                    worldRay = new Ray(start, dir.normalized);
                    return true;
                }
            }

            // Otherwise use the laser this package drives itself.
            var driver = ControllerRayDriver.Active;
            if (driver != null && driver.TryGetRay(out worldRay))
            {
                maxDistance = driver.MaxLength;
                return true;
            }

            return base.GetWorldRay(eventData, out worldRay, out maxDistance, out layerMask);
        }
    }
}
