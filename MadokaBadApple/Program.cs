#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Spectre.Console;

// Audio backends
using NAudio.Wave;     // Windows

// FFmpeg
using FFmpeg.AutoGen;

// ---------------- CLI options ----------------
sealed class Options
{
    public string Video { get; init; } = "";
    public string? Audio { get; init; }
    public int Width { get; init; } = 80;
    public bool Loop { get; init; } = false;

    public static Options Parse(string[] args)
    {
        string? video = null, audio = null;
        int width = 80;
        bool loop = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--video" && i + 1 < args.Length) video = args[++i];
            else if (a == "--audio" && i + 1 < args.Length) audio = args[++i];
            else if (a == "--width" && i + 1 < args.Length) width = int.Parse(args[++i]);
            else if (a == "--loop") loop = true;
        }

        if (string.IsNullOrWhiteSpace(video))
            throw new ArgumentException("Usage: --video <path> [--audio <path>] [--width N] [--loop]");

        return new Options { Video = video!, Audio = audio, Width = width, Loop = loop };
    }
}

// ---------------- Main program ----------------
static class Program
{
    // Tweak this ramp to change visual contrast
    const string Ramp = "$@B%8&WM#*oahkbdpqwmZO0QLCJUYXzcvunxrjft/\\|()1{}[]?-_+~<>i!lI;:,\"^`'. ";

    static int Main(string[] args)
    {
        try
        {
            var opt = Options.Parse(args);

            // Load vendored FFmpeg native libs from ./ffmpeg-libs/<rid>/
            FFmpegBinaries.LoadFromRepo();

            // Start audio (optional)
            using var audio = StartAudio(opt.Audio, opt.Loop);

            // Show ASCII video
            RunAsciiVideo(opt.Video, opt.Width, opt.Loop);

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
        finally
        {
            AnsiConsole.Cursor.Show();
        }
    }

    static IAudioPlayer? StartAudio(string? path, bool loop)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var p = new NAudioPlayer();
            p.Start(path, loop);
            return p;
        }
        else
        {
            // No native deps; just skip audio on non-Windows
            var p = new NoOpAudioPlayer();
            p.Start(path, loop);
            return p;
        }
    }

    static void RunAsciiVideo(string videoPath, int outWidth, bool loop)
    {
        using var reader = new FFmpegVideoReader(videoPath); // GRAY8 frames
        double fps = reader.Fps > 0 ? reader.Fps : 30.0;
        var frameDuration = TimeSpan.FromSeconds(1.0 / fps);
        var sw = new Stopwatch();

        AnsiConsole.Cursor.Hide();
        var panel = new Panel("loading…") { Border = BoxBorder.None };

        AnsiConsole.Live(panel).AutoClear(false).Start(ctx =>
        {
            do
            {
                reader.SeekStart();

                while (reader.TryReadFrame(out var frame))
                {
                    sw.Restart();
                    string ascii = ToAscii(frame, outWidth, Ramp, aspect: 0.55);
                    ctx.UpdateTarget(new Panel(ascii) { Border = BoxBorder.None });

                    var remaining = frameDuration - sw.Elapsed;
                    if (remaining > TimeSpan.Zero)
                        Thread.Sleep((int)Math.Min(remaining.TotalMilliseconds, 15));
                }

                if (!loop) break;
            }
            while (true);
        });
    }

    // Scale GRAY8 frame with nearest neighbor and map to ASCII chars
    static string ToAscii(GrayFrame f, int outWidth, string ramp, double aspect)
    {
        int w = Math.Max(1, outWidth);
        int h = Math.Max(1, (int)(f.Height * (outWidth / (double)f.Width) * aspect));

        var sb = new StringBuilder(h * (w + 1));
        int shades = ramp.Length;

        for (int y = 0; y < h; y++)
        {
            int srcY = y * f.Height / h;
            int rowOff = srcY * f.Stride;

            for (int x = 0; x < w; x++)
            {
                int srcX = x * f.Width / w;
                byte g = f.Data[rowOff + srcX];
                int idx = (int)(g * (shades - 1) / 255.0);
                sb.Append(ramp[idx]);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}


// ---------------- Audio backends ----------------
interface IAudioPlayer : IDisposable
{
    void Start(string path, bool loop);
    void Stop();
}

sealed class NAudioPlayer : IAudioPlayer
{
    private NAudio.Wave.IWavePlayer? _out;
    private NAudio.Wave.WaveStream? _reader;
    private bool _loop;

    public void Start(string path, bool loop)
    {
        _loop = loop;
        _reader = new NAudio.Wave.AudioFileReader(path);
        _out = new NAudio.Wave.WaveOutEvent();
        _out.Init(_reader);
        _out.PlaybackStopped += (_, __) =>
        {
            if (_loop && _reader != null && _out != null)
            {
                _reader.Position = 0;
                _out.Init(_reader);
                _out.Play();
            }
        };
        _out.Play();
    }

    public void Stop() => _out?.Stop();

    public void Dispose()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _reader?.Dispose();
    }
}

sealed class NoOpAudioPlayer : IAudioPlayer
{
    public void Start(string path, bool loop)
        => Spectre.Console.AnsiConsole.MarkupLine("[yellow]Audio disabled on this platform (no extra natives).[/]");
    public void Stop() { }
    public void Dispose() { }
}


// ---------------- FFmpeg loader ----------------
static class FFmpegBinaries
{
    public static void LoadFromRepo()
    {
        var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx-x64"
                : "linux-x64";

        string baseDir = AppContext.BaseDirectory;
        string ffDir = Path.Combine(baseDir, "ffmpeg-libs", rid);

        if (!Directory.Exists(ffDir))
            throw new DirectoryNotFoundException($"Missing FFmpeg libs at {ffDir}");

        // Tell FFmpeg.AutoGen to look here for native libs (helps P/Invoke resolution)
        try { FFmpeg.AutoGen.ffmpeg.RootPath = ffDir; } catch { /* older/newer versions may not expose RootPath; safe to ignore */ }

        // Load in dependency order so sonames are already present when others dlopen()
        string[] bases = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "avutil", "swresample", "swscale", "avcodec", "avformat", "avfilter", "avdevice" }
            : new[] { "libavutil", "libswresample", "libswscale", "libavcodec", "libavformat", "libavfilter", "libavdevice" };

        foreach (var b in bases)
        {
            var path = FindBestMatch(ffDir, b);
            if (path == null) continue; // avfilter/avdevice are optional; skip if not present
            TryLoad(path);
        }
    }

    private static string? FindBestMatch(string dir, string baseName)
    {
        // Try exact base, then versioned variants, then any matching file
        // Linux/mac: libname.so / .so.* / .dylib; Windows: name-*.dll
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // e.g. avcodec-62.dll
            var dlls = Directory.GetFiles(dir, $"{baseName}*.dll");
            return dlls.Length > 0 ? dlls[0] : null;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var dylibs = Directory.GetFiles(dir, $"{baseName}*.dylib");
            if (dylibs.Length > 0) return dylibs[0];
            var exact = Path.Combine(dir, baseName + ".dylib");
            return File.Exists(exact) ? exact : null;
        }
        else // Linux
        {
            var exact = Path.Combine(dir, baseName + ".so");
            if (File.Exists(exact)) return exact;
            var soN = Directory.GetFiles(dir, baseName + ".so.*");
            return soN.Length > 0 ? soN[0] : null;
        }
    }

    private static void TryLoad(string fullPath)
    {
        try { NativeLibrary.Load(fullPath); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]warn:[/] failed to load {Path.GetFileName(fullPath)}: {ex.Message}");
        }
    }
}

// ---------------- FFmpeg reader (GRAY8 frames) ----------------
readonly record struct GrayFrame(byte[] Data, int Width, int Height, int Stride);

unsafe sealed class FFmpegVideoReader : IDisposable
{
    private AVFormatContext* _fmt = null;
    private AVCodecContext* _codec = null;
    private SwsContext* _sws = null;
    private AVFrame* _src = null;
    private AVFrame* _dst = null;
    private AVPacket* _pkt = null;
    private int _videoStreamIndex = -1;

    private int _w, _h;
    public int Width => _w;
    public int Height => _h;
    public double Fps { get; private set; }

    static unsafe sbyte* Utf8Alloc(string text)
    {
        // allocate with av_malloc so FFmpeg frees are compatible (we'll free it ourselves)
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        sbyte* p = (sbyte*)ffmpeg.av_malloc((ulong)(bytes.Length + 1));
        if (p == null) throw new OutOfMemoryException();
        for (int i = 0; i < bytes.Length; i++) p[i] = (sbyte)bytes[i];
        p[bytes.Length] = 0; // NUL
        return p;
    }

    public FFmpegVideoReader(string path)
    {
        // OPEN INPUT (string url)
        AVFormatContext* fmt = null;
        Throw(ffmpeg.avformat_open_input(&fmt, path, null, null));
        _fmt = fmt;


        Throw(ffmpeg.avformat_find_stream_info(_fmt, null));

        _videoStreamIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (_videoStreamIndex < 0) throw new InvalidOperationException("No video stream found.");

        var stream = _fmt->streams[_videoStreamIndex];
        Fps = ffmpeg.av_q2d(stream->avg_frame_rate);

        var codecpar = stream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException("Decoder not found.");

        _codec = ffmpeg.avcodec_alloc_context3(codec);
        Throw(ffmpeg.avcodec_parameters_to_context(_codec, codecpar));
        Throw(ffmpeg.avcodec_open2(_codec, codec, null));

        _w = _codec->width;
        _h = _codec->height;

        _src = ffmpeg.av_frame_alloc();
        _dst = ffmpeg.av_frame_alloc();
        _pkt = ffmpeg.av_packet_alloc();

        _sws = ffmpeg.sws_getContext(_w, _h, _codec->pix_fmt, _w, _h,
                                     AVPixelFormat.AV_PIX_FMT_GRAY8,
                                     ffmpeg.SWS_BILINEAR, null, null, null);

        int bufSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_GRAY8, _w, _h, 1);
        byte* buffer = (byte*)ffmpeg.av_malloc((ulong)bufSize);
        ffmpeg.av_image_fill_arrays(ref *(byte_ptrArray4*)&_dst->data, ref *(int_array4*)&_dst->linesize, buffer,
                                    AVPixelFormat.AV_PIX_FMT_GRAY8, _w, _h, 1);
    }

    public void SeekStart()
    {
        ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_codec);
    }

    public bool TryReadFrame(out GrayFrame frame)
    {
        frame = default;

        while (ffmpeg.av_read_frame(_fmt, _pkt) >= 0)
        {
            if (_pkt->stream_index != _videoStreamIndex)
            {
                ffmpeg.av_packet_unref(_pkt);
                continue;
            }

            int send = ffmpeg.avcodec_send_packet(_codec, _pkt);
            ffmpeg.av_packet_unref(_pkt);
            if (send == ffmpeg.AVERROR(ffmpeg.EAGAIN)) continue;
            Throw(send);

            while (true)
            {
                int recv = ffmpeg.avcodec_receive_frame(_codec, _src);
                if (recv == ffmpeg.AVERROR(ffmpeg.EAGAIN)) break;
                if (recv == ffmpeg.AVERROR_EOF) return false;
                Throw(recv);

                // Convert to GRAY8
                ffmpeg.sws_scale(_sws, _src->data, _src->linesize, 0, _h, _dst->data, _dst->linesize);

                // Copy to managed buffer
                int stride = _dst->linesize[0];
                int size = stride * _h;
                var managed = new byte[size];
                Marshal.Copy((nint)_dst->data[0], managed, 0, size);

                frame = new GrayFrame(managed, _w, _h, stride);

                ffmpeg.av_frame_unref(_src);
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_pkt != null)
        {
            AVPacket* pkt = _pkt;
            ffmpeg.av_packet_free(&pkt);
            _pkt = null;
        }
        if (_src != null)
        {
            AVFrame* f = _src;
            ffmpeg.av_frame_free(&f);
            _src = null;
        }
        if (_dst != null)
        {
            if (_dst->data[0] != null) ffmpeg.av_free(_dst->data[0]);
            AVFrame* f = _dst;
            ffmpeg.av_frame_free(&f);
            _dst = null;
        }
        if (_sws != null)
        {
            SwsContext* s = _sws;
            ffmpeg.sws_freeContext(s);
            _sws = null;
        }
        if (_codec != null)
        {
            AVCodecContext* c = _codec;
            ffmpeg.avcodec_free_context(&c);
            _codec = null;
        }
        if (_fmt != null)
        {
            AVFormatContext* f = _fmt;
            ffmpeg.avformat_close_input(&f);
            _fmt = null;
        }
    }

    private static void Throw(int err)
    {
        if (err < 0)
        {
            var buffer = stackalloc byte[1024];
            ffmpeg.av_strerror(err, buffer, 1024);
            throw new ApplicationException($"ffmpeg error: {Marshal.PtrToStringAnsi((nint)buffer)}");
        }
    }
}
