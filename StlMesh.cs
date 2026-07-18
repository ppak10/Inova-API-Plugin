using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Inova.ApiPlugin;

/// <summary>
/// Converts an STL file (binary or ASCII) into the dashboard's binary MESH
/// blob format — the same layout <c>/printing/meshes/{hash}</c> emits (see
/// InovaApiPlugin.cs), so the web's <c>useMesh</c> parser handles both:
///   uint32 magic 0x4853454D ("MESH"), uint32 version = 1,
///   uint32 vertexCount, uint32 indexCount, uint32 flags (bit 0: hasNormals),
///   float32[vertexCount*3] vertices, uint32[indexCount] indices,
///   float32[vertexCount*3] normals (only if hasNormals).
///
/// STL is a triangle soup, so vertices are emitted 3-per-triangle with
/// sequential indices and no normals — the viewer computes vertex normals
/// client-side. No vertex dedup: preview-quality payloads are fine and the
/// parts on this printer are small.
/// </summary>
internal static class StlMesh
{
    public static byte[]? ToMeshBlob(byte[] stl)
    {
        var verts = ParseBinary(stl) ?? ParseAscii(stl);
        if (verts is null || verts.Count == 0 || verts.Count % 9 != 0) return null;
        var vertexCount = verts.Count / 3;

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write((uint)0x4853454D); // "MESH" little-endian
            bw.Write((uint)1);          // version
            bw.Write((uint)vertexCount);
            bw.Write((uint)vertexCount); // soup: one index per vertex
            bw.Write((uint)0);          // no normals
            foreach (var f in verts) bw.Write(f);
            for (uint i = 0; i < vertexCount; i++) bw.Write(i);
        }
        return ms.ToArray();
    }

    // Binary STL: 80-byte header, uint32 triangle count, then 50 bytes per
    // triangle (normal 3×f32, vertices 9×f32, uint16 attribute). The length
    // check is the reliable binary-vs-ASCII discriminator ("solid" headers
    // appear in binary files too).
    private static List<float>? ParseBinary(byte[] b)
    {
        if (b.Length < 84) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(80, 4));
        if ((long)b.Length != 84L + count * 50L) return null;
        var verts = new List<float>((int)count * 9);
        for (var t = 0; t < count; t++)
        {
            var off = 84 + t * 50 + 12; // skip facet normal
            for (var k = 0; k < 9; k++)
                verts.Add(BitConverter.ToSingle(b, off + k * 4));
        }
        return verts;
    }

    private static List<float>? ParseAscii(byte[] b)
    {
        if (b.Length < 6 || !Encoding.ASCII.GetString(b, 0, 5).Equals("solid", StringComparison.OrdinalIgnoreCase))
            return null;
        var verts = new List<float>();
        foreach (var rawLine in Encoding.ASCII.GetString(b).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("vertex ", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return null;
            for (var k = 1; k <= 3; k++)
            {
                if (!float.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    return null;
                verts.Add(f);
            }
        }
        return verts;
    }
}
