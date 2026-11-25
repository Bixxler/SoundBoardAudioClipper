namespace Soundboard.Audio;

/// <summary>
/// A fixed-length ring buffer holding the most recent interleaved samples. Written by the
/// capture thread and read from the UI thread, so both sides take the same lock.
/// </summary>
public sealed class ReplayBuffer
{
    private readonly float[] _data;
    private readonly object _lock = new();
    private int _write;
    private bool _full;

    public ReplayBuffer(int sampleRate, int channels, TimeSpan length)
    {
        SampleRate = sampleRate;
        Channels = channels;
        var frames = (int)(sampleRate * length.TotalSeconds);
        _data = new float[frames * channels];
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int Capacity => _data.Length;

    public void Write(float[] source, int count)
    {
        lock (_lock)
        {
            var offset = 0;
            // Anything larger than the buffer would only overwrite itself, so keep the newest part.
            if (count > _data.Length)
            {
                offset = count - _data.Length;
                count = _data.Length;
            }

            while (count > 0)
            {
                var n = Math.Min(count, _data.Length - _write);
                Array.Copy(source, offset, _data, _write, n);
                _write += n;
                if (_write == _data.Length)
                {
                    _write = 0;
                    _full = true;
                }
                offset += n;
                count -= n;
            }
        }
    }

    /// <summary>A copy of everything buffered, oldest sample first.</summary>
    public float[] Snapshot()
    {
        lock (_lock)
        {
            if (!_full) return _data[.._write];

            var result = new float[_data.Length];
            var tail = _data.Length - _write;
            Array.Copy(_data, _write, result, 0, tail);
            Array.Copy(_data, 0, result, tail, _write);
            return result;
        }
    }
}
