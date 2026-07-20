using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class ObjWriterTests
{
    private static CirVertex Vertex(float x, float y, float z, float s = 0, float t = 0) =>
        new(x, y, z, x, y, z, 0, 1, 0, s, t, BoneIndex1: 0, BoneIndex2: 0, BoneWeight: 1f);

    private static CirModel SingleTriangleModel(string? materialName = "mat0", string? groupName = "group0") =>
        new()
        {
            Materials = materialName is null ? [] : [new CirMaterial(materialName, 0, "tex.png", 1, 0.5f, 0.25f)],
            Skeleton = [],
            Groups =
            [
                new CirGroup(
                    groupName ?? "",
                    Faces:
                    [
                        new CirFace(
                            MaterialIndex: 0,
                            Vertices: [Vertex(0, 0, 0), Vertex(1, 0, 0), Vertex(0, 1, 0)],
                            Triangles: [new CirTriangle(0, 1, 2)]),
                    ],
                    Unknown1: [],
                    Unknown2: []),
            ],
        };

    private static string TempObjPath() => Path.Combine(Path.GetTempPath(), $"tlj-obj-test-{Guid.NewGuid():N}.obj");

    [Fact]
    public void Write_NullModel_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ObjWriter.Write(null!, TempObjPath()));
    }

    [Fact]
    public void Write_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(SingleTriangleModel(), ""));
    }

    [Fact]
    public void Write_SimpleTriangle_EmitsExpectedVertexAndFaceLines()
    {
        string objPath = TempObjPath();
        string mtlPath = Path.ChangeExtension(objPath, ".mtl");
        try
        {
            ObjWriter.Write(SingleTriangleModel(), objPath);

            string[] lines = File.ReadAllLines(objPath);
            Assert.Contains(lines, l => l.StartsWith("v 0 0 0", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("v 1 0 0", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("f 1/1/1 2/2/2 3/3/3", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("mtllib ", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("o group0", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(objPath);
            File.Delete(mtlPath);
        }
    }

    [Fact]
    public void Write_ModelWithMaterials_WritesSiblingMtlFile()
    {
        string objPath = TempObjPath();
        string mtlPath = Path.ChangeExtension(objPath, ".mtl");
        try
        {
            ObjWriter.Write(SingleTriangleModel(materialName: "hero_skin"), objPath);

            Assert.True(File.Exists(mtlPath));
            string mtl = File.ReadAllText(mtlPath);
            Assert.Contains("newmtl hero_skin", mtl, StringComparison.Ordinal);
            Assert.Contains("Kd 1 0.5 0.25", mtl, StringComparison.Ordinal);
            Assert.Contains("map_Kd tex.png", mtl, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(objPath);
            File.Delete(mtlPath);
        }
    }

    [Fact]
    public void Write_ModelWithNoMaterials_DoesNotWriteMtlOrMtllibLine()
    {
        string objPath = TempObjPath();
        string mtlPath = Path.ChangeExtension(objPath, ".mtl");
        try
        {
            ObjWriter.Write(SingleTriangleModel(materialName: null), objPath);

            Assert.False(File.Exists(mtlPath));
            string obj = File.ReadAllText(objPath);
            Assert.DoesNotContain("mtllib", obj, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(objPath);
        }
    }

    [Fact]
    public void Write_NoGroups_WritesOnlyHeaderComment()
    {
        string objPath = TempObjPath();
        try
        {
            var model = new CirModel { Materials = [], Skeleton = [], Groups = [] };
            ObjWriter.Write(model, objPath);

            string[] lines = File.ReadAllLines(objPath);
            Assert.Single(lines);
            Assert.StartsWith("#", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(objPath);
        }
    }

    [Fact]
    public void Write_MaterialNameWithSpaces_ReplacesWithUnderscores()
    {
        string objPath = TempObjPath();
        string mtlPath = Path.ChangeExtension(objPath, ".mtl");
        try
        {
            ObjWriter.Write(SingleTriangleModel(materialName: "hero skin v2"), objPath);

            string obj = File.ReadAllText(objPath);
            Assert.Contains("usemtl hero_skin_v2", obj, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(objPath);
            File.Delete(mtlPath);
        }
    }

    [Fact]
    public void Write_TextureVCoordinate_IsFlippedFromSourceT()
    {
        string objPath = TempObjPath();
        string mtlPath = Path.ChangeExtension(objPath, ".mtl");
        try
        {
            var model = SingleTriangleModel();
            model = new CirModel
            {
                Materials = model.Materials,
                Skeleton = model.Skeleton,
                Groups =
                [
                    new CirGroup(
                        "group0",
                        Faces:
                        [
                            new CirFace(
                                0,
                                Vertices: [Vertex(0, 0, 0, s: 0.25f, t: 0.75f)],
                                Triangles: [new CirTriangle(0, 0, 0)]),
                        ],
                        [],
                        []),
                ],
            };

            ObjWriter.Write(model, objPath);

            string obj = File.ReadAllText(objPath);
            Assert.Contains("vt 0.25 0.25", obj, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(objPath);
            File.Delete(mtlPath);
        }
    }
}
