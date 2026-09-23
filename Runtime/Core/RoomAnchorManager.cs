using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Galaxy XR-compatible room anchor manager.
    ///
    /// The core TSDF + mesh reconstruction pipeline does not depend on MRUK room
    /// discovery. This shim preserves the public API and keeps the scan flow running
    /// without the later MRUK scene and room-understanding steps.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomAnchorManager : MonoBehaviour, IRoomScanModule
    {
        public string ModuleName => "Room Anchor";
        public void OnModuleInitialize(RoomScanner scanner) { }

        public static RoomAnchorManager Instance { get; private set; }

        public event Action RoomReady;

        public bool IsRoomLoaded { get; private set; }
        public bool HasSceneRooms { get; private set; }

        private readonly TaskCompletionSource<bool> _readyTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _readySignaled;
        private Transform _anchorTransform;

        private void Awake()
        {
            Instance = this;
        }

        private IEnumerator Start()
        {
            MarkRoomReady(hasRooms: false);
            yield break;
        }

        public Task WaitUntilRoomReadyAsync()
        {
            if (IsRoomLoaded) return Task.CompletedTask;
            return _readyTcs.Task;
        }

        public Task<bool> ReloadSceneFromDeviceAsync() => Task.FromResult(HasSceneRooms);
        public Task<bool> RequestSpaceSetupAndReloadAsync() => Task.FromResult(HasSceneRooms);

        public Matrix4x4 GetRoomLocalToWorldForPersistence() =>
            _anchorTransform != null ? _anchorTransform.localToWorldMatrix : Matrix4x4.identity;

        public static Matrix4x4 ComputeRelocationMatrix(Matrix4x4 anchorNow, Matrix4x4 anchorAtSave)
        {
            return anchorNow * anchorAtSave.inverse;
        }

        public Matrix4x4 ComputeRelocationMatrix(Matrix4x4 anchorAtSave)
        {
            return ComputeRelocationMatrix(
                _anchorTransform != null ? _anchorTransform.localToWorldMatrix : Matrix4x4.identity,
                anchorAtSave);
        }

        public Matrix4x4 SpatialAnchorMatrix => Matrix4x4.identity;
        public bool HasSpatialAnchor => false;
        public Transform SpatialAnchorTransform => null;
        public Guid SpatialAnchorUuid => Guid.Empty;

        public Task<(Guid uuid, Matrix4x4 matrix)?> CreateAndSaveSpatialAnchorAsync(Vector3 position, Quaternion rotation)
        {
            var go = new GameObject("[GalaxyAnchor]");
            go.transform.SetPositionAndRotation(position, rotation);
            _anchorTransform = go.transform;
            return Task.FromResult<(Guid uuid, Matrix4x4 matrix)?>((Guid.Empty, Matrix4x4.identity));
        }

        public Task<Matrix4x4?> LoadSpatialAnchorAsync(Guid uuid)
        {
            return Task.FromResult<Matrix4x4?>(Matrix4x4.identity);
        }

        public void UnloadActiveSpatialAnchor() { }
        public Task<bool> EraseSpatialAnchorAsync(Guid uuid) => Task.FromResult(true);

        private void MarkRoomReady(bool hasRooms)
        {
            HasSceneRooms = hasRooms;
            IsRoomLoaded = true;
            if (_readySignaled) return;
            _readySignaled = true;
            _readyTcs.TrySetResult(true);
            RoomReady?.Invoke();
        }

        private void OnDestroy()
        {
            _readyTcs.TrySetResult(false);
            if (Instance == this) Instance = null;
        }
    }
}
