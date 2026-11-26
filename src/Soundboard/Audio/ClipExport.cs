using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundboard.Audio;

public static class ClipExport
{
    private static readonly Lazy<bool> MediaFoundation = new(() => { MediaFoundationApi.Startup(); return true; });

    /// <summary>Decodes a whole audio file into memory so it can be trimmed. Rejects very long files.</summary>
    public static (float[] Samples, int SampleRate, int Channels) Load(string path, TimeSpan maxLength)
    {
        using var audio = AudioFiles.Open(path);
        if (audio.TotalTime > maxLength)
            throw new InvalidOperationException(
                $"It's {audio.TotalTime:m\\:ss} long. Editing works on files up to {maxLength.TotalMinutes:0} minutes.");

        var reader = audio.Samples;
        var format = reader.WaveFormat;
        var samples = new List<float>((int)Math.Min(audio.TotalTime.TotalSeconds * format.SampleRate * format.Channels + 1, int.MaxValue));
        var buffer = new float[format.SampleRate * format.Channels];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read));
        return (samples.ToArray(), format.SampleRate, format.Channels);
    }

    /// <summary>
    /// Reverses interleaved audio in place, frame by frame, so left and right stay on their own sides.
    /// Running it twice gives back the original.
    /// </summary>
    public static void Reverse(float[] samples, int channels)
    {
        var frames = samples.Length / channels;
        for (int a = 0, b = frames - 1; a < b; a++, b--)
        {
            for (var c = 0; c < channels; c++)
                (samples[a * channels + c], samples[b * channels + c]) = (samples[b * channels + c], samples[a * channels + c]);
        }
    }

    /// <summary>A copy of part of an interleaved buffer, from <paramref name="startSeconds"/> to <paramref name="endSeconds"/>.</summary>
    public static float[] Slice(float[] samples, int sampleRate, int channels, double startSeconds, double endSeconds)
    {
        var totalFrames = samples.Length / channels;
        var first = Math.Clamp((int)(startSeconds * sampleRate), 0, totalFrames);
        var last = Math.Clamp((int)(endSeconds * sampleRate), first, totalFrames);
        return samples[(first * channels)..(last * channels)];
    }

    /// <summary>A playable source for a whole interleaved buffer.</summary>
    public static ISampleProvider FromArray(float[] samples, int sampleRate, int channels) =>
        new ArraySampleProvider(samples, 0, samples.Length, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));

    /// <summary>A playable source for part of an interleaved buffer.</summary>
    public static ISampleProvider Segment(float[] samples, int sampleRate, int channels, double startSeconds, double endSeconds)
    {
        var totalFrames = samples.Length / channels;
        var first = Math.Clamp((int)(startSeconds * sampleRate), 0, totalFrames);
        var last = Math.Clamp((int)(endSeconds * sampleRate), first, totalFrames);
        return new ArraySampleProvider(samples, first * channels, (last - first) * channels,
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
    }

    /// <summary>
    /// Writes an MP3 file with Windows' built-in encoder. At 128 kbps, a 5-second clip is about
    /// 80 KB, well inside Discord's 512 KB soundboard limit.
    /// </summary>
    public static void SaveMp3(ISampleProvider clip, string path)
    {
        _ = MediaFoundation.Value;
        MediaFoundationEncoder.EncodeToMp3(new SampleToWaveProvider16(clip), path, 128_000);
    }

    private sealed class ArraySampleProvider(float[] data, int offset, int count, WaveFormat format) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = format;

        public int Read(float[] buffer, int bufferOffset, int wanted)
        {
            var n = Math.Min(wanted, count - _position);
            if (n <= 0) return 0;
            Array.Copy(data, offset + _position, buffer, bufferOffset, n);
            _position += n;
            return n;
        }
    }
}
