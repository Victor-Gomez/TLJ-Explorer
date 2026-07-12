using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TLJExplorer.Services;

/// <summary>Result of a transcode attempt.</summary>
public readonly record struct TranscodeResult(string? OutputPath, string? ErrorDetail)
{
    public bool Success => OutputPath is not null;
    public static TranscodeResult Failed(string detail) => new(null, detail);
    public static TranscodeResult Ok(string path) => new(path, null);
}

/// <summary>
/// Runs the bundled <c>ffmpeg.exe</c> to transcode a source video to an MP4 that WPF's MediaElement can
/// play natively via Media Foundation.
/// </summary>
public static class FfmpegTranscoder
{
    /// <summary>Transcodes to a fresh temp MP4 (H.264 + AAC). Set <paramref name="removeBackgroundColor"/>
    /// to a hex string like <c>"00E5D9"</c> to chroma-key that color out and composite onto
    /// <paramref name="overlayColor"/> (also hex, defaults to <c>202020</c> so the result visually merges
    /// into the app's dark panel background).</summary>
    public static TranscodeResult TranscodeToMp4(
        string inputPath,
        string ffmpegPath,
        TempFileTracker tempFiles,
        string? removeBackgroundColor = null,
        string overlayColor = "202020")
    {
        if (!File.Exists(ffmpegPath))
            return TranscodeResult.Failed($"ffmpeg.exe not found at:\n{ffmpegPath}");

        string outputPath = tempFiles.CreateTempFile(".mp4");

        // Even-dimensions requirement for libx264+yuv420p; some Bink cutscenes are 640x365 etc.
        string sizeFilter = "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        string videoFilter;
        if (!string.IsNullOrWhiteSpace(removeBackgroundColor))
        {
            // Chroma-key the specified color out and composite onto the panel-matching overlay color, so
            // the resulting rectangle blends into the app's background. `overlay=shortest=1` is essential:
            // the `color` source has infinite duration, so without it overlay hangs after the video ends.
            // Softer similarity+blend values reduce the fringe left around subject edges.
            videoFilter =
                $"[0:v]chromakey=color=0x{removeBackgroundColor}:similarity=0.22:blend=0.15[ck];" +
                $"color=c=0x{overlayColor}:s=1920x1080[bg];" +
                "[bg][ck]scale2ref=iw:ih[bg2][ck2];" +
                $"[bg2][ck2]overlay=shortest=1,{sizeFilter}[out]";
        }
        else
        {
            videoFilter = string.Empty;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        if (videoFilter.Length > 0)
        {
            psi.ArgumentList.Add("-filter_complex");
            psi.ArgumentList.Add(videoFilter);
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("[out]");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:a?");
        }
        else
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(sizeFilter);
        }
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("veryfast");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(outputPath);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return TranscodeResult.Failed("Failed to start ffmpeg.exe (Process.Start returned null).");

            // Ensure the child dies with the parent if the app crashes or is killed mid-transcode.
            ChildProcessTracker.AddProcess(process);

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            process.WaitForExit();
            string stderr = stderrTask.Result;

            if (process.ExitCode != 0)
                return TranscodeResult.Failed($"ffmpeg exit code {process.ExitCode}.\n\n{TailLines(stderr, 20)}");

            if (!File.Exists(outputPath))
                return TranscodeResult.Failed($"ffmpeg reported success but no output file was written.\n\n{TailLines(stderr, 20)}");

            return TranscodeResult.Ok(outputPath);
        }
        catch (Exception ex)
        {
            return TranscodeResult.Failed($"Exception launching ffmpeg: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Result of a frame-extraction attempt.</summary>
    public readonly record struct FrameExtractResult(
        IReadOnlyList<string> FramePaths,
        double Fps,
        string? ErrorDetail)
    {
        public bool Success => ErrorDetail is null;

        public static FrameExtractResult Failed(string detail) => new([], 0, detail);

        public static FrameExtractResult Ok(IReadOnlyList<string> paths, double fps) => new(paths, fps, null);
    }

    /// <summary>
    /// Runs ffmpeg to extract every frame of <paramref name="inputPath"/> as a PNG (RGBA when the source
    /// has alpha, RGB otherwise) into <paramref name="outputDirectory"/>. Returns the frame paths in order
    /// plus the detected source framerate; on failure returns an <see cref="FrameExtractResult"/> with
    /// <see cref="FrameExtractResult.Success"/> false. The output directory is not created by this method.
    /// </summary>
    public static FrameExtractResult ExtractFrames(
        string inputPath,
        string ffmpegPath,
        string outputDirectory,
        double fallbackFps = 15.0)
    {
        if (!File.Exists(ffmpegPath))
            return FrameExtractResult.Failed($"ffmpeg.exe not found at:\n{ffmpegPath}");

        string outputPattern = Path.Combine(outputDirectory, "frame_%05d.png");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-vsync");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add("colorkey=0x00FFFF:0.05:0.0");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("rgba");
        psi.ArgumentList.Add(outputPattern);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return FrameExtractResult.Failed("Failed to start ffmpeg.exe (Process.Start returned null).");

            ChildProcessTracker.AddProcess(process);

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            process.WaitForExit();
            string stderr = stderrTask.Result;

            if (process.ExitCode != 0)
                return FrameExtractResult.Failed($"ffmpeg exit code {process.ExitCode}.\n\n{TailLines(stderr, 20)}");

            string[] frames = Directory.GetFiles(outputDirectory, "frame_*.png");
            Array.Sort(frames, StringComparer.OrdinalIgnoreCase);

            if (frames.Length == 0)
                return FrameExtractResult.Failed($"ffmpeg reported success but no frames were written.\n\n{TailLines(stderr, 20)}");

            double fps = ParseFps(stderr) ?? fallbackFps;
            return FrameExtractResult.Ok(frames, fps);
        }
        catch (Exception ex)
        {
            return FrameExtractResult.Failed($"Exception launching ffmpeg: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts the source frame rate from ffmpeg's stderr banner. Matches the "<c>NN fps</c>" or
    /// "<c>NN.NN fps</c>" that follows the video stream line. Returns null if the banner didn't include
    /// one -- callers substitute a sensible default.
    /// </summary>
    private static double? ParseFps(string stderr)
    {
        // "Stream #0:0: Video: bink, ..., 640x480, 15 fps, ..." or similar. Prefer the "fps" reading
        // over "tbr" because tbr is only a "best guess" tick rate and can be far off for old codecs.
        Match m = Regex.Match(stderr, @"(\d+(?:\.\d+)?)\s*fps", RegexOptions.CultureInvariant);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) && fps > 0)
            return fps;

        return null;
    }

    private static string TailLines(string text, int lineCount)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length <= lineCount
            ? text.Trim()
            : string.Join('\n', lines[^lineCount..]).Trim();
    }
}
