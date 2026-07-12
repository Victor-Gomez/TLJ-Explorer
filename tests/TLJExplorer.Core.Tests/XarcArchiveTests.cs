using TLJExplorer.Core.FileSystem;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class XarcArchiveTests
{
    [Fact]
    public void LocaleFlag_ZeroMeansEnglish_OneMeansLocalized()
    {
        // Build a synthetic 2-entry archive: entry 0 is English (flag 0), entry 1 is localized (flag 1).
        // Layout: Int32 unknown, Int32 numFiles, Int32 baseOfs, then per entry: null-terminated name,
        // Int32 size, UInt32 localeFlag.
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);

        w.Write(0); // unknown
        w.Write(2); // numFiles
        w.Write(1024); // baseOfs

        WriteEntry(w, "hello.wav", size: 100, localeFlag: 0);
        WriteEntry(w, "hola.wav", size: 200, localeFlag: 1);

        stream.Position = 0;
        XarcArchive archive = XarcArchive.Read(stream);

        Assert.Equal(2, archive.Entries.Count);

        Assert.Equal("hello.wav", archive.Entries[0].Name);
        Assert.Equal(100, archive.Entries[0].Size);
        Assert.Equal(1024, archive.Entries[0].Offset);
        Assert.False(archive.Entries[0].IsLocalized);
        Assert.Equal(0u, archive.Entries[0].LocaleFlagRaw);

        Assert.Equal("hola.wav", archive.Entries[1].Name);
        Assert.Equal(200, archive.Entries[1].Size);
        Assert.Equal(1024 + 100, archive.Entries[1].Offset);
        Assert.True(archive.Entries[1].IsLocalized);
        Assert.Equal(1u, archive.Entries[1].LocaleFlagRaw);
    }

    [Fact]
    public void LocaleFlag_UnrecognizedValue_IsSurfacedRaw_AndDefaultsToNotLocalized()
    {
        // A hypothetical archive with a locale-flag byte we don't recognize (neither 0 nor 1). The archive
        // should still parse; downstream code can inspect LocaleFlagRaw to decide what to do.
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write(0);
        w.Write(1);
        w.Write(2048);
        WriteEntry(w, "mystery.dat", size: 42, localeFlag: 0xFF);

        stream.Position = 0;
        XarcArchive archive = XarcArchive.Read(stream);

        XarcEntry only = Assert.Single(archive.Entries);
        Assert.Equal(0xFFu, only.LocaleFlagRaw);
        Assert.False(only.IsLocalized);
    }

    private static void WriteEntry(BinaryWriter w, string name, int size, uint localeFlag)
    {
        foreach (char c in name)
            w.Write((byte)c);
        w.Write((byte)0);
        w.Write(size);
        w.Write(localeFlag);
    }
}
