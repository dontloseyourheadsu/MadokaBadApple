using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

class Program
{
    static async Task Main(string[] args)
    {
        // (1) Resolve input path (arg or default) relative to current working directory
        var inputArg = args.Length > 0 ? args[0] : "madoka-bad-apple.mp4";
        var inputFile = Path.IsPathRooted(inputArg)
            ? inputArg
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), inputArg));

        // (2) Validate early
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

        // (3) Prepare output dirs
        var framesDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "frames"));
        Directory.CreateDirectory(framesDir);
        var audioDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "audio"));
        Directory.CreateDirectory(audioDir);

        // (4) Ensure FFmpeg executables (multi-platform) are present
        var ffDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg-binaries");
        Directory.CreateDirectory(ffDir);
        FFmpeg.SetExecutablesPath(ffDir);
        await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffDir);

        // --------------------  VIDEO FRAME EXTRACTION  --------------------
        var pattern = Path.Combine(framesDir, "frame_%06d.png").Replace("\\", "/");
        var frameConversion = FFmpeg.Conversions.New();
        frameConversion.OnDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Console.WriteLine(e.Data);
        };

        frameConversion = frameConversion
            .AddParameter("-hide_banner -loglevel info", ParameterPosition.PreInput)
            .AddParameter($"-i \"{inputFile}\"", ParameterPosition.PreInput)
            .AddParameter("-vf fps=1", ParameterPosition.PostInput)  // change/remove for different sampling
            .AddParameter("-f image2", ParameterPosition.PostInput)
            .SetOverwriteOutput(true)
            .SetOutput($"\"{pattern}\"");

        Console.WriteLine("FFmpeg command (frames):");
        Console.WriteLine(frameConversion.Build());
        Console.WriteLine("Extracting frames...");
        await frameConversion.Start();

        var sampleFrame = Path.Combine(framesDir, "frame_000001.png");
        Console.WriteLine(File.Exists(sampleFrame)
            ? $"✅ Frames extracted. Example: {sampleFrame}"
            : $"⚠️ No frames found in {framesDir}.");

        // --------------------  AUDIO EXTRACTION  --------------------
        Console.WriteLine("\nExtracting audio track...");
        var mediaInfo = await FFmpeg.GetMediaInfo(inputFile);
        var audioStream = mediaInfo.AudioStreams?.FirstOrDefault();
        if (audioStream == null)
        {
            Console.WriteLine("⚠️ No audio stream present; skipping audio analysis.");
            return; // nothing else to do
        }

        // Force mono 44.1 kHz PCM WAV for simple analysis
        var wavPath = Path.Combine(audioDir, "extracted.wav");
        var audioConversion = FFmpeg.Conversions.New()
            .AddStream(audioStream)
            .AddParameter("-ac 1 -ar 44100 -sample_fmt s16", ParameterPosition.PostInput) // mono 44.1k 16-bit
            .SetOverwriteOutput(true)
            .SetOutput(wavPath);

        audioConversion.OnDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Console.WriteLine(e.Data);
        };

        Console.WriteLine("FFmpeg command (audio):");
        Console.WriteLine(audioConversion.Build());
        await audioConversion.Start();

        if (!File.Exists(wavPath))
        {
            Console.WriteLine("❌ Audio extraction failed.");
            return;
        }
        Console.WriteLine($"✅ Audio extracted: {wavPath}");

        // --------------------  SIMPLE AUDIO ANALYSIS  --------------------
        Console.WriteLine("\nAnalyzing first ~5s (100ms windows)...");
        try
        {
            AnalyzeWavApprox(wavPath, maxSeconds: 5);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Audio analysis error: " + ex.Message);
        }

        Console.WriteLine("\nDone.");
    }

    // Lightweight approximate frequency + RMS analysis using zero crossing per 100ms window.
    // This is intentionally simple: shows variation but is not a spectral (FFT) analysis.
    static void AnalyzeWavApprox(string wavPath, int maxSeconds)
    {
        using var fs = File.OpenRead(wavPath);
        if (fs.Length < 44)
        {
            Console.WriteLine("WAV too small.");
            return;
        }

        byte[] header = new byte[44];
        fs.Read(header, 0, 44);

        // Parse basic PCM header fields (little-endian)
        int sampleRate = BitConverter.ToInt32(header, 24);
        short channels = BitConverter.ToInt16(header, 22);
        short bitsPerSample = BitConverter.ToInt16(header, 34);
        int dataSize = BitConverter.ToInt32(header, 40);

        if (channels != 1 || bitsPerSample != 16)
        {
            Console.WriteLine($"Unexpected WAV format (channels={channels}, bits={bitsPerSample}). Expected mono 16-bit.");
            return;
        }

        int bytesPerSample = bitsPerSample / 8; // 2
        int totalSamples = dataSize / bytesPerSample;
        int maxSamples = Math.Min(totalSamples, sampleRate * maxSeconds);

        byte[] sampleBytes = new byte[maxSamples * bytesPerSample];
        int actuallyRead = fs.Read(sampleBytes, 0, sampleBytes.Length);
        int samplesRead = actuallyRead / bytesPerSample;
        if (samplesRead == 0)
        {
            Console.WriteLine("No sample data read.");
            return;
        }

        int window = sampleRate / 10; // ~100ms
        if (window < 100) window = 100; // safety

        for (int offset = 0; offset + window < samplesRead; offset += window)
        {
            int zeroCross = 0;
            double rmsAccum = 0;
            short prev = BitConverter.ToInt16(sampleBytes, offset * bytesPerSample);

            for (int i = offset + 1; i < offset + window; i++)
            {
                short cur = BitConverter.ToInt16(sampleBytes, i * bytesPerSample);
                if ((prev >= 0 && cur < 0) || (prev < 0 && cur >= 0)) zeroCross++;
                double norm = cur / 32768.0; // Normalize 16-bit
                rmsAccum += norm * norm;
                prev = cur;
            }

            double rms = Math.Sqrt(rmsAccum / window);
            double freqEstimate = (zeroCross / 2.0) * (sampleRate / (double)window); // crude
            double tStart = offset / (double)sampleRate;
            Console.WriteLine($"t={tStart,5:0.00}s  freq≈{freqEstimate,6:0}Hz  rms={rms:0.00}");
        }
    }
}
