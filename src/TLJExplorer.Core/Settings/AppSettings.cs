using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TLJExplorer.Core.Settings;

/// <summary>
/// Persisted application preferences: a small JSON document stored under the user's roaming
/// application data folder.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>The last-used game data/installation base directory, if any.</summary>
    public string? BaseDir { get; set; }

    /// <summary>Whether to automatically play sound resources when they're opened/selected.</summary>
    public bool AutoPlaySound { get; set; } = true;

    /// <summary>Whether to render/show mipmaps for texture previews.</summary>
    public bool ShowMipMaps { get; set; } = true;

    /// <summary>
    /// Anti-aliasing sample count for 3D previews. -1 means "use the default"; otherwise typically 0, 2, or 4.
    /// </summary>
    public int AntiAliasSamples { get; set; } = -1;

    /// <summary>Whether to prefer higher-quality (slower) rendering for previews.</summary>
    public bool HighQuality { get; set; }

    /// <summary>
    /// Full path to the <c>ffmpeg.exe</c> executable, used to transcode Smacker (.sss) and Bink (.bbb)
    /// videos to MP4 on demand so they can play in the app's embedded WPF MediaElement. Defaults to the
    /// <c>ffmpeg\ffmpeg.exe</c> subfolder next to the app's own exe.
    /// </summary>
    public string FfmpegPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    /// <summary>Whether to strip the chroma-key background (default: cyan) from video cutscenes.</summary>
    public bool RemoveVideoBackground { get; set; }

    /// <summary>Chroma-key color to remove when <see cref="RemoveVideoBackground"/> is on. Hex like <c>00FFFF</c>.</summary>
    public string VideoBackgroundColor { get; set; } = "00FFFF";

    /// <summary>Color the chroma-keyed video is composited over. Should match the app panel color so the
    /// video rectangle visually merges into the surrounding UI. Hex like <c>202020</c>.</summary>
    public string VideoOverlayColor { get; set; } = "202020";

    /// <summary>UI theme: <c>System</c>, <c>Light</c>, or <c>Dark</c>. Maps to WPF's <c>ThemeMode</c>.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>
    /// When true, every scene-folder load also writes a per-scene diagnostic report to
    /// <c>%TEMP%\TLJExplorer_last_scene_items.txt</c>: every Item's subtype/enabled/position/asset plus
    /// every item-enable script call in the tree. Off by default; useful when a scene renders the wrong
    /// sprite or picks a mid-animation frame.
    /// </summary>
    public bool DumpSceneDiagnostics { get; set; }

    /// <summary>
    /// When true, the tree hides XARC entries whose per-entry locale flag marks them as localised
    /// (non-English). Off by default. See <see cref="TLJExplorer.Core.FileSystem.FsNode.IsLocalized"/>.
    /// </summary>
    public bool HideLocalizedEntries { get; set; }

    /// <summary>
    /// The folder Export / Batch Export dialogs default to on next open. Set to whichever folder the user
    /// last exported into. <c>null</c> means "let the OS pick the default".
    /// </summary>
    public string? LastExportDir { get; set; }

    /// <summary>Auto-play video resources on selection, mirroring <see cref="AutoPlaySound"/>.</summary>
    public bool AutoPlayVideo { get; set; } = true;

    /// <summary>Loop sound playback on end-of-file. Off by default.</summary>
    public bool LoopSoundPlayback { get; set; }

    /// <summary>Model viewer background: <c>Dark</c> (default), <c>Light</c>, or <c>Transparent</c>.</summary>
    public string ModelViewerBackground { get; set; } = "Dark";

    /// <summary>Show a wireframe overlay on top of the shaded mesh in the model viewer.</summary>
    public bool ModelViewerWireframe { get; set; }

    /// <summary>Most-recently-used install folders, in reverse-chronological order. Capped at 5 entries.</summary>
    public List<string> RecentInstalls { get; set; } = [];

    /// <summary>
    /// Last-selected path per install folder (keyed by <see cref="BaseDir"/> at save time). Restored on
    /// next launch so users pick up exactly where they left off.
    /// </summary>
    public Dictionary<string, string> LastSelectedPath { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Load asset mods — files sitting in an archive's <c>xarc\</c> sibling folder — as overrides for
    /// the archived entries with the same name (matching ScummVM Stark's mod convention). On by default:
    /// most users who dropped mod files in place want them applied. Toggle off to view the original
    /// assets even when mods are present.
    /// </summary>
    public bool LoadAssetMods { get; set; } = true;

    /// <summary>
    /// Optional root of an external mods folder. When set, VFS scans this directory (mirroring the
    /// install's layout: <c>&lt;modsDir&gt;/&lt;relativePath&gt;/xarc/&lt;name&gt;</c>) for override files
    /// in addition to any files sitting inside the install itself. Lets modders keep their work outside
    /// the game directory so it can be swapped or version-controlled independently. <c>null</c> means
    /// "no external folder — only look inside the install".
    /// </summary>
    public string? ExternalModsDir { get; set; }

    /// <summary>
    /// Registers <paramref name="install"/> as the most-recent install and trims the list to 5 entries.
    /// Case-insensitive dedupe on absolute path; existing entries move to the front.
    /// </summary>
    public void RegisterRecentInstall(string install)
    {
        if (string.IsNullOrWhiteSpace(install))
            return;

        RecentInstalls.RemoveAll(p => string.Equals(p, install, StringComparison.OrdinalIgnoreCase));
        RecentInstalls.Insert(0, install);
        if (RecentInstalls.Count > 5)
            RecentInstalls.RemoveRange(5, RecentInstalls.Count - 5);
    }

    /// <summary>
    /// Loads settings from <c>%APPDATA%\TLJExplorer\settings.json</c>. If the file is missing, unreadable,
    /// or fails to deserialize, this returns a default-constructed <see cref="AppSettings"/> instead of
    /// throwing.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            string path = GetSettingsFilePath();
            if (!File.Exists(path))
                return new AppSettings();

            string json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            return settings ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes these settings to <c>%APPDATA%\TLJExplorer\settings.json</c>, creating the containing
    /// directory if it doesn't already exist.
    /// </summary>
    public void Save()
    {
        string path = GetSettingsFilePath();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(this, SerializerOptions);
        File.WriteAllText(path, json);
    }

    private static string GetSettingsFilePath()
    {
        string appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataDir, "TLJExplorer", "settings.json");
    }
}
