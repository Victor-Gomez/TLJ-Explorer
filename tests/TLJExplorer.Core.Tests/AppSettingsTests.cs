using System.Text.Json;
using TLJExplorer.Core.Settings;
using Xunit;

namespace TLJExplorer.Core.Tests;

/// <summary>
/// Exercises <see cref="AppSettings"/>'s pure logic and JSON shape. Deliberately never calls
/// <see cref="AppSettings.Load"/>/<see cref="AppSettings.Save"/>, since those read/write the real
/// user's roaming AppData folder and would clobber their actual settings file.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void Defaults_MatchDocumentedValues()
    {
        var settings = new AppSettings();

        Assert.True(settings.AutoPlaySound);
        Assert.True(settings.AutoPlayVideo);
        Assert.True(settings.ShowMipMaps);
        Assert.True(settings.LoadAssetMods);
        Assert.False(settings.HighQuality);
        Assert.False(settings.RemoveVideoBackground);
        Assert.Equal(-1, settings.AntiAliasSamples);
        Assert.Equal("00FFFF", settings.VideoBackgroundColor);
        Assert.Equal("202020", settings.VideoOverlayColor);
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("Dark", settings.ModelViewerBackground);
        Assert.Empty(settings.RecentInstalls);
        Assert.Null(settings.ExternalModsDir);
        Assert.Null(settings.LastExportDir);
    }

    [Fact]
    public void RegisterRecentInstall_NewEntry_InsertsAtFront()
    {
        var settings = new AppSettings();
        settings.RegisterRecentInstall(@"C:\Games\TLJ1");
        settings.RegisterRecentInstall(@"C:\Games\TLJ2");

        Assert.Equal([@"C:\Games\TLJ2", @"C:\Games\TLJ1"], settings.RecentInstalls);
    }

    [Fact]
    public void RegisterRecentInstall_ExistingEntry_MovesToFrontInsteadOfDuplicating()
    {
        var settings = new AppSettings();
        settings.RegisterRecentInstall(@"C:\Games\TLJ1");
        settings.RegisterRecentInstall(@"C:\Games\TLJ2");
        settings.RegisterRecentInstall(@"C:\Games\TLJ1");

        Assert.Equal([@"C:\Games\TLJ1", @"C:\Games\TLJ2"], settings.RecentInstalls);
    }

    [Fact]
    public void RegisterRecentInstall_IsCaseInsensitiveOnDedupe()
    {
        var settings = new AppSettings();
        settings.RegisterRecentInstall(@"C:\Games\TLJ1");
        settings.RegisterRecentInstall(@"c:\games\tlj1");

        Assert.Single(settings.RecentInstalls);
    }

    [Fact]
    public void RegisterRecentInstall_MoreThanFiveEntries_TrimsToFive()
    {
        var settings = new AppSettings();
        for (int i = 0; i < 8; i++)
            settings.RegisterRecentInstall($@"C:\Games\TLJ{i}");

        Assert.Equal(5, settings.RecentInstalls.Count);
        Assert.Equal(@"C:\Games\TLJ7", settings.RecentInstalls[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RegisterRecentInstall_NullOrWhitespace_IsIgnored(string? install)
    {
        var settings = new AppSettings();
        settings.RegisterRecentInstall(install!);

        Assert.Empty(settings.RecentInstalls);
    }

    [Fact]
    public void Serialization_RoundTripsThroughJson()
    {
        var settings = new AppSettings
        {
            BaseDir = @"C:\Games\TLJ",
            AutoPlaySound = false,
            Theme = "Light",
            RecentInstalls = [@"C:\Games\TLJ", @"C:\Games\TLJ-Old"],
            LastSelectedPath = { [@"C:\Games\TLJ"] = @"\chapter1\sprite.xmg" },
        };

        string json = JsonSerializer.Serialize(settings);
        var roundTripped = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(settings.BaseDir, roundTripped.BaseDir);
        Assert.Equal(settings.AutoPlaySound, roundTripped.AutoPlaySound);
        Assert.Equal(settings.Theme, roundTripped.Theme);
        Assert.Equal(settings.RecentInstalls, roundTripped.RecentInstalls);
        Assert.Equal(settings.LastSelectedPath, roundTripped.LastSelectedPath);
    }

    [Fact]
    public void LastSelectedPath_KeyLookup_IsCaseInsensitive()
    {
        var settings = new AppSettings();
        settings.LastSelectedPath[@"C:\Games\TLJ"] = @"\a";

        Assert.True(settings.LastSelectedPath.ContainsKey(@"c:\games\tlj"));
    }
}
