using Silk.NET.OpenAL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TerminalVideoPlayer;

/// <summary>
/// Handles playback of extracted video frames as ASCII art synchronized with a WAV audio track.
/// Includes internal helpers for loading PCM WAV data and converting images to ASCII.
/// </summary>
public static class AsciiVideoPlayer
{
    // Ordered from darkest to lightest (70 chars) for grayscale mapping.
    private const string ASCIICHARS = "$@B%8&WM#*oahkbdpqwmZO0QLCJUYXzcvunxrjft/\\|()1{}[]?-_+~<>i!lI;:,\"^`'. ";

    /// <summary>
    /// Plays audio from <paramref name="wavPath"/> while streaming frames from <paramref name="framesDir"/> as ASCII art in the console.
    /// </summary>
    /// <param name="wavPath">Path to mono/stereo 16-bit PCM WAV file.</param>
    /// <param name="framesDir">Directory containing sequential PNG frames named frame_XXXXXX.png.</param>
    /// <param name="targetFps">Target frames per second to attempt. If &lt;= 0 an even distribution across audio length is used.</param>
    public static void Play(string wavPath, string framesDir, int targetFps)
    {
        var wav = LoadPcmWav(wavPath);
        short channels = wav.channels;
        int sampleRate = wav.sampleRate;
        short bitsPerSample = wav.bitsPerSample;
        byte[] pcm = wav.pcmData;
        if (pcm.Length == 0) throw new InvalidOperationException("No PCM data found in WAV.");

        var frames = Directory.GetFiles(framesDir, "frame_*.png").OrderBy(f => f).ToArray();
        if (frames.Length == 0) return;

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
                totalSeconds = frames.Length / 24.0; // fallback
            }

            double frameDuration = targetFps > 0 ? 1.0 / targetFps : totalSeconds / frames.Length;
            SourceState state;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                al.SourceStop(source);
            };

            int lastRendered = -1;
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
                    try
                    {
                        string ascii = FrameToAscii(frames[frameIndex], Console.WindowWidth, Console.WindowHeight);
                        Console.SetCursorPosition(0, 0);
                        Console.Write(ascii);
                    }
                    catch { }
                    lastRendered = frameIndex;
                }

                if (state != SourceState.Playing && frameIndex >= frames.Length - 1) break;
                int sleep = (int)(frameDuration * 1000 / 3);
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

    /// <summary>
    /// Converts a single PNG frame to an ASCII art string sized for the current console window.
    /// </summary>
    private static string FrameToAscii(string path, int consoleWidth, int consoleHeight)
    {
        if (consoleWidth < 10 || consoleHeight < 5) return "[Console too small]";
        double charAspect = 0.5; // approximate width/height ratio of a character cell
        int targetWidth = Math.Max(8, consoleWidth);
        int targetHeight = Math.Max(4, (int)(consoleHeight / 1.0));

        using Image<Rgba32> img = Image.Load<Rgba32>(path);
        double imgAspect = img.Width / (double)img.Height;
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
                double lum = (0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B) / 255.0;
                int idx = (int)((ASCIICHARS.Length - 1) * lum);
                if (idx < 0) idx = 0; else if (idx >= ASCIICHARS.Length) idx = ASCIICHARS.Length - 1;
                sb.Append(ASCIICHARS[idx]);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses a PCM WAV file locating the 'fmt ' and 'data' chunks; supports mono/stereo 16-bit PCM.
    /// </summary>
    private static (short channels, int sampleRate, short bitsPerSample, byte[] pcmData) LoadPcmWav(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (br.BaseStream.Length < 12) throw new InvalidOperationException("File too small for RIFF header");
        string riff = new string(br.ReadChars(4));
        br.ReadInt32();
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
                break;

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16) throw new InvalidOperationException("fmt chunk too small");
                short audioFormat = br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                _ = br.ReadInt32(); // byteRate unused
                _ = br.ReadInt16(); // blockAlign unused
                bitsPerSample = br.ReadInt16();
                int remaining = chunkSize - 16;
                if (remaining > 0) br.BaseStream.Seek(remaining, SeekOrigin.Current);
                if (audioFormat != 1) throw new NotSupportedException($"Unsupported WAV format code {audioFormat}, only PCM (1) supported.");
                fmtSeen = true;
            }
            else if (chunkId == "data")
            {
                if (!fmtSeen) { }
                pcm = br.ReadBytes(chunkSize);
            }
            else
            {
                br.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }

            if ((chunkSize & 1) == 1 && br.BaseStream.Position < br.BaseStream.Length)
                br.BaseStream.Seek(1, SeekOrigin.Current);

            if (pcm != null && fmtSeen) break;
        }

        if (!fmtSeen) throw new InvalidOperationException("Missing fmt chunk");
        if (pcm == null) throw new InvalidOperationException("Missing data chunk");
        if (bitsPerSample != 16) throw new NotSupportedException("Only 16-bit PCM supported");
        if (channels != 1 && channels != 2) throw new NotSupportedException("Only mono or stereo supported");

        return (channels, sampleRate, bitsPerSample, pcm);
    }
}
