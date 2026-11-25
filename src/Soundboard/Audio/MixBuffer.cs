namespace Soundboard.Audio;

/// <summary>
/// A ring buffer of the most recent audio, which several sources add into on one shared timeline.
/// Each source writes at its own frame position, so a source that starts later lands at the right
/// time instead of overwriting the others. Written by capture threads and read from the UI thread,
/// so every access takes the same lock.
/// </summary>
public sealed class MixBuffer
{
    private readonly float[] _data;
    private readonly int _frames;
    private readonly int _channels;
    private readonly object _lock = new();

    // Frames before _clearedTo have been zeroed for their current trip round the ring. The array starts
    // out zeroed, so the whole first trip is clean.
    private long _clearedTo;
    private long _end;

    public MixBuffer(int sampleRate, int channels, TimeSpan length)
    {
        _channels = channels;
        _frames = (int)(sampleRate * length.TotalSeconds);
        _data = new float[_frames * channels];
        _clearedTo = _frames;
    }

    /// <summary>Mixes interleaved samples in, starting at <paramref name="startFrame"/> on the shared timeline.</summary>
    public void Add(long startFrame, float[] samples, int count)
    {
        var frames = count / _channels;
        if (frames == 0) return;
        var endFrame = startFrame + frames;

        lock (_lock)
        {
            if (endFrame > _clearedTo)
            {
                // Zero the cells this write moves into, since they still hold audio from one trip ago.
                for (var f = Math.Max(_clearedTo, endFrame - _frames); f < endFrame; f++)
                    Array.Clear(_data, (int)(f % _frames) * _channels, _channels);
                _clearedTo = endFrame;
            }

            // Anything older than the ring holds is already gone.
            var first = Math.Max(0, _clearedTo - _frames - startFrame);
            for (var f = first; f < frames; f++)
            {
                var dst = (int)((startFrame + f) % _frames) * _channels;
                var src = (int)f * _channels;
                for (var c = 0; c < _channels; c++) _data[dst + c] += samples[src + c];
            }
            if (endFrame > _end) _end = endFrame;
        }
    }

    /// <summary>A copy of everything buffered, oldest sample first, limited to -1..1 because mixed sources can add up past it.</summary>
    public float[] Snapshot()
    {
        lock (_lock)
        {
            var start = Math.Max(0, _end - _frames);
            var frames = (int)(_end - start);
            var result = new float[frames * _channels];
            for (var f = 0; f < frames; f++)
            {
                var src = (int)((start + f) % _frames) * _channels;
                for (var c = 0; c < _channels; c++)
                    result[f * _channels + c] = Math.Clamp(_data[src + c], -1f, 1f);
            }
            return result;
        }
    }
}
