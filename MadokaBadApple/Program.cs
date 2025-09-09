namespace MadokaBadApple;

class Program
{
    static async Task Main(string[] args)
    {
        // 1. Resolve input video path (CLI arg or first video found in current directory)
        string? inputFile = null;
        if (args.Length > 0)
        {
            var inputArg = args[0];
            inputFile = Path.IsPathRooted(inputArg)
                ? inputArg
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), inputArg));
        }
        else
        {
            string cwd = Directory.GetCurrentDirectory();
            string[] videoExts = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v"]; // simple set, no recursion
            inputFile = Directory.GetFiles(cwd)
                                 .Where(f => videoExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            return; // No video found/provided, exit silently

        // 2. Prepare output directories (frames + audio)
        var framesDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "frames"));
        Directory.CreateDirectory(framesDir);
        var audioDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "audio"));
        Directory.CreateDirectory(audioDir);

        // 3. Ensure FFmpeg binaries exist locally (download if missing)
        var ffDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg-binaries");
        await VideoFrameExtractor.EnsureFfmpegAsync(ffDir);

        // 4. Extract video frames (returns chosen FPS for playback)
        int playbackFps = await VideoFrameExtractor.ExtractFramesAsync(inputFile, framesDir, maxFpsCap: 60);

        // 5. Extract audio to WAV (mono 44.1kHz). Skip if no audio stream.
        string? wavPath = await AudioExtractor.ExtractAudioAsync(inputFile, audioDir);
        if (wavPath == null) return; // silent exit if no audio

        // 6. Playback ASCII video synchronized with audio
        try
        {
            AsciiVideoPlayer.Play(wavPath, framesDir, playbackFps);
        }
        catch (Exception)
        {
        }

        // 7. Optional lightweight audio analysis (silent)
        try
        {
            AudioExtractor.AnalyzeWavApprox(wavPath, maxSeconds: 5);
        }
        catch (Exception)
        {
        }
    }
}
