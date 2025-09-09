using Xabe.FFmpeg;

namespace TerminalVideoPlayer;

/// <summary>
/// Provides audio extraction and lightweight analysis helpers (PCM WAV mono conversion and simple zero-cross based metrics).
/// </summary>
public static class AudioExtractor
{
    /// <summary>
    /// Extracts the first audio stream from a video as a mono 44.1 kHz 16-bit PCM WAV file.
    /// </summary>
    /// <param name="inputFile">Input video path.</param>
    /// <param name="audioDir">Directory to output the WAV into.</param>
    /// <returns>Full path to the extracted WAV, or <c>null</c> if no audio stream was found.</returns>
    public static async Task<string?> ExtractAudioAsync(string inputFile, string audioDir)
    {
        Directory.CreateDirectory(audioDir);
        var mediaInfo = await FFmpeg.GetMediaInfo(inputFile);
        var audioStream = mediaInfo.AudioStreams?.FirstOrDefault();
        if (audioStream == null) return null;

        var wavPath = Path.Combine(audioDir, "extracted.wav");
        var audioConversion = FFmpeg.Conversions.New()
            .AddStream(audioStream)
            .AddParameter("-ac 1 -ar 44100 -sample_fmt s16", ParameterPosition.PostInput) // mono 44.1k 16-bit
            .AddParameter("-hide_banner -loglevel quiet", ParameterPosition.PreInput)
            .SetOverwriteOutput(true)
            .SetOutput(wavPath);

        await audioConversion.Start();
        return File.Exists(wavPath) ? wavPath : null;
    }

    /// <summary>
    /// Performs a simple approximate frequency and RMS amplitude analysis of a mono 16-bit WAV file
    /// by counting zero crossings in ~100ms windows. Results are not returned; the method is silent
    /// (can be extended later to collect data if desired).
    /// </summary>
    /// <param name="wavPath">Path to the PCM mono WAV file.</param>
    /// <param name="maxSeconds">Maximum number of seconds to scan from the beginning.</param>
    public static void AnalyzeWavApprox(string wavPath, int maxSeconds)
    {
        using var fs = File.OpenRead(wavPath);
        if (fs.Length < 44) return;

        byte[] header = new byte[44];
        fs.Read(header, 0, 44);

        int sampleRate = BitConverter.ToInt32(header, 24);
        short channels = BitConverter.ToInt16(header, 22);
        short bitsPerSample = BitConverter.ToInt16(header, 34);
        int dataSize = BitConverter.ToInt32(header, 40);

        if (channels != 1 || bitsPerSample != 16) return;

        int bytesPerSample = bitsPerSample / 8;
        int totalSamples = dataSize / bytesPerSample;
        int maxSamples = Math.Min(totalSamples, sampleRate * maxSeconds);

        byte[] sampleBytes = new byte[maxSamples * bytesPerSample];
        int actuallyRead = fs.Read(sampleBytes, 0, sampleBytes.Length);
        int samplesRead = actuallyRead / bytesPerSample;
        if (samplesRead == 0) return;

        int window = sampleRate / 10; // ~100ms
        if (window < 100) window = 100;

        for (int offset = 0; offset + window < samplesRead; offset += window)
        {
            int zeroCross = 0;
            double rmsAccum = 0;
            short prev = BitConverter.ToInt16(sampleBytes, offset * bytesPerSample);
            for (int i = offset + 1; i < offset + window; i++)
            {
                short cur = BitConverter.ToInt16(sampleBytes, i * bytesPerSample);
                if ((prev >= 0 && cur < 0) || (prev < 0 && cur >= 0)) zeroCross++;
                double norm = cur / 32768.0;
                rmsAccum += norm * norm;
                prev = cur;
            }
            double rms = Math.Sqrt(rmsAccum / window);
            _ = (zeroCross, rms); // intentionally unused (placeholder for future use)
        }
    }
}
