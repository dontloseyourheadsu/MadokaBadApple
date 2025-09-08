using System;
using System.IO;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

class Program
{
    static async Task Main(string[] args)
    {
        // 1) Pick input (arg or default) relative to the *current working directory*
        //    Put your file next to the .csproj OR pass a path: dotnet run -- ../videos/my.mp4
        var inputArg = args.Length > 0 ? args[0] : "madoka-bad-apple.mp4";
        var inputFile = Path.IsPathRooted(inputArg)
            ? inputArg
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), inputArg));

        // 2) Validate input early
        if (!File.Exists(inputFile))
        {
            Console.WriteLine("❌ Input file not found:");
            Console.WriteLine(inputFile);
            Console.WriteLine("\nTips:");
            Console.WriteLine(" • Run from the project folder where the video lives, OR pass a correct relative/absolute path.");
            Console.WriteLine(" • On Linux/macOS, file names are case-sensitive.");
            Console.WriteLine("\nCurrent directory contents:");
            foreach (var f in Directory.GetFiles(Directory.GetCurrentDirectory()))
                Console.WriteLine(" - " + f);
            return;
        }

        // 3) Output dir (next to your project)
        var outputDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "frames"));
        Directory.CreateDirectory(outputDir);

        // 4) FFmpeg binaries (download on first run)
        var ffDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg-binaries");
        Directory.CreateDirectory(ffDir);
        FFmpeg.SetExecutablesPath(ffDir);
        await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffDir);

        // 5) Build conversion (1 fps; change to taste, or remove for every frame)
        var pattern = Path.Combine(outputDir, "frame_%06d.png").Replace("\\", "/");
        var conversion = FFmpeg.Conversions.New();

        conversion.OnDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine(e.Data);
        };

        conversion = conversion
            .AddParameter("-hide_banner -loglevel info", ParameterPosition.PreInput)
            .AddParameter($"-i \"{inputFile}\"", ParameterPosition.PreInput) // input
            .AddParameter("-vf fps=1", ParameterPosition.PostInput)          // 1 frame/sec; remove for every frame
            .AddParameter("-f image2", ParameterPosition.PostInput)          // image sequence muxer
            .SetOverwriteOutput(true)
            .SetOutput($"\"{pattern}\"");

        Console.WriteLine("FFmpeg command:");
        Console.WriteLine(conversion.Build());

        Console.WriteLine("Extracting frames...");
        await conversion.Start();

        var sample = Path.Combine(outputDir, "frame_000001.png");
        Console.WriteLine(File.Exists(sample)
            ? $"✅ Done. Example frame: {sample}"
            : $"⚠️ No frames found in {outputDir}. Check the log above.");
    }
}
