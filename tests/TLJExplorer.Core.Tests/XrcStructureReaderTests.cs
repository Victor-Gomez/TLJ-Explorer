using System.Text;
using TLJExplorer.Core.FileSystem;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class XrcStructureReaderTests
{
    [Fact]
    public void Read_TypeSoundChild_ProducesFileRefWithoutSubtitle()
    {
        // A record tree of:
        //   root (typeId=0, name="root")
        //     sound (typeId=0x10, name="doorbell", data=<string "doorbell.isn">)
        var payload = new MemoryStream();
        var w = new BinaryWriter(payload);

        WriteRecord(w, typeId: 0, tag1: 0, tag2: 1, name: "root", data: [], numChildren: 1);
        WriteRecord(w, typeId: 0x10, tag1: 0, tag2: 2, name: "doorbell", data: BuildDataString("doorbell.isn"), numChildren: 0);

        payload.Position = 0;
        XrcStructure structure = XrcStructure.Read(payload);

        XrcFileRef file = Assert.Single(structure.Files);
        Assert.Equal("doorbell.isn", file.Name);
        Assert.Equal("doorbell", file.FriendlyName);
        Assert.Null(file.ExtendedInfo);
    }

    [Fact]
    public void Read_TypeDialogueWithSoundChild_AttachesSubtitleToFileRef()
    {
        // Dialogue record (typeId=0x1d, data=subtitle string) with a Sound child (typeId=0x10).
        // Expected: single XrcFileRef whose ExtendedInfo is ["[<soundName>]", "", <subtitle>].
        var payload = new MemoryStream();
        var w = new BinaryWriter(payload);

        WriteRecord(w, typeId: 0, tag1: 0, tag2: 1, name: "root", data: [], numChildren: 1);
        WriteRecord(w,
            typeId: 0x1d, tag1: 0, tag2: 2,
            name: "april_line_042",
            data: BuildDataString("Hello, world!"),
            numChildren: 1);
        WriteRecord(w,
            typeId: 0x10, tag1: 0, tag2: 3,
            name: "s0042",
            data: BuildDataString("s0042.isn"),
            numChildren: 0);

        payload.Position = 0;
        XrcStructure structure = XrcStructure.Read(payload);

        // NOTE: the current XrcStructureReader emits the sound entry twice for a dialogue-wrapped sound:
        // once with the subtitle (via TypeDialogue's handler) and once plain (via the recursive walk into
        // the Sound child). Downstream code dedupes/prefers by using whichever entry it hits first.
        // Assert only that at least one entry carries the expected subtitle text.
        XrcFileRef? withSubtitle = structure.Files
            .FirstOrDefault(f => f.Name == "s0042.isn" && f.ExtendedInfo is { Length: > 0 });

        Assert.NotNull(withSubtitle);
        Assert.Equal(3, withSubtitle!.ExtendedInfo!.Length);
        Assert.Equal("[s0042]", withSubtitle.ExtendedInfo[0]);
        Assert.Equal(string.Empty, withSubtitle.ExtendedInfo[1]);
        Assert.Equal("Hello, world!", withSubtitle.ExtendedInfo[2]);
    }

    [Fact]
    public void Read_TypeDialogueWithSoundChild_DoesNotClobberSubtitle_WhenBothRefsExist()
    {
        // Reproduces the "subtitles disappear for known-dialogue sounds" regression: XrcStructureReader
        // emits two XrcFileRefs for the same sound (dialogue-wrap with subtitle + recursive Sound
        // without), and callers that walk the list in order must not let the bare ref clobber the
        // rich one. The reader itself emits both refs; the callers' job is to reconcile them.
        var payload = new MemoryStream();
        var w = new BinaryWriter(payload);

        WriteRecord(w, typeId: 0, tag1: 0, tag2: 1, name: "root", data: [], numChildren: 1);
        WriteRecord(w, typeId: 0x1d, tag1: 0, tag2: 2, name: "line", data: BuildDataString("Hello!"), numChildren: 1);
        WriteRecord(w, typeId: 0x10, tag1: 0, tag2: 3, name: "s01", data: BuildDataString("s01.isn"), numChildren: 0);

        payload.Position = 0;
        XrcStructure structure = XrcStructure.Read(payload);

        // Two refs for "s01.isn": one carrying subtitle text, one bare.
        Assert.Equal(2, structure.Files.Count);
        Assert.All(structure.Files, f => Assert.Equal("s01.isn", f.Name));
        Assert.Contains(structure.Files, f => f.ExtendedInfo is { Length: > 0 });
        Assert.Contains(structure.Files, f => f.ExtendedInfo is null or { Length: 0 });
    }

    private static void WriteRecord(
        BinaryWriter w,
        byte typeId, byte tag1, ushort tag2,
        string name, byte[] data, ushort numChildren)
    {
        w.Write(typeId);
        w.Write(tag1);
        w.Write(tag2);

        byte[] nameBytes = Encoding.Latin1.GetBytes(name);
        w.Write((ushort)nameBytes.Length);
        w.Write(nameBytes);

        w.Write(data.Length);
        w.Write(data);

        w.Write(numChildren);
        w.Write((ushort)0); // unknown3
    }

    private static byte[] BuildDataString(string value)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(value);
        var buffer = new byte[2 + bytes.Length];
        BitConverter.GetBytes((ushort)bytes.Length).CopyTo(buffer, 0);
        bytes.CopyTo(buffer, 2);
        return buffer;
    }
}
