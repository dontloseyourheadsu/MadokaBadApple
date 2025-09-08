using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using Silk.NET.OpenAL;
using System.Threading;

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

        // --------------------  PLAY AUDIO WITH PROGRESS  --------------------
        Console.WriteLine("\nPlaying audio (press Ctrl+C to abort)...");
        try
        {
            PlayWavOpenALWithProgress(wavPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Playback error: " + ex.Message);
        }

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

    // OpenAL playback with second counter clearing the terminal output.
    static void PlayWavOpenALWithProgress(string wavPath)
    {
        var wav = LoadPcmWav(wavPath);
        short channels = wav.channels;
        int sampleRate = wav.sampleRate;
        short bitsPerSample = wav.bitsPerSample;
        byte[] pcm = wav.pcmData;

        if (pcm.Length == 0) throw new InvalidOperationException("No PCM data found in WAV.");

        var alc = ALContext.GetApi();
        var al = AL.GetApi();

        unsafe
        {
            var device = alc.OpenDevice(null);
            if (device == null) throw new Exception("Failed to open default audio device.");
            var context = alc.CreateContext(device, null);
            alc.MakeContextCurrent(context);

            uint buffer = al.GenBuffer();
            uint source = al.GenSource();

            var format = (channels, bitsPerSample) switch
            {
                (1, 16) => BufferFormat.Mono16,
                (2, 16) => BufferFormat.Stereo16,
                _ => throw new NotSupportedException("Unsupported channel/bit depth.")
            };

            fixed (byte* p = pcm)
            {
                al.BufferData(buffer, format, p, pcm.Length, sampleRate);
            }

            al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
            al.SourcePlay(source);

            double totalSeconds = (double)pcm.Length / (channels * (bitsPerSample / 8) * sampleRate);
            DateTime start = DateTime.UtcNow;
            SourceState state;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                al.SourceStop(source);
            };

            Console.WriteLine(); // ensure we have at least one line to rewrite
            do
            {
                al.GetSourceProperty(source, GetSourceInteger.SourceState, out int rawState);
                state = (SourceState)rawState;
                double elapsed = (DateTime.UtcNow - start).TotalSeconds;
                if (elapsed > totalSeconds) elapsed = totalSeconds;

                // In-place update (carriage return). Pad to overwrite previous longer text.
                string line = $"Playing audio... {elapsed:0.00}s / {totalSeconds:0.00}s (Ctrl+C to stop)";
                int pad = Console.WindowWidth - line.Length - 1;
                if (pad < 0) pad = 0;
                Console.Write("\r" + line + new string(' ', pad));
                Thread.Sleep(100);
            }
            while (state == SourceState.Playing);

            Console.WriteLine();

            al.SourceStop(source);
            al.DeleteSource(source);
            al.DeleteBuffer(buffer);
            alc.DestroyContext(context);
            alc.CloseDevice(device);
            Console.WriteLine("Playback finished.");
        }
    }

    // Robust WAV parser: scans chunks to find 'fmt ' and 'data'. Supports standard PCM (format code 1) 16-bit.
    private static (short channels, int sampleRate, short bitsPerSample, byte[] pcmData) LoadPcmWav(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (br.BaseStream.Length < 12) throw new InvalidOperationException("File too small for RIFF header");
        string riff = new string(br.ReadChars(4));
        br.ReadInt32(); // file size minus 8
        string wave = new string(br.ReadChars(4));
        if (riff != "RIFF" || wave != "WAVE") throw new InvalidOperationException("Not a RIFF/WAVE file");

        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? pcm = null;
        bool fmtSeen = false;

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            string chunkId = new string(br.ReadChars(4));
            int chunkSize = br.ReadInt32();
            if (chunkSize < 0 || br.BaseStream.Position + chunkSize > br.BaseStream.Length)
            {
                // Corrupt size - abort
                break;
            }

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16) throw new InvalidOperationException("fmt chunk too small");
                short audioFormat = br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                int byteRate = br.ReadInt32(); // unused
                short blockAlign = br.ReadInt16(); // unused
                bitsPerSample = br.ReadInt16();
                int remaining = chunkSize - 16;
                if (remaining > 0) br.BaseStream.Seek(remaining, SeekOrigin.Current); // skip any extra fmt bytes
                if (audioFormat != 1) throw new NotSupportedException($"Unsupported WAV format code {audioFormat}, only PCM (1) supported.");
                fmtSeen = true;
            }
            else if (chunkId == "data")
            {
                if (!fmtSeen)
                {
                    // Must read fmt first per spec, but continue if order swapped
                }
                pcm = br.ReadBytes(chunkSize);
            }
            else
            {
                // Skip other chunk (LIST, fact, etc.)
                br.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }

            // Chunks are padded to even size
            if ((chunkSize & 1) == 1 && br.BaseStream.Position < br.BaseStream.Length)
                br.BaseStream.Seek(1, SeekOrigin.Current);

            if (pcm != null && fmtSeen) break; // we have what we need
        }

        if (!fmtSeen) throw new InvalidOperationException("Missing fmt chunk");
        if (pcm == null) throw new InvalidOperationException("Missing data chunk");
        if (bitsPerSample != 16) throw new NotSupportedException("Only 16-bit PCM supported");
        if (channels != 1 && channels != 2) throw new NotSupportedException("Only mono or stereo supported");

        return (channels, sampleRate, bitsPerSample, pcm);
    }
}
