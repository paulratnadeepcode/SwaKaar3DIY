using System;
using System.IO;
using System.Text;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace SwaKaar3DIY.TrackedReconstruction.Editor
{
    // Deliberately restricted to the verified COLMAP binary XYZ/normal/RGB layout.
    [ScriptedImporter(1, "ply")]
    public sealed class ColoredPlyImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            using (var reader = new BinaryReader(File.OpenRead(ctx.assetPath)))
            {
                var header = new StringBuilder();
                while (true)
                {
                    if (header.Length > 4096) throw new InvalidDataException("PLY header exceeds limit.");
                    char c = (char)reader.ReadByte();
                    header.Append(c);
                    if (c == '\n' && header.ToString().EndsWith("end_header\n", StringComparison.Ordinal)) break;
                    if (c == '\n' && header.ToString().EndsWith("end_header\r\n", StringComparison.Ordinal)) break;
                }
                string actual = header.ToString().Replace("\r\n", "\n");
                const string expected = "ply\nformat binary_little_endian 1.0\nelement vertex 271250\n" +
                    "property float x\nproperty float y\nproperty float z\n" +
                    "property float nx\nproperty float ny\nproperty float nz\n" +
                    "property uchar red\nproperty uchar green\nproperty uchar blue\nend_header\n";
                if (actual != expected || reader.BaseStream.Length - reader.BaseStream.Position != 271250L * 27)
                    throw new InvalidDataException("Unsupported PLY layout or payload size; expected verified 271250-point COLMAP cloud.");
                var positions = new Vector3[271250];
                var colors = new Color32[271250];
                var indices = new int[271250];
                for (int i = 0; i < positions.Length; i++)
                {
                    float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
                    if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) ||
                        float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
                        throw new InvalidDataException("Nonfinite point coordinate.");
                    // Match Unity's OBJ handedness conversion. Do not independently center/scale.
                    positions[i] = new Vector3(-x, y, z);
                    reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
                    colors[i] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), 255);
                    indices[i] = i;
                }
                var mesh = new Mesh { name = "COLMAP Colored Points", indexFormat = IndexFormat.UInt32 };
                mesh.vertices = positions;
                mesh.colors32 = colors;
                mesh.SetIndices(indices, MeshTopology.Points, 0);
                mesh.RecalculateBounds();
                ctx.AddObjectToAsset("points", mesh);
                ctx.SetMainObject(mesh);
            }
        }
    }
}