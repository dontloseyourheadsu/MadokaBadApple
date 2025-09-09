using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace MadokaBadApple;

/// <summary>
/// Provides utilities for extracting video frames to disk using FFmpeg.
/// Handles FFmpeg binary download, frame rate detection/capping and cleanup of stale frames.
/// </summary>
public static class VideoFrameExtractor
{
    /// <summary>
    /// Ensures FFmpeg executables are present in a local folder so that extraction can run offline afterwards.
    /// Idempotent: safe to call multiple times.
    /// </summary>
    /// <param name="ffmpegDir">Directory where FFmpeg binaries should live.</param>
    public static async Task EnsureFfmpegAsync(string ffmpegDir)
    {
        Directory.CreateDirectory(ffmpegDir);
        FFmpeg.SetExecutablesPath(ffmpegDir);
        await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffmpegDir);
    }

    /// <summary>
    /// Extracts frames from a video file into the supplied directory as sequentially numbered PNG files.
    /// </summary>
    /// <param name="inputFile">Absolute or relative path to the input video file.</param>
    /// <param name="framesDir">Directory that will receive extracted frames (created if missing).</param>
    /// <param name="maxFpsCap">Optional maximum FPS to cap extraction at (default 60).</param>
    /// <returns>The FPS value that was actually used (rounded) for playback purposes.</returns>
    public static async Task<int> ExtractFramesAsync(string inputFile, string framesDir, double maxFpsCap = 60)
    {
        Directory.CreateDirectory(framesDir);

        // Remove previous frames to avoid mixing frame sets.
        foreach (var old in Directory.GetFiles(framesDir, "frame_*.png"))
        {
            try { File.Delete(old); } catch { /* ignore */ }
        }

        // Determine FPS from media info.
        double extractFps = 24; // default fallback
        try
        {
            var mediaInfo = await FFmpeg.GetMediaInfo(inputFile);
            var videoStream = mediaInfo.VideoStreams?.FirstOrDefault();
            if (videoStream != null && videoStream.Framerate > 0)
            {
                extractFps = videoStream.Framerate;
                if (extractFps > maxFpsCap) extractFps = maxFpsCap;
            }
        }
        catch { /* use fallback */ }

        var pattern = Path.Combine(framesDir, "frame_%06d.png").Replace("\\", "/");
        var conversion = FFmpeg.Conversions.New()
            .AddParameter("-hide_banner -loglevel quiet", ParameterPosition.PreInput)
            .AddParameter($"-i \"{inputFile}\"", ParameterPosition.PreInput)
            .AddParameter($"-vf fps={extractFps:0.####}", ParameterPosition.PostInput)
            .AddParameter("-f image2", ParameterPosition.PostInput)
            .SetOverwriteOutput(true)
            .SetOutput($"\"{pattern}\"");

        // Minimal loading spinner (only non-frame console output permitted).
        var loadingTask = conversion.Start();
        string[] loadingStates = ["Loading", "Loading.", "Loading..", "Loading..."];
        int li = 0;
        while (!loadingTask.IsCompleted)
        {
            try
            {
                Console.SetCursorPosition(0, 0);
                Console.Write(loadingStates[li++ % loadingStates.Length] + "   ");
            }
            catch { }
            await Task.Delay(250);
        }
        await loadingTask;
        try { Console.Clear(); } catch { }

        return (int)Math.Round(extractFps);
    }
}
