using System;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.RoomScan
{
    /// <summary>
    /// CPU copy of one completed GPU Surface Nets extraction, as read back by
    /// <see cref="GPUMeshReadback.ReadAsync"/>. Unity world space (left-handed,
    /// Y up, metres). Every index is in range and no triangle repeats a vertex.
    /// </summary>
    internal sealed class GPUMeshSnapshot
    {
        public Vector3[] Positions;
        public Vector3[] Normals;
        /// <summary>Vertex colour from the colour volume; alpha is always 255.</summary>
        public Color32[] Colors;
        public int[] Indices;
        /// <summary>Triangles dropped because an index was out of range or repeated.</summary>
        public int DroppedTriangles;

        public int VertexCount => Positions != null ? Positions.Length : 0;
        public int TriangleCount => Indices != null ? Indices.Length / 3 : 0;
    }

    /// <summary>
    /// Reads the live GPU Surface Nets mesh back to the CPU. Shared by the
    /// texture-refinement unwrap and <see cref="RoomScanMeshExport"/>, so the
    /// vertex format is parsed in one place.
    /// <para>
    /// <b>Consistency.</b> The counters say how much of the vertex and index
    /// buffers is in use, and only that prefix is read (the buffers are sized
    /// to a voxel budget, tens of MB). That takes two readback round trips:
    /// counters first, then the buffers a frame or two later. While scanning,
    /// <see cref="RoomScanner"/> re-extracts every few frames, which clears
    /// the counters and rewrites both buffers; a dump landing between the two
    /// round trips would pair one extraction's counts with the next one's data
    /// (a torn mesh). So the readback holds live extraction
    /// (<see cref="MeshExtractor.HoldExtraction"/>) until the buffers are on
    /// the CPU, and checks <see cref="MeshExtractor.ExtractCount"/> did not
    /// move, which catches explicit <see cref="MeshExtractor.Extract"/> calls
    /// the hold does not block; then it retries. Readbacks are also serialised,
    /// so an autosave export and a refinement never interleave.
    /// </para>
    /// Main thread only (Unity API and the static queue); parsing runs on a
    /// worker.
    /// </summary>
    internal static class GPUMeshReadback
    {
        // pos @ 0 is the extractor dump. prevPos @ 12 is presentation-only.
        const int VertStride = GPUSurfaceNets.VertexStride;
        const int VertPos = GPUSurfaceNets.VertexPosOffset;
        const int VertNorm = GPUSurfaceNets.VertexNormalOffset;
        const int VertPacked = GPUSurfaceNets.VertexPackedColorOffset;

        /// <summary>Readbacks restarted after the mesh changed underneath them.</summary>
        const int MaxAttempts = 3;

        /// <summary>Completes when the previous readback finished; readbacks queue on it.</summary>
        static Task _tail = Task.CompletedTask;

        /// <summary>
        /// Read the current GPU mesh. <paramref name="extractFirst"/> runs
        /// <see cref="MeshExtractor.ExtractForAuthoring"/> inside the hold, so
        /// the copy is the TSDF as of this call (unwrap / bake); without it the
        /// last live extraction is read and the live look is left alone
        /// (periodic export while scanning). <paramref name="tag"/> prefixes
        /// the log lines. Returns null when there is no mesh or the readback
        /// failed (logged).
        /// </summary>
        internal static async Task<GPUMeshSnapshot> ReadAsync(bool extractFirst, string tag)
        {
            var previous = _tail;
            var mine = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tail = mine.Task;
            try
            {
                await previous;
                return await ReadCoreAsync(extractFirst, tag);
            }
            finally
            {
                mine.SetResult(true);
            }
        }

        static async Task<GPUMeshSnapshot> ReadCoreAsync(bool extractFirst, string tag)
        {
            var extractor = MeshExtractor.Instance;
            if (extractor == null || !HasBuffers(extractor.GpuSurfaceNets))
            {
                Logger.Error($"{tag} GpuSurfaceNets or its buffers are null");
                return null;
            }

            extractor.HoldExtraction();
            try
            {
                if (extractFirst)
                    extractor.ExtractForAuthoring();

                for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    var gpuSN = extractor.GpuSurfaceNets;
                    if (!HasBuffers(gpuSN))
                    {
                        Logger.Warning($"{tag} Surface Nets buffers were released during the readback");
                        return null;
                    }
                    int generation = extractor.ExtractCount;

                    Logger.Info($"{tag} Starting GPU readback...");

                    // Callback readbacks copy the NativeArray to a managed array
                    // in the callback frame.
                    byte[] counterBytes = await ReadbackBytesAsync(gpuSN.CountersBuffer);
                    if (counterBytes == null || counterBytes.Length < 4)
                    {
                        Logger.Error($"{tag} Counter readback failed");
                        return null;
                    }
                    if (!SameExtraction(extractor, gpuSN, generation))
                    {
                        Logger.Warning($"{tag} Mesh re-extracted during the counter readback (attempt {attempt}/{MaxAttempts}); retrying");
                        continue;
                    }

                    int vertCount = BitConverter.ToInt32(counterBytes, 0);
                    int idxCount = counterBytes.Length >= 8 ? BitConverter.ToInt32(counterBytes, 4) : 0;

                    Logger.Info($"{tag} Counters: verts={vertCount}, idx={idxCount}");

                    if (vertCount <= 0 || idxCount <= 0)
                    {
                        Logger.Warning($"{tag} No mesh data: verts={vertCount}, idx={idxCount}");
                        return null;
                    }

                    // The emit kernel counts past the vertex budget and the index
                    // kernel has no bound at all; only the in-buffer prefix is real.
                    vertCount = Mathf.Min(vertCount, gpuSN.VertexBuffer.count);
                    idxCount = Mathf.Min(idxCount, gpuSN.IndexBuffer.count);

                    var vertTask = ReadbackBytesAsync(gpuSN.VertexBuffer, vertCount * VertStride);
                    var idxTask = ReadbackBytesAsync(gpuSN.IndexBuffer, idxCount * 4);
                    byte[] vertData = await vertTask;
                    byte[] idxData = await idxTask;
                    if (vertData == null || idxData == null)
                    {
                        Logger.Error($"{tag} Mesh readback failed");
                        return null;
                    }
                    if (!SameExtraction(extractor, gpuSN, generation))
                    {
                        Logger.Warning($"{tag} Mesh re-extracted during the buffer readback (attempt {attempt}/{MaxAttempts}); retrying");
                        continue;
                    }

                    // Parse on a worker: a few MB of BitConverter is still a frame's worth.
                    var snapshot = await Task.Run(() => Parse(vertData, vertCount, idxData, idxCount));

                    if (snapshot.DroppedTriangles > 0)
                        Logger.Warning($"{tag} Dropped {snapshot.DroppedTriangles} triangles with out-of-range or repeated indices");
                    Logger.Info($"{tag} Readback complete: {snapshot.VertexCount} verts, {snapshot.TriangleCount} tris");
                    return snapshot;
                }

                Logger.Error($"{tag} Mesh kept changing during the readback; gave up after {MaxAttempts} attempts");
                return null;
            }
            finally
            {
                extractor.ReleaseExtraction();
            }
        }

        static bool HasBuffers(GPUSurfaceNets sn) =>
            sn != null && sn.VertexBuffer != null && sn.IndexBuffer != null && sn.CountersBuffer != null;

        /// <summary>
        /// Same Surface Nets instance and no extraction since
        /// <paramref name="generation"/>, so everything read so far is one dump.
        /// </summary>
        static bool SameExtraction(MeshExtractor extractor, GPUSurfaceNets sn, int generation) =>
            extractor != null && extractor.GpuSurfaceNets == sn && extractor.ExtractCount == generation;

        static GPUMeshSnapshot Parse(byte[] vertData, int vertCount, byte[] idxData, int idxCount)
        {
            int vc = Mathf.Min(vertCount, vertData.Length / VertStride);
            int ic = Mathf.Min(idxCount, idxData.Length / 4);
            ic -= ic % 3;

            var positions = new Vector3[vc];
            var normals = new Vector3[vc];
            var colors = new Color32[vc];
            for (int i = 0; i < vc; i++)
            {
                int off = i * VertStride;
                positions[i] = new Vector3(
                    BitConverter.ToSingle(vertData, off + VertPos),
                    BitConverter.ToSingle(vertData, off + VertPos + 4),
                    BitConverter.ToSingle(vertData, off + VertPos + 8));
                normals[i] = new Vector3(
                    BitConverter.ToSingle(vertData, off + VertNorm),
                    BitConverter.ToSingle(vertData, off + VertNorm + 4),
                    BitConverter.ToSingle(vertData, off + VertNorm + 8));
                uint packed = BitConverter.ToUInt32(vertData, off + VertPacked);
                colors[i] = new Color32(
                    (byte)(packed & 0xFF),
                    (byte)((packed >> 8) & 0xFF),
                    (byte)((packed >> 16) & 0xFF),
                    255);
            }

            // Compact in place. A vertex past the budget leaves a stale
            // coord-to-vertex entry, and a clamped count cuts off the tail.
            var indices = new int[ic];
            Buffer.BlockCopy(idxData, 0, indices, 0, ic * 4);
            int kept = 0;
            for (int t = 0; t < ic; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if ((uint)a >= (uint)vc || (uint)b >= (uint)vc || (uint)c >= (uint)vc
                    || a == b || b == c || a == c)
                    continue;
                indices[kept] = a;
                indices[kept + 1] = b;
                indices[kept + 2] = c;
                kept += 3;
            }
            if (kept != ic)
                Array.Resize(ref indices, kept);

            return new GPUMeshSnapshot
            {
                Positions = positions,
                Normals = normals,
                Colors = colors,
                Indices = indices,
                DroppedTriangles = (ic - kept) / 3,
            };
        }

        /// <summary>Read back the first <paramref name="byteCount"/> bytes (0 = whole buffer).
        /// The extractor's buffers are sized to a voxel budget, tens of MB; the
        /// mesh in them is a few MB. Reading the capacity was a 100 ms frame.</summary>
        internal static Task<byte[]> ReadbackBytesAsync(GraphicsBuffer buffer, int byteCount = 0)
        {
            var tcs = new TaskCompletionSource<byte[]>();
            Action<AsyncGPUReadbackRequest> onDone = request =>
            {
                if (request.hasError) { tcs.SetResult(null); return; }
                var native = request.GetData<byte>();
                byte[] managed = new byte[native.Length];
                NativeArray<byte>.Copy(native, managed, native.Length);
                tcs.SetResult(managed);
            };
            if (byteCount > 0)
                AsyncGPUReadback.Request(buffer, byteCount, 0, onDone);
            else
                AsyncGPUReadback.Request(buffer, onDone);
            return tcs.Task;
        }
    }
}
