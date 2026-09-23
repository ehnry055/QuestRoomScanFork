using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR;

namespace Genesis.RoomScan.UI
{
    /// <summary>
    /// Picks the active VR controller (or tracked hand), keeps a ray transform
    /// pointing along its aim pose, and draws a laser + cursor dot.
    /// Place on the EventSystem GameObject.
    ///
    /// The live ray is published through <see cref="Active"/> so
    /// <see cref="VRDocumentRaycaster"/> can raycast along it.
    /// </summary>
    public class ControllerRayDriver : MonoBehaviour
    {
        /// <summary>Most recently enabled driver, or null. Used by the UI raycaster.</summary>
        public static ControllerRayDriver Active { get; private set; }

        // OpenXR exposes the aim ("pointer") pose separately from the grip pose.
        // These are the standard custom usages for it; not in CommonUsages.
        private static readonly InputFeatureUsage<Vector3> k_PointerPosition = new("PointerPosition");
        private static readonly InputFeatureUsage<Quaternion> k_PointerRotation = new("PointerRotation");

        [Header("Ray")]
        [SerializeField, Tooltip("Forward offset from controller origin (meters)")]
        private float rayStartOffset = 0.05f;
        [SerializeField] private float maxLength = 5f;

        [Header("Laser Visual")]
        [SerializeField] private float beamWidth = 0.002f;
        [SerializeField] private Color idleColor = new(1f, 1f, 1f, 0.15f);
        [SerializeField] private Color hoverColor = new(0f, 0.8f, 1f, 0.7f);

        [Header("Cursor Dot")]
        [SerializeField] private float cursorRadius = 0.006f;
        [SerializeField] private Color cursorColor = new(1f, 1f, 1f, 0.9f);

        [Header("Rendering")]
        [SerializeField] internal Shader overlayShader;

        private Transform _rayHelper;
        private LineRenderer _line;
        private GameObject _cursor;
        private MeshRenderer _cursorRenderer;
        private XROrigin _origin;

        private readonly List<InputDevice> _scratch = new();
        private InputDevice _activeDevice;
        private bool _hasRay;

        /// <summary>Transform following the active controller's aim pose.</summary>
        public Transform RayTransform => _rayHelper;

        /// <summary>
        /// Current world-space aim ray. <paramref name="ray"/> is only valid when
        /// this returns true (no tracked device yet, or tracking lost).
        /// </summary>
        public bool TryGetRay(out Ray ray)
        {
            if (!_hasRay || _rayHelper == null)
            {
                ray = default;
                return false;
            }
            ray = new Ray(_rayHelper.position, _rayHelper.forward);
            return true;
        }

        /// <summary>Max distance the ray interacts over, in meters.</summary>
        public float MaxLength => maxLength;

        private void Awake()
        {
            _rayHelper = new GameObject("ControllerRayHelper").transform;
            _rayHelper.SetParent(transform, false);

            _origin = FindAnyObjectByType<XROrigin>();

            SetupLineRenderer();
            SetupCursor();
        }

        private void OnEnable() => Active = this;

        private void OnDisable()
        {
            if (Active == this) Active = null;
        }

        private void Update()
        {
            _activeDevice = ChooseBestDevice(_activeDevice);
            UpdateRayOrigin();
        }

        private void LateUpdate()
        {
            DrawLaser();
        }

        private void OnDestroy()
        {
            if (_rayHelper != null) Destroy(_rayHelper.gameObject);
            if (_cursor != null) Destroy(_cursor);
        }

        // ─── Device Selection ───

        /// <summary>
        /// Keeps the current device while it stays valid, otherwise prefers the
        /// right hand, then the left. Switches to whichever device the user is
        /// actively pressing, mirroring the old controller-swap behaviour.
        /// </summary>
        private InputDevice ChooseBestDevice(InputDevice previous)
        {
            var right = FindDevice(InputDeviceCharacteristics.Right);
            var left = FindDevice(InputDeviceCharacteristics.Left);

            var device = previous;
            if (!device.isValid)
                device = right.isValid ? right : left;

            // Hand over to the other device as soon as it is used.
            if (right.isValid && !Same(device, right) && IsBeingUsed(right)) device = right;
            else if (left.isValid && !Same(device, left) && IsBeingUsed(left)) device = left;

            return device;
        }

        private static bool Same(InputDevice a, InputDevice b) => a.isValid && b.isValid && a == b;

        private static bool IsBeingUsed(InputDevice d)
        {
            return (d.TryGetFeatureValue(CommonUsages.triggerButton, out bool t) && t)
                || (d.TryGetFeatureValue(CommonUsages.gripButton, out bool g) && g)
                || (d.TryGetFeatureValue(CommonUsages.primaryButton, out bool p) && p)
                || (d.TryGetFeatureValue(CommonUsages.secondaryButton, out bool s) && s);
        }

        private InputDevice FindDevice(InputDeviceCharacteristics hand)
        {
            // Controllers first; fall back to a tracked hand on the same side.
            _scratch.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Controller | hand,
                _scratch);
            if (_scratch.Count > 0) return _scratch[0];

            _scratch.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.HandTracking | hand, _scratch);
            return _scratch.Count > 0 ? _scratch[0] : default;
        }

        // ─── Ray Transform ───

        private void UpdateRayOrigin()
        {
            _hasRay = false;
            if (!_activeDevice.isValid) return;

            // Prefer the aim pose; fall back to the grip pose on runtimes that
            // do not surface a separate pointer pose (e.g. some hand profiles).
            if (!_activeDevice.TryGetFeatureValue(k_PointerPosition, out Vector3 localPos) ||
                !_activeDevice.TryGetFeatureValue(k_PointerRotation, out Quaternion localRot))
            {
                if (!_activeDevice.TryGetFeatureValue(CommonUsages.devicePosition, out localPos) ||
                    !_activeDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out localRot))
                    return;
            }

            // Device poses are in tracking space; lift them into world space
            // through the rig. Without a rig they are already world-space.
            var rig = _origin != null ? _origin.transform : null;
            if (rig == null)
            {
                _origin = FindAnyObjectByType<XROrigin>();
                rig = _origin != null ? _origin.transform : null;
            }

            if (rig != null)
            {
                _rayHelper.SetPositionAndRotation(
                    rig.TransformPoint(localPos),
                    rig.rotation * localRot);
            }
            else
            {
                _rayHelper.SetPositionAndRotation(localPos, localRot);
            }

            _hasRay = true;
        }

        // ─── Laser Visual ───

        private void SetupLineRenderer()
        {
            _line = gameObject.AddComponent<LineRenderer>();
            _line.positionCount = 2;
            _line.startWidth = beamWidth;
            _line.endWidth = beamWidth * 0.5f;
            _line.material = new Material(overlayShader);
            _line.startColor = _line.endColor = idleColor;
            _line.useWorldSpace = true;
            _line.receiveShadows = false;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void SetupCursor()
        {
            _cursor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _cursor.name = "RayCursor";
            _cursor.transform.localScale = Vector3.one * (cursorRadius * 2f);

            // Remove the collider so it doesn't interfere with raycasts
            var col = _cursor.GetComponent<Collider>();
            if (col != null) Destroy(col);

            _cursorRenderer = _cursor.GetComponent<MeshRenderer>();
            _cursorRenderer.material = new Material(overlayShader);
            _cursorRenderer.material.color = cursorColor;
            _cursorRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _cursorRenderer.receiveShadows = false;

            _cursor.SetActive(false);
        }

        private void DrawLaser()
        {
            if (_rayHelper == null || _line == null) return;

            // Hide the beam entirely while no device is tracked.
            if (!_hasRay)
            {
                _line.enabled = false;
                if (_cursor != null) _cursor.SetActive(false);
                return;
            }
            _line.enabled = true;

            var origin = _rayHelper.position;
            var dir = _rayHelper.forward;
            var start = origin + dir * rayStartOffset;
            var end = start + dir * maxLength;
            bool hoveringUI = false;

            // Only highlight when hitting a world-space UI Toolkit panel collider
            if (Physics.Raycast(origin, dir, out var hit, maxLength + rayStartOffset))
            {
                end = hit.point;

                // Check if we hit a UIDocument's auto-generated panel collider
                var uiDoc = hit.collider.GetComponentInParent<UIDocument>();
                hoveringUI = uiDoc != null;
            }

            _line.SetPosition(0, start);
            _line.SetPosition(1, end);

            var color = hoveringUI ? hoverColor : idleColor;
            _line.startColor = _line.endColor = color;

            // Position cursor dot at the end of the ray
            if (_cursor != null)
            {
                bool showCursor = hoveringUI;
                _cursor.SetActive(showCursor);
                if (showCursor)
                {
                    _cursor.transform.position = end;
                    _cursor.transform.LookAt(_rayHelper);
                    _cursorRenderer.material.color = hoverColor;
                }
            }
        }
    }
}
