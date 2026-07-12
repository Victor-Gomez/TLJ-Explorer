using System.Text;
using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class BiffMeshReaderTests
{
    private const uint TypeSceneData = 0x05a4aa94;
    private const uint TypeBase      = 0x05a4aa89;
    private const uint TypeTri       = 0x05a4aa8d;
    private const uint TypeMaterial  = 0x05a4aa8e;

    [Fact]
    public void Read_TwoTriangleQuad_ReindexesToSharedVertices()
    {
        // Build a minimum BIFF file (version 1) with one material + one MeshObjectTri whose two triangles
        // share their four corners. After the reader's reindex pass the flat vertex array should have
        // 4 vertices (not 6), and the single face group should reference them with 6 indices.
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);

        WriteBiffHeader(w, version: 1, numBlocks: 2);

        // Block 1: MeshObjectMaterial
        WriteBlock(w, TypeMaterial, fileVersion: 1, writePayload: mw =>
        {
            WriteString16(mw, "diffuse_mat");
            WriteString16(mw, "wall.tm");
            WriteString16(mw, string.Empty);
            WriteString16(mw, string.Empty);
            mw.Write(0u);                      // shading
            WriteVec3(mw, 0.2f, 0.2f, 0.2f);  // ambient
            WriteVec3(mw, 1f, 0.5f, 0.25f);   // diffuse
            WriteVec3(mw, 1f, 1f, 1f);        // specular
            mw.Write(32f); mw.Write(1f);       // shininess, opacity
            mw.Write((byte)0);                 // doubleSided
            mw.Write(1u); mw.Write(1u); mw.Write(1u); // tilings
            mw.Write((byte)0);                 // isColorKey
            mw.Write(0u);                      // colorKey
            mw.Write(0u);                      // attributeCount
        });

        // Block 2: MeshObjectTri — a quad split into two triangles sharing all four vertices.
        WriteBlock(w, TypeTri, fileVersion: 1, writePayload: mw =>
        {
            WriteString16(mw, "quad");
            mw.Write(1u);                      // keyFrame count
            // Identity keyframe: time=0, essentialRot=(0,0,0,1), det=1, stretchRot=(0,0,0,1), scale=(1,1,1), trans=(0,0,0)
            mw.Write(0u);
            WriteQuat(mw, 0, 0, 0, 1);
            mw.Write(1f);
            WriteQuat(mw, 0, 0, 0, 1);
            WriteVec3(mw, 1, 1, 1);
            WriteVec3(mw, 0, 0, 0);

            // Vertices: 4 corners.
            mw.Write(4u);
            WriteVertex(mw, 0, 0, 0);
            WriteVertex(mw, 1, 0, 0);
            WriteVertex(mw, 1, 1, 0);
            WriteVertex(mw, 0, 1, 0);

            // Normals: one shared.
            mw.Write(1u);
            WriteVec3(mw, 0, 0, 1);

            // Texture positions: 4.
            mw.Write(4u);
            WriteVec3(mw, 0, 0, 0);
            WriteVec3(mw, 1, 0, 0);
            WriteVec3(mw, 1, 1, 0);
            WriteVec3(mw, 0, 1, 0);

            // Two triangles both referencing material 0.
            mw.Write(2u);
            WriteFace(mw, v: [0, 1, 2], n: [0, 0, 0], t: [0, 1, 2], mat: 0);
            WriteFace(mw, v: [0, 2, 3], n: [0, 0, 0], t: [0, 2, 3], mat: 0);

            mw.Write((byte)1); // hasPhysics
        });

        stream.Position = 0;
        BiffMesh mesh = BiffMeshReader.Read(stream);

        Assert.Equal("quad", mesh.Name);
        Assert.True(mesh.HasPhysics);
        Assert.Single(mesh.Materials);
        Assert.Equal("diffuse_mat", mesh.Materials[0].Name);
        Assert.Equal("wall.tm", mesh.Materials[0].Texture);

        // Each of the 4 (vertex,normal,texcoord) tuples produces one reindexed vertex; the two shared
        // corners across the two triangles should collapse.
        Assert.Equal(4, mesh.Vertices.Length);
        Assert.Single(mesh.Groups);
        Assert.Equal(0u, mesh.Groups[0].MaterialId);
        Assert.Equal(6, mesh.Groups[0].VertexIndices.Length); // 2 triangles × 3
    }

    private static void WriteBiffHeader(BinaryWriter w, uint version, uint numBlocks)
    {
        foreach (char c in "BIFF") w.Write((byte)c);
        w.Write(version);
        w.Write(0u); w.Write(0u); // unknown1, unknown2
        w.Write(numBlocks);
    }

    private static void WriteBlock(BinaryWriter w, uint typeId, uint fileVersion, Action<BinaryWriter> writePayload)
    {
        w.Write(0xf0f0f0f0u); // BeginMarker
        w.Write(typeId);
        w.Write(0u);           // Unknown1

        // Serialize payload to a scratch buffer so we can know its length.
        using var scratch = new MemoryStream();
        var sw = new BinaryWriter(scratch);
        writePayload(sw);
        byte[] payloadBytes = scratch.ToArray();

        w.Write(payloadBytes.Length);
        if (fileVersion == 2)
            w.Write(0u); // per-object version (0 = don't read uv keyframes)
        w.Write(payloadBytes);

        w.Write(0x0f0f0f0fu); // EndMarker
        w.Write(0u);          // NumSubBlocks
    }

    private static void WriteString16(BinaryWriter w, string s)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(s);
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    private static void WriteVec3(BinaryWriter w, float x, float y, float z)
    {
        w.Write(x); w.Write(y); w.Write(z);
    }

    private static void WriteQuat(BinaryWriter w, float x, float y, float z, float ww)
    {
        w.Write(x); w.Write(y); w.Write(z); w.Write(ww);
    }

    private static void WriteVertex(BinaryWriter w, float x, float y, float z)
    {
        WriteString16(w, string.Empty); // animName1
        WriteString16(w, string.Empty); // animName2
        w.Write(0f); w.Write(0f);       // influences
        WriteVec3(w, x, y, z);
    }

    private static void WriteFace(BinaryWriter w, uint[] v, uint[] n, uint[] t, uint mat)
    {
        for (int i = 0; i < 3; i++) w.Write(v[i]);
        for (int i = 0; i < 3; i++) w.Write(n[i]);
        for (int i = 0; i < 3; i++) w.Write(t[i]);
        w.Write(mat);
        w.Write(0u); // smoothingGroup
    }
}
