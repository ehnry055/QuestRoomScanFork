using System;
using System.Collections.Generic;
using UnityEngine;

namespace Genesis.RoomScan
{
    public enum SurfaceType : byte
    {
        Unknown = 0,
        Floor = 1,
        Ceiling = 2,
        Wall = 3,
        Furniture = 4
    }

    /// <summary>
    /// Galaxy XR-compatible fallback for the optional room-semantic layer.
    /// TSDF integration and Surface Nets extraction do not depend on room discovery.
    /// </summary>
    public class RoomUnderstanding : MonoBehaviour, IRoomScanModule
    {
        public static RoomUnderstanding Instance { get; private set; }
        public string ModuleName => "Room Understanding";
        public void OnModuleInitialize(RoomScanner scanner) { }
        public event Action AnchorsChanged;

        private SurfaceType[] _lastClassification;

        void Awake() => Instance = this;
        void OnEnable() { }
        void OnDisable() { }

        public SurfaceType GetSurfaceType(Vector3 worldPos) => SurfaceType.Unknown;

        public SurfaceType[] GetPerVertexSurfaceTypes(Mesh mesh)
        {
            if (mesh == null) return null;
            var normals = mesh.normals;
            var result = new SurfaceType[normals.Length];
            for (int i = 0; i < normals.Length; i++)
                result[i] = ClassifyFromNormal(normals[i]);
            _lastClassification = result;
            return result;
        }

        public SurfaceType[] LastClassification => _lastClassification;
        public List<Plane> GetWallPlanes() => new List<Plane>();
        public Plane GetFloorPlane() => new Plane(Vector3.up, Vector3.zero);
        public List<Bounds> GetFurnitureBounds() => new List<Bounds>();
        public bool IsHeadsetInsideAnyRoom() => true;
        public bool IsHeadsetInsideRoom(Guid sceneRoomUuid) => sceneRoomUuid != Guid.Empty;
        public bool HasRoom(Guid sceneRoomUuid) => false;
        public Guid TryGetRoomUuidAt(Vector3 worldPos) => Guid.Empty;
        public Guid TryGetRoomUuidContainingHeadset() => Guid.Empty;
        public void RefreshRoom() { }

        public int CopyRoomClipPlanes(Guid sceneRoomUuid, List<Vector4> dest)
        {
            dest?.Clear();
            return 0;
        }

        public int CopyScreenStamps(Guid sceneRoomUuid, List<ScanScreenStamp> dest)
        {
            dest?.Clear();
            return 0;
        }

        public int CopyShellCells(Guid sceneRoomUuid, ShellCellSet dest)
        {
            dest?.Clear();
            return 0;
        }

        public bool CopyRoomWorldAabb(Guid sceneRoomUuid, out Vector3 min, out Vector3 max)
        {
            min = Vector3.zero;
            max = Vector3.zero;
            return false;
        }

        public int CopyWallFacesOfRoomContaining(Vector3 worldPos, List<SceneWallFace> dest, SceneFaceKind kind)
        {
            dest?.Clear();
            return 0;
        }

        public int CopyHeadsetRoomWallFaces(List<SceneWallFace> dest, SceneFaceKind kind)
        {
            dest?.Clear();
            return 0;
        }

        public void PopulateRegistry(SceneObjectRegistry registry)
        {
            registry?.RemoveBySource(SceneObjectSource.MRUK);
        }

        private static SurfaceType ClassifyFromNormal(Vector3 normal)
        {
            float absY = Mathf.Abs(normal.y);
            if (absY > 0.8f)
                return Vector3.Dot(normal, Vector3.up) > 0f ? SurfaceType.Floor : SurfaceType.Ceiling;
            if (absY > 0.35f)
                return SurfaceType.Wall;
            return SurfaceType.Furniture;
        }
    }
}
