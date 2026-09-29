using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Writes the scan as standard mesh files that open in MeshLab, Blender,
    /// CloudCompare and similar tools, next to the private package format of
    /// <see cref="RoomScanPersistence"/>.
    /// <list type="bullet">
    /// <item><see cref="ExportLiveMeshAsync"/>: the live GPU Surface Nets mesh
    /// with vertex colours, as binary PLY and / or OBJ. Works while scanning.</item>
    /// <item><see cref="ExportRefinedMeshAsync"/>: the refined, UV-unwrapped
    /// mesh as a textured OBJ + MTL + PNG atlas.</item>
    /// </list>
    /// <para>
    /// <b>Coordinates.</b> Unity is left-handed with Y up; PLY / OBJ viewers
    /// assume right-handed with Y up. Both exporters negate X on positions and
    /// normals and reverse every triangle's winding (a, b, c → a, c, b):
    /// the mirror alone would show the room mirrored, and without the winding
    /// flip it would also be inside-out, because Unity's clockwise front faces
    /// become counter-clockwise front faces. Units are metres, in the scan
    /// session's tracking (world) space, the same space as
    /// <see cref="ScanResult.Mesh"/>. UV v is written as in Unity: OBJ
    /// texture coordinates also have their origin at the bottom left.
    /// </para>
    /// <para>
    /// <b>Colours.</b> PLY / OBJ viewers read vertex colours as sRGB display
    /// values, and both exports write them that way. In the Linear colour
    /// space the colour volume holds linear light (the camera frame is
    /// sampled through an sRGB texture, and the vertex shader outputs the
    /// value as-is for URP to encode), so the live export sRGB-encodes each
    /// channel; dark tones show some banding, because the volume keeps 8-bit
    /// linear values. In Gamma the volume already holds display values and
    /// they are written unchanged. The atlas PNG is sRGB bytes either way
    /// (keyframes are baked from raw JPEG bytes).
    /// </para>
    /// <para>
    /// <b>Files.</b> Everything heavy (format conversion, PNG encoding, file
    /// writes) runs on a worker thread. Each file is written as
    /// <c>name + <see cref="PartialSuffix"/></c> and renamed over the final
    /// name only after every file of the export is complete, so a copy taken
    /// meanwhile (for example <c>adb pull</c>) sees the previous complete file
    /// or the new one, never half of one. The returned tasks complete after
    /// the renames. Call from the main thread.
    /// </para>
    /// </summary>
    public static class RoomScanMeshExport
    {
        /// <summary>Suffix of a file that is still being written. Skip these when copying exports.</summary>
        public const string PartialSuffix = ".partial";

        const string Tag = "[MeshExport]";
        const string MaterialName = "RoomScanAtlas";

        // Fixed-point digits: 10 µm positions (voxels are centimetres),
        // 1e-4 for unit normals / colours, 1e-6 UV (0.004 texel at 4096).
        const int PositionDecimals = 5;
        const int UnitDecimals = 4;
        const int UvDecimals = 6;

        static readonly HashSet<string> InFlight = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 8-bit linear → 8-bit sRGB (IEC 61966-2-1). Plain C# so the worker
        /// can use it; <c>Mathf.LinearToGammaSpace</c> is a native call.
        /// </summary>
        static readonly byte[] LinearToSrgb8 = BuildLinearToSrgb8();

        /// <summary>
        /// Export the current live mesh (the last completed GPU extraction)
        /// as <c>baseName.ply</c> and / or <c>baseName.obj</c> in
        /// <paramref name="directory"/> (created if missing), overwriting
        /// files of the same name.
        /// <para>
        /// Reads the vertex and index buffers under an extraction hold, so
        /// the counts and data describe one extraction even while scanning;
        /// live extraction pauses for the few frames of the readback and the
        /// live look is untouched. Triangles with out-of-range or repeated
        /// indices, or a non-finite corner, are skipped.
        /// </para>
        /// PLY: binary little-endian; vertex <c>float x y z, float nx ny nz,
        /// uchar red green blue</c> (sRGB); face <c>list uchar int vertex_indices</c>.
        /// OBJ: <c>v x y z r g b</c> (sRGB colour 0..1), <c>vn</c>, <c>f a//a b//b c//c</c>.
        /// </summary>
        /// <returns>The written paths, or an empty array when there is no
        /// live mesh (not scanned yet, or the scan GPU resources were released
        /// after refinement). Throws on I/O errors, or when an export to the
        /// same path is already running.</returns>
        public static async Task<string[]> ExportLiveMeshAsync(string directory, string baseName,
            bool writePly = true, bool writeObj = true)
        {
            ValidateTarget(directory, baseName);
            var targets = new List<string>(2);
            if (writePly) targets.Add(Path.Combine(directory, baseName + ".ply"));
            if (writeObj) targets.Add(Path.Combine(directory, baseName + ".obj"));
            if (targets.Count == 0) return Array.Empty<string>();
            // Main thread (native property). Linear: the colour volume holds
            // linear light, which viewers would show too dark.
            bool encodeSrgb = QualitySettings.activeColorSpace == ColorSpace.Linear;

            Claim(targets);
            try
            {
                var total = Stopwatch.StartNew();
                var snapshot = await GPUMeshReadback.ReadAsync(extractFirst: false, Tag);
                if (snapshot == null || snapshot.VertexCount == 0 || snapshot.TriangleCount == 0)
                {
                    Logger.Warning($"{Tag} No live mesh to export (not scanned yet, or scan resources released).");
                    return Array.Empty<string>();
                }
                double readbackMs = total.Elapsed.TotalMilliseconds;

                int vertexCount = snapshot.VertexCount;
                int triangleCount = 0, dropped = 0;
                long[] sizes = null;
                string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(directory);
                    var positions = snapshot.Positions;
                    var normals = snapshot.Normals;
                    MirrorX(positions);
                    MirrorX(normals);
                    if (encodeSrgb) EncodeSrgb(snapshot.Colors);
                    int[] triangles = FlipTriangles(snapshot.Indices, positions, out dropped);
                    triangleCount = triangles.Length / 3;

                    string comment = $"Genesis RoomScan live mesh {stamp}; right-handed Y-up metres (Unity x negated, winding reversed); sRGB vertex colours";
                    var partials = new List<string>(targets.Count);
                    try
                    {
                        foreach (string target in targets)
                        {
                            string partial = target + PartialSuffix;
                            partials.Add(partial);
                            if (target.EndsWith(".ply", StringComparison.Ordinal))
                                WritePly(partial, positions, normals, snapshot.Colors, triangles, comment);
                            else
                                WriteColoredObj(partial, positions, normals, snapshot.Colors, triangles, comment);
                        }
                        sizes = CommitAll(partials, targets);
                    }
                    catch
                    {
                        DeleteQuietly(partials);
                        throw;
                    }
                });

                Logger.Info($"{Tag} Live mesh: {vertexCount} verts, {triangleCount} tris" +
                    (dropped + snapshot.DroppedTriangles > 0 ? $" ({dropped + snapshot.DroppedTriangles} bad tris skipped)" : "") +
                    (encodeSrgb ? ", colours linear -> sRGB" : ", colours as stored (Gamma)") +
                    $" -> {Describe(targets, sizes)} in {total.Elapsed.TotalSeconds:F2} s " +
                    $"(readback {readbackMs:F0} ms, convert + write {total.Elapsed.TotalMilliseconds - readbackMs:F0} ms)");
                return targets.ToArray();
            }
            finally
            {
                Unclaim(targets);
            }
        }

        /// <summary>
        /// Export a refined mesh and its atlas as <c>baseName.obj</c>,
        /// <c>baseName.mtl</c> and <c>baseName.png</c> in
        /// <paramref name="directory"/> (created if missing), overwriting
        /// files of the same name. Typically <see cref="ScanResult.Mesh"/> /
        /// <see cref="ScanResult.Atlas"/> or <see cref="RoomScanner.RefinedMesh"/> /
        /// <see cref="RoomScanner.RefinedAtlas"/>.
        /// <para>
        /// OBJ: <c>v</c>, <c>vt</c>, <c>vn</c>, <c>f a/a/a b/b/b c/c/c</c>
        /// (the unwrap gives each vertex one UV, so the three indices agree);
        /// triangle sub-meshes are merged. The PNG is the atlas's top mip with
        /// alpha forced opaque. A readable RGBA32 atlas is copied directly;
        /// any other (non-readable, or another format) is blitted to an RGBA32
        /// render texture in its own colour space and read back. The mesh
        /// must be CPU-readable. With a null <paramref name="atlas"/> only an
        /// untextured OBJ is written.
        /// </para>
        /// Mesh and texture reads happen on the main thread (a few ms, plus
        /// the atlas copy); PNG encoding
        /// (<see cref="ImageConversion.EncodeArrayToPNG"/>, which Unity marks
        /// thread-safe) and the writes run on a worker.
        /// </summary>
        /// <returns>The written paths (OBJ first). Throws on I/O errors, a
        /// non-readable mesh, or when an export to the same path is already
        /// running.</returns>
        public static async Task<string[]> ExportRefinedMeshAsync(Mesh mesh, Texture2D atlas,
            string directory, string baseName)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            ValidateTarget(directory, baseName);
            if (!mesh.isReadable)
                throw new InvalidOperationException($"Mesh '{mesh.name}' is not CPU-readable; cannot export it.");

            string objPath = Path.Combine(directory, baseName + ".obj");
            string mtlPath = Path.Combine(directory, baseName + ".mtl");
            string pngPath = Path.Combine(directory, baseName + ".png");
            var claimed = atlas != null
                ? new List<string> { objPath, mtlPath, pngPath }
                : new List<string> { objPath };
            var targets = claimed;

            Claim(claimed);
            try
            {
                var total = Stopwatch.StartNew();

                // Main thread: Mesh / Texture2D API.
                Vector3[] positions = mesh.vertices;
                Vector3[] normals = mesh.normals;
                Vector2[] uvs = mesh.uv;
                int[] indices = GatherTriangles(mesh);
                if (positions.Length == 0 || indices.Length < 3)
                {
                    Logger.Warning($"{Tag} Mesh '{mesh.name}' has no triangles; nothing to export.");
                    return Array.Empty<string>();
                }

                byte[] rgba = null;
                int atlasW = 0, atlasH = 0;
                if (atlas != null)
                {
                    (rgba, atlasW, atlasH) = await ReadAtlasRgbaAsync(atlas);
                    if (rgba == null)
                    {
                        Logger.Warning($"{Tag} Atlas readback failed; exporting the mesh untextured.");
                        targets = new List<string> { objPath };
                    }
                }
                double mainMs = total.Elapsed.TotalMilliseconds;

                bool textured = rgba != null;
                int triangleCount = 0, dropped = 0;
                long[] sizes = null;
                string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(directory);
                    MirrorX(positions);
                    MirrorX(normals);
                    int[] triangles = FlipTriangles(indices, positions, out dropped);
                    triangleCount = triangles.Length / 3;

                    byte[] png = null;
                    if (textured)
                    {
                        for (int i = 3; i < rgba.Length; i += 4)
                            rgba[i] = 255;
                        png = ImageConversion.EncodeArrayToPNG(rgba, GraphicsFormat.R8G8B8A8_UNorm,
                            (uint)atlasW, (uint)atlasH);
                        if (png == null || png.Length == 0)
                            throw new InvalidOperationException("PNG encoding of the atlas failed");
                    }

                    string comment = $"Genesis RoomScan refined mesh {stamp}; right-handed Y-up metres (Unity x negated, winding reversed)";
                    var partials = new List<string>(targets.Count);
                    try
                    {
                        string objPartial = objPath + PartialSuffix;
                        partials.Add(objPartial);
                        WriteTexturedObj(objPartial, positions, normals, uvs, triangles, comment,
                            textured ? Path.GetFileName(mtlPath) : null);
                        if (textured)
                        {
                            string mtlPartial = mtlPath + PartialSuffix;
                            partials.Add(mtlPartial);
                            File.WriteAllText(mtlPartial,
                                "# " + comment + "\n" +
                                "newmtl " + MaterialName + "\n" +
                                "Ka 1 1 1\nKd 1 1 1\nKs 0 0 0\nd 1\nillum 1\n" +
                                "map_Kd " + Path.GetFileName(pngPath) + "\n",
                                new UTF8Encoding(false));

                            string pngPartial = pngPath + PartialSuffix;
                            partials.Add(pngPartial);
                            File.WriteAllBytes(pngPartial, png);
                        }
                        sizes = CommitAll(partials, targets);
                    }
                    catch
                    {
                        DeleteQuietly(partials);
                        throw;
                    }
                });

                Logger.Info($"{Tag} Refined mesh: {positions.Length} verts, {triangleCount} tris" +
                    (dropped > 0 ? $" ({dropped} bad tris skipped)" : "") +
                    (textured ? $", atlas {atlasW}x{atlasH}" : ", untextured") +
                    $" -> {Describe(targets, sizes)} in {total.Elapsed.TotalSeconds:F2} s " +
                    $"(main thread {mainMs:F0} ms)");
                return targets.ToArray();
            }
            finally
            {
                Unclaim(claimed);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Conversion (worker thread)
        // ─────────────────────────────────────────────────────────────

        /// <summary>Unity left-handed → right-handed Y-up, in place.</summary>
        static void MirrorX(Vector3[] v)
        {
            if (v == null) return;
            for (int i = 0; i < v.Length; i++)
                v[i].x = -v[i].x;
        }

        /// <summary>
        /// Valid triangles with their winding reversed (a, c, b) to go with
        /// <see cref="MirrorX"/>. Skips out-of-range or repeated indices and
        /// triangles touching a non-finite position.
        /// </summary>
        static int[] FlipTriangles(int[] src, Vector3[] positions, out int dropped)
        {
            int vc = positions.Length;
            int n = src.Length - src.Length % 3;
            var dst = new int[n];
            int k = 0;
            dropped = 0;
            for (int t = 0; t < n; t += 3)
            {
                int a = src[t], b = src[t + 1], c = src[t + 2];
                if ((uint)a >= (uint)vc || (uint)b >= (uint)vc || (uint)c >= (uint)vc
                    || a == b || b == c || a == c
                    || !IsFinite(positions[a]) || !IsFinite(positions[b]) || !IsFinite(positions[c]))
                {
                    dropped++;
                    continue;
                }
                dst[k] = a;
                dst[k + 1] = c;
                dst[k + 2] = b;
                k += 3;
            }
            if (k != n) Array.Resize(ref dst, k);
            return dst;
        }

        /// <summary>Linear-light vertex colours → sRGB display values, in place; alpha kept.</summary>
        static void EncodeSrgb(Color32[] colors)
        {
            if (colors == null) return;
            byte[] lut = LinearToSrgb8;
            for (int i = 0; i < colors.Length; i++)
            {
                Color32 c = colors[i];
                colors[i] = new Color32(lut[c.r], lut[c.g], lut[c.b], c.a);
            }
        }

        static byte[] BuildLinearToSrgb8()
        {
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                double s = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
                lut[i] = (byte)Math.Round(Math.Min(1.0, Math.Max(0.0, s)) * 255.0);
            }
            return lut;
        }

        static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        static float Finite(float f) => IsFinite(f) ? f : 0f;

        static int[] GatherTriangles(Mesh mesh)
        {
            if (mesh.subMeshCount == 1 && mesh.GetTopology(0) == MeshTopology.Triangles)
                return mesh.GetIndices(0);
            var all = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles)
                    all.AddRange(mesh.GetIndices(s));
            return all.ToArray();
        }

        // ─────────────────────────────────────────────────────────────
        //  Writers (worker thread)
        // ─────────────────────────────────────────────────────────────

        static void WritePly(string path, Vector3[] positions, Vector3[] normals, Color32[] colors,
            int[] triangles, string comment)
        {
            int vc = positions.Length;
            bool hasNormals = normals != null && normals.Length == vc;
            bool hasColors = colors != null && colors.Length == vc;

            var header = new StringBuilder(512);
            header.Append("ply\n");
            header.Append("format binary_little_endian 1.0\n");
            header.Append("comment ").Append(comment).Append('\n');
            header.Append("element vertex ").Append(vc.ToString(CultureInfo.InvariantCulture)).Append('\n');
            header.Append("property float x\nproperty float y\nproperty float z\n");
            header.Append("property float nx\nproperty float ny\nproperty float nz\n");
            header.Append("property uchar red\nproperty uchar green\nproperty uchar blue\n");
            header.Append("element face ").Append((triangles.Length / 3).ToString(CultureInfo.InvariantCulture)).Append('\n');
            header.Append("property list uchar int vertex_indices\n");
            header.Append("end_header\n");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            // BinaryWriter always writes little-endian.
            using var bw = new BinaryWriter(fs, Encoding.ASCII);
            bw.Write(Encoding.ASCII.GetBytes(header.ToString()));
            for (int i = 0; i < vc; i++)
            {
                Vector3 p = positions[i];
                bw.Write(Finite(p.x)); bw.Write(Finite(p.y)); bw.Write(Finite(p.z));
                Vector3 nrm = hasNormals ? normals[i] : Vector3.zero;
                bw.Write(Finite(nrm.x)); bw.Write(Finite(nrm.y)); bw.Write(Finite(nrm.z));
                Color32 c = hasColors ? colors[i] : new Color32(200, 200, 200, 255);
                bw.Write(c.r); bw.Write(c.g); bw.Write(c.b);
            }
            for (int t = 0; t < triangles.Length; t += 3)
            {
                bw.Write((byte)3);
                bw.Write(triangles[t]);
                bw.Write(triangles[t + 1]);
                bw.Write(triangles[t + 2]);
            }
        }

        static void WriteColoredObj(string path, Vector3[] positions, Vector3[] normals, Color32[] colors,
            int[] triangles, string comment)
        {
            int vc = positions.Length;
            bool hasNormals = normals != null && normals.Length == vc;
            bool hasColors = colors != null && colors.Length == vc;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            using var w = new AsciiWriter(fs);
            w.Text("# ").Text(comment).Nl();
            w.Text("# ").Int(vc).Text(" vertices, ").Int(triangles.Length / 3).Text(" triangles").Nl();
            for (int i = 0; i < vc; i++)
            {
                Vector3 p = positions[i];
                w.Text("v ").Fixed(p.x, PositionDecimals).Sp().Fixed(p.y, PositionDecimals).Sp().Fixed(p.z, PositionDecimals);
                if (hasColors)
                {
                    Color32 c = colors[i];
                    w.Sp().Fixed(c.r / 255f, UnitDecimals).Sp().Fixed(c.g / 255f, UnitDecimals).Sp().Fixed(c.b / 255f, UnitDecimals);
                }
                w.Nl();
            }
            if (hasNormals)
                for (int i = 0; i < vc; i++)
                {
                    Vector3 nrm = normals[i];
                    w.Text("vn ").Fixed(nrm.x, UnitDecimals).Sp().Fixed(nrm.y, UnitDecimals).Sp().Fixed(nrm.z, UnitDecimals).Nl();
                }
            for (int t = 0; t < triangles.Length; t += 3)
            {
                w.Text("f ");
                for (int j = 0; j < 3; j++)
                {
                    int idx = triangles[t + j] + 1;
                    if (j > 0) w.Sp();
                    w.Int(idx);
                    if (hasNormals) w.Text("//").Int(idx);
                }
                w.Nl();
            }
        }

        static void WriteTexturedObj(string path, Vector3[] positions, Vector3[] normals, Vector2[] uvs,
            int[] triangles, string comment, string mtlFileName)
        {
            int vc = positions.Length;
            bool hasNormals = normals != null && normals.Length == vc;
            bool hasUvs = uvs != null && uvs.Length == vc;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            using var w = new AsciiWriter(fs);
            w.Text("# ").Text(comment).Nl();
            w.Text("# ").Int(vc).Text(" vertices, ").Int(triangles.Length / 3).Text(" triangles").Nl();
            if (mtlFileName != null)
                w.Text("mtllib ").Text(mtlFileName).Nl();
            for (int i = 0; i < vc; i++)
            {
                Vector3 p = positions[i];
                w.Text("v ").Fixed(p.x, PositionDecimals).Sp().Fixed(p.y, PositionDecimals).Sp().Fixed(p.z, PositionDecimals).Nl();
            }
            if (hasUvs)
                for (int i = 0; i < vc; i++)
                    w.Text("vt ").Fixed(uvs[i].x, UvDecimals).Sp().Fixed(uvs[i].y, UvDecimals).Nl();
            if (hasNormals)
                for (int i = 0; i < vc; i++)
                {
                    Vector3 nrm = normals[i];
                    w.Text("vn ").Fixed(nrm.x, UnitDecimals).Sp().Fixed(nrm.y, UnitDecimals).Sp().Fixed(nrm.z, UnitDecimals).Nl();
                }
            if (mtlFileName != null)
                w.Text("usemtl ").Text(MaterialName).Nl();
            for (int t = 0; t < triangles.Length; t += 3)
            {
                w.Text("f ");
                for (int j = 0; j < 3; j++)
                {
                    int idx = triangles[t + j] + 1;
                    if (j > 0) w.Sp();
                    w.Int(idx);
                    if (hasUvs && hasNormals) w.Char('/').Int(idx).Char('/').Int(idx);
                    else if (hasUvs) w.Char('/').Int(idx);
                    else if (hasNormals) w.Text("//").Int(idx);
                }
                w.Nl();
            }
        }

        /// <summary>
        /// Allocation-free ASCII writer. <c>float.ToString</c> per value was
        /// millions of small strings per export, and worker-thread garbage
        /// still stops the main thread when the collector runs.
        /// </summary>
        sealed class AsciiWriter : IDisposable
        {
            static readonly long[] Pow10 = { 1, 10, 100, 1000, 10000, 100000, 1000000, 10000000 };

            readonly Stream _stream;
            readonly byte[] _buf = new byte[1 << 16];
            int _n;

            public AsciiWriter(Stream stream) { _stream = stream; }

            public AsciiWriter Char(char c)
            {
                if (_n == _buf.Length) Flush();
                _buf[_n++] = c < 128 ? (byte)c : (byte)'?';
                return this;
            }

            public AsciiWriter Sp() => Char(' ');
            public AsciiWriter Nl() => Char('\n');

            public AsciiWriter Text(string s)
            {
                for (int i = 0; i < s.Length; i++) Char(s[i]);
                return this;
            }

            public AsciiWriter Int(long v)
            {
                if (_n + 21 > _buf.Length) Flush();
                if (v < 0)
                {
                    _buf[_n++] = (byte)'-';
                    v = -v;
                }
                int start = _n;
                do
                {
                    _buf[_n++] = (byte)('0' + (int)(v % 10));
                    v /= 10;
                } while (v > 0);
                for (int i = start, j = _n - 1; i < j; i++, j--)
                {
                    byte tmp = _buf[i];
                    _buf[i] = _buf[j];
                    _buf[j] = tmp;
                }
                return this;
            }

            /// <summary>Fixed point, trailing zeros trimmed; non-finite writes 0.</summary>
            public AsciiWriter Fixed(float f, int decimals)
            {
                if (!IsFinite(f)) f = 0f;
                long scale = Pow10[decimals];
                long q = (long)Math.Round((double)f * scale);
                if (q < 0)
                {
                    Char('-');
                    q = -q;
                }
                Int(q / scale);
                long frac = q % scale;
                if (frac != 0)
                {
                    if (_n + decimals + 1 > _buf.Length) Flush();
                    _buf[_n++] = (byte)'.';
                    for (int d = decimals - 1; d >= 0; d--)
                    {
                        _buf[_n + d] = (byte)('0' + (int)(frac % 10));
                        frac /= 10;
                    }
                    _n += decimals;
                    while (_buf[_n - 1] == (byte)'0') _n--;
                }
                return this;
            }

            public void Flush()
            {
                if (_n > 0) _stream.Write(_buf, 0, _n);
                _n = 0;
            }

            public void Dispose() => Flush();
        }

        // ─────────────────────────────────────────────────────────────
        //  Atlas pixels (main thread)
        // ─────────────────────────────────────────────────────────────

        static async Task<(byte[] rgba, int width, int height)> ReadAtlasRgbaAsync(Texture2D atlas)
        {
            int w = atlas.width, h = atlas.height;
            if (atlas.isReadable && atlas.format == TextureFormat.RGBA32)
                return (atlas.GetPixelData<byte>(0).ToArray(), w, h);

            // Non-readable (uploaded with makeNoLongerReadable) or not RGBA32:
            // GPU copy into an RGBA32 target in the texture's own colour space,
            // so sRGB bytes round-trip unchanged, then read that back.
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                atlas.isDataSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            try
            {
                Graphics.Blit(atlas, rt);
                var tcs = new TaskCompletionSource<byte[]>();
                AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
                {
                    if (request.hasError) { tcs.SetResult(null); return; }
                    tcs.SetResult(request.GetData<byte>().ToArray());
                });
                byte[] data = await tcs.Task;
                if (data != null && data.Length < w * h * 4) data = null;
                return (data, w, h);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  File plumbing
        // ─────────────────────────────────────────────────────────────

        static void ValidateTarget(string directory, string baseName)
        {
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException("An export directory is required.", nameof(directory));
            if (string.IsNullOrEmpty(baseName) || baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || baseName.IndexOf('/') >= 0 || baseName.IndexOf('\\') >= 0)
                throw new ArgumentException($"'{baseName}' is not a valid file base name.", nameof(baseName));
        }

        static void Claim(List<string> targets)
        {
            lock (InFlight)
            {
                foreach (string t in targets)
                    if (InFlight.Contains(t))
                        throw new InvalidOperationException($"An export to {t} is already running.");
                foreach (string t in targets)
                    InFlight.Add(t);
            }
        }

        static void Unclaim(List<string> targets)
        {
            lock (InFlight)
            {
                foreach (string t in targets)
                    InFlight.Remove(t);
            }
        }

        /// <summary>Rename every partial over its target, after all were written. Returns the sizes.</summary>
        static long[] CommitAll(List<string> partials, List<string> targets)
        {
            for (int i = 0; i < partials.Count; i++)
                Commit(partials[i], targets[i]);
            var sizes = new long[targets.Count];
            for (int i = 0; i < targets.Count; i++)
                sizes[i] = new FileInfo(targets[i]).Length;
            return sizes;
        }

        /// <summary>
        /// Atomic replace where the runtime supports it (a rename over the old
        /// file). Otherwise delete + rename: a reader may briefly find no file,
        /// but never a partial one.
        /// </summary>
        static void Commit(string partial, string target)
        {
            if (File.Exists(target))
            {
                try
                {
                    File.Replace(partial, target, null);
                    return;
                }
                catch (Exception e) when (e is PlatformNotSupportedException || e is NotSupportedException
                                          || e is NotImplementedException || e is IOException
                                          || e is UnauthorizedAccessException)
                {
                    File.Delete(target);
                }
            }
            File.Move(partial, target);
        }

        static void DeleteQuietly(List<string> paths)
        {
            foreach (string p in paths)
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch { /* best effort */ }
            }
        }

        static string Describe(List<string> paths, long[] sizes)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < paths.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Path.GetFileName(paths[i]));
                if (sizes != null && i < sizes.Length)
                    sb.Append(" (").Append((sizes[i] / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture)).Append(" MB)");
            }
            if (paths.Count > 0)
                sb.Append(" in ").Append(Path.GetDirectoryName(paths[0]));
            return sb.ToString();
        }
    }
}
