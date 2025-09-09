using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using Silk.NET.OpenAL;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MadokaBadApple;

class Program
{
    // Ordered from darkest to lightest (70 chars) for grayscale mapping
    // Need to escape backslash and double quote inside the C# string. Sequence length 70.
    private const string ASCIICHARS = "$@B%8&WM#*oahkbdpqwmZO0QLCJUYXzcvunxrjft/\\|()1{}[]?-_+~<>i!lI;:,\"^`'. ";

    static async Task Main(string[] args)
    {
        // Resolve input path (arg or first video in current directory) without printing anything except frames later.
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

        // --------------------  MEDIA INFO (video+audio)  --------------------
        var mediaInfo = await FFmpeg.GetMediaInfo(inputFile);
        var videoStream = mediaInfo.VideoStreams?.FirstOrDefault();
        double extractFps = 24; // fallback
        try
        {
            if (videoStream != null && videoStream.Framerate > 0)
            {
                extractFps = videoStream.Framerate;
                if (extractFps > 60) extractFps = 60; // cap to avoid huge frame dumps
            }
        }
        catch { /* keep fallback */ }

        // Clean old extracted frames so leftover slow (1 fps) frames don't remain
        foreach (var old in Directory.GetFiles(framesDir, "frame_*.png"))
        {
            try { File.Delete(old); } catch { }
        }

        // --------------------  VIDEO FRAME EXTRACTION  --------------------
        var pattern = Path.Combine(framesDir, "frame_%06d.png").Replace("\\", "/");
        var frameConversion = FFmpeg.Conversions.New();

        frameConversion = frameConversion
            .AddParameter("-hide_banner -loglevel quiet", ParameterPosition.PreInput)
            .AddParameter($"-i \"{inputFile}\"", ParameterPosition.PreInput)
            .AddParameter($"-vf fps={extractFps:0.####}", ParameterPosition.PostInput)
            .AddParameter("-f image2", ParameterPosition.PostInput)
            .SetOverwriteOutput(true)
            .SetOutput($"\"{pattern}\"");
        // Loading animation (only permitted non-frame output). No newlines.
        var loadingTask = frameConversion.Start();
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
        await loadingTask; // ensure completion
        try { Console.Clear(); } catch { }

        var sampleFrame = Path.Combine(framesDir, "frame_000001.png");
        // Silent: do not print extraction result

        // --------------------  AUDIO EXTRACTION  --------------------
        var audioStream = mediaInfo.AudioStreams?.FirstOrDefault();
        if (audioStream == null)
        {
            return; // nothing else to do
        }

        // Force mono 44.1 kHz PCM WAV for simple analysis
        var wavPath = Path.Combine(audioDir, "extracted.wav");
        var audioConversion = FFmpeg.Conversions.New()
            .AddStream(audioStream)
            .AddParameter("-ac 1 -ar 44100 -sample_fmt s16", ParameterPosition.PostInput) // mono 44.1k 16-bit
            .SetOverwriteOutput(true)
            .SetOutput(wavPath);

        // Silence audio conversion logs
        audioConversion.AddParameter("-hide_banner -loglevel quiet", ParameterPosition.PreInput);

        await audioConversion.Start();

        if (!File.Exists(wavPath)) return; // silent failure

        // --------------------  PLAY AUDIO + ASCII VIDEO  --------------------
        try
        {
            // Use the extraction FPS for smoother playback; if something went wrong fallback to even distribution (targetFps<=0)
            int playbackFps = extractFps > 0 ? (int)Math.Round(extractFps) : 0;
            PlayAsciiVideoWithAudio(wavPath, framesDir, targetFps: playbackFps);
        }
        catch (Exception)
        {
        }

        // --------------------  SIMPLE AUDIO ANALYSIS  --------------------
        try
        {
            AnalyzeWavApprox(wavPath, maxSeconds: 5);
        }
        catch (Exception)
        {
        }
    }

    // Lightweight approximate frequency + RMS analysis using zero crossing per 100ms window.
    // This is intentionally simple: shows variation but is not a spectral (FFT) analysis.
    static void AnalyzeWavApprox(string wavPath, int maxSeconds)
    {
        using var fs = File.OpenRead(wavPath);
        if (fs.Length < 44)
        {
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
        }
    }

    // Audio + ASCII video synchronized playback.
    static void PlayAsciiVideoWithAudio(string wavPath, string framesDir, int targetFps)
    {
        var wav = LoadPcmWav(wavPath);
        short channels = wav.channels;
        int sampleRate = wav.sampleRate;
        short bitsPerSample = wav.bitsPerSample;
        byte[] pcm = wav.pcmData;
        if (pcm.Length == 0) throw new InvalidOperationException("No PCM data found in WAV.");

        // Gather frames list
        var frames = Directory.GetFiles(framesDir, "frame_*.png").OrderBy(f => f).ToArray();
        if (frames.Length == 0) return; // silent

        // frameDuration will be determined later (after we know audio totalSeconds)

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
            if (totalSeconds <= 0)
            {
                // Fallback: assume 24fps clip length from frame count
                totalSeconds = frames.Length / 24.0;
            }

            double frameDuration = targetFps > 0
                ? 1.0 / targetFps
                : totalSeconds / frames.Length; // evenly spread frames across audio
            SourceState state;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                al.SourceStop(source);
            };

            int lastRendered = -1;
            // Precompute console target size (allow user to resize between frames but we'll adapt basics)
            while (true)
            {
                al.GetSourceProperty(source, GetSourceInteger.SourceState, out int rawState);
                state = (SourceState)rawState;
                double elapsed = (DateTime.UtcNow - start).TotalSeconds;
                if (elapsed > totalSeconds) elapsed = totalSeconds;
                int frameIndex = (int)(elapsed / frameDuration);
                if (frameIndex >= frames.Length) frameIndex = frames.Length - 1;

                if (frameIndex != lastRendered)
                {
                    // Load & convert frame
                    try
                    {
                        string ascii = FrameToAscii(frames[frameIndex], Console.WindowWidth, Console.WindowHeight); // full height
                        Console.SetCursorPosition(0, 0);
                        Console.Write(ascii); // only print frames
                    }
                    catch (Exception)
                    {
                        // Suppress frame load errors
                    }
                    lastRendered = frameIndex;
                }

                if (state != SourceState.Playing && frameIndex >= frames.Length - 1) break;
                int sleep = (int)(frameDuration * 1000 / 3); // poll ~3x per frame for timing
                if (sleep < 1) sleep = 1;
                Thread.Sleep(sleep);
            }

            al.SourceStop(source);
            al.DeleteSource(source);
            al.DeleteBuffer(buffer);
            alc.DestroyContext(context);
            alc.CloseDevice(device);
        }
    }

    static string FrameToAscii(string path, int consoleWidth, int consoleHeight)
    {
        if (consoleWidth < 10 || consoleHeight < 5) return "[Console too small]";
        // Using ImageSharp to load and resize preserving aspect ratio. Terminal character cells are roughly twice as tall as wide.
        // We'll adjust height by scaling image height to (targetHeight * (charAspect)). charAspect ~0.5 (since char is taller) so use factor.
        double charAspect = 0.5; // width/height ratio approximate
        int targetWidth = Math.Max(8, consoleWidth);
        int targetHeight = Math.Max(4, (int)(consoleHeight / 1.0));

        using Image<Rgba32> img = Image.Load<Rgba32>(path);
        double imgAspect = img.Width / (double)img.Height;
        // Fit to width
        int resizedWidth = targetWidth;
        int resizedHeight = (int)(resizedWidth / imgAspect * charAspect);
        if (resizedHeight > targetHeight)
        {
            resizedHeight = targetHeight;
            resizedWidth = (int)(resizedHeight * imgAspect / charAspect);
        }
        if (resizedWidth < 8 || resizedHeight < 4) return "[Frame too small after resize]";

        img.Mutate(c => c.Resize(new ResizeOptions
        {
            Size = new Size(resizedWidth, resizedHeight),
            Mode = ResizeMode.Stretch
        }).Grayscale());

        var sb = new System.Text.StringBuilder(resizedHeight * (resizedWidth + 1));
        for (int y = 0; y < resizedHeight; y++)
        {
            for (int x = 0; x < resizedWidth; x++)
            {
                var p = img[x, y];
                // Convert to luminance
                double lum = (0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B) / 255.0; // 0..1
                // Map luminance (0=dark) to char index (0=darkest char). Optionally invert depending on taste.
                int idx = (int)((ASCIICHARS.Length - 1) * lum);
                if (idx < 0) idx = 0; else if (idx >= ASCIICHARS.Length) idx = ASCIICHARS.Length - 1;
                sb.Append(ASCIICHARS[idx]);
            }
            sb.Append('\n');
        }
        return sb.ToString();
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
