using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundboard.Audio;

internal static class SampleFormat
{
    /// <summary>
    /// Converts a source to the bus format (float, target sample rate, mono or stereo)
    /// so it can be added to that bus's mixer.
    /// </summary>
    public static ISampleProvider ConvertTo(this ISampleProvider source, WaveFormat target)
    {
        var channels = source.WaveFormat.Channels;
        if (channels > 2)
        {
            source = new DownmixToStereoSampleProvider(source);
            channels = 2;
        }

        if (channels == 1 && target.Channels == 2) source = new MonoToStereoSampleProvider(source);
        else if (channels == 2 && target.Channels == 1) source = new StereoToMonoSampleProvider(source);

        if (source.WaveFormat.SampleRate != target.SampleRate)
            source = new WdlResamplingSampleProvider(source, target.SampleRate);

        return source;
    }
}

/// <summary>Folds N interleaved channels into stereo: even channels to the left, odd channels to the right.</summary>
internal sealed class DownmixToStereoSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly int _inChannels = source.WaveFormat.Channels;
    private float[] _buffer = [];

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / 2;
        var needed = frames * _inChannels;
        if (_buffer.Length < needed) _buffer = new float[needed];

        var read = source.Read(_buffer, 0, needed);
        var framesRead = read / _inChannels;
        var leftCount = (_inChannels + 1) / 2;
        var rightCount = _inChannels / 2;

        for (var f = 0; f < framesRead; f++)
        {
            float l = 0, r = 0;
            var baseIndex = f * _inChannels;
            for (var c = 0; c < _inChannels; c++)
            {
                if (c % 2 == 0) l += _buffer[baseIndex + c];
                else r += _buffer[baseIndex + c];
            }
            buffer[offset + f * 2] = l / leftCount;
            buffer[offset + f * 2 + 1] = r / rightCount;
        }
        return framesRead * 2;
    }
}
