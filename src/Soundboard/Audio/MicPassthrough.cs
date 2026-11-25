using NAudio.Wave;

namespace Soundboard.Audio;

/// <summary>
/// Mixes a real microphone into the virtual-mic bus, so Discord hears your voice and the sounds
/// through one input. It keeps working through drop-outs:
/// <list type="bullet">
/// <item>A watchdog reopens the mic if capture stops (sleep, a USB power-saving disconnect, a
/// format change) or if no audio arrives for <see cref="StallTimeout"/>, and keeps retrying until it's back.</item>
/// <item><see cref="Output"/> always returns a full buffer, padded with silence, so the mixer can
/// never treat it as finished and drop it.</item>
/// <item>The delay is capped: if clock drift between the mic and the output builds up more than
/// 200 ms of buffered audio, the oldest audio is dropped back to 60 ms.</item>
/// </list>
/// </summary>
public sealed class MicPassthrough : IDisposable
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(3);
    private const double MaxLatencySeconds = 0.20;
    private const double TargetLatencySeconds = 0.06;

    private readonly Func<IWaveIn> _openCapture;
    private readonly WaveFormat _outputFormat;
    private readonly object _sessionLock = new();
    private readonly Timer _watchdog;
    private Session? _session;
    private int _checking;
    private volatile bool _disposed;
    private bool _healthy;
    private bool _hadOutage;
    private float _peak;

    /// <param name="openCapture">Opens a fresh capture for the mic. It's called again on every reconnect, so it should look the device up again.</param>
    /// <param name="outputFormat">The format of the bus the mic is mixed into.</param>
    public MicPassthrough(Func<IWaveIn> openCapture, WaveFormat outputFormat, TimeSpan? watchdogInterval = null)
    {
        _openCapture = openCapture;
        _outputFormat = outputFormat;
        Output = new AlwaysFullOutput(this);

        Reconnect(initial: true);
        var interval = watchdogInterval ?? TimeSpan.FromSeconds(1);
        _watchdog = new Timer(_ => Check(), null, interval, interval);
    }

    /// <summary>Raised on a background thread when the mic drops out or comes back.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Never ends. It returns silence while the mic is reconnecting.</summary>
    public ISampleProvider Output { get; }

    public bool IsLive
    {
        get { lock (_sessionLock) return _session is { Stopped: false }; }
    }

    /// <summary>The loudest sample sent since the last call (0 to 1), for a level meter.</summary>
    public float TakeLevel() => Interlocked.Exchange(ref _peak, 0f);

    /// <summary>Runs one watchdog check now. The timer calls this. Tests call it directly.</summary>
    public void Check()
    {
        if (_disposed || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            Session? session;
            lock (_sessionLock) session = _session;

            string? problem = session switch
            {
                null => "isn't connected",
                { Stopped: true } => $"stopped ({session.StopError?.Message ?? "Windows gave no reason"})",
                _ when session.MillisecondsSinceData > StallTimeout.TotalMilliseconds => "stopped sending audio",
                _ => null,
            };

            if (problem is null)
            {
                if (!_healthy && _hadOutage) Report("Your mic reconnected.");
                _healthy = true;
                return;
            }

            if (_healthy)
            {
                _healthy = false;
                _hadOutage = true;
                Report($"Your mic {problem}. Reconnecting…");
            }
            Reconnect(initial: false);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private void Reconnect(bool initial)
    {
        Session? fresh = null;
        try
        {
            fresh = Session.Open(_openCapture, _outputFormat);
        }
        catch (Exception ex)
        {
            // Report the first failure only, rather than once a second while the mic is gone.
            if (initial || _healthy || !_hadOutage) Log.Write($"Mic open failed: {ex.GetType().Name}: {ex.Message}");
            if (initial)
            {
                _hadOutage = true;
                Report($"Couldn't open your mic ({ex.Message}). Retrying…");
            }
        }

        Session? old;
        lock (_sessionLock)
        {
            if (_disposed)
            {
                // Dispose ran while this reconnect was opening the mic. Don't leave the capture running.
                old = fresh;
                fresh = null;
            }
            else
            {
                old = _session;
                _session = fresh;
            }
        }
        old?.Dispose();
        if (initial) _healthy = fresh is not null;
    }

    private void Report(string message)
    {
        Log.Write(message);
        StatusChanged?.Invoke(message);
    }

    private int ReadInto(float[] buffer, int offset, int count)
    {
        var read = 0;
        lock (_sessionLock)
        {
            if (_session is { Stopped: false } session)
            {
                session.CapLatency();
                read = session.Reader.Read(buffer, offset, count);
            }
        }

        float peak = 0;
        for (var i = offset; i < offset + read; i++)
        {
            var v = Math.Abs(buffer[i]);
            if (v > peak) peak = v;
        }
        if (peak > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, peak);

        Array.Clear(buffer, offset + read, count - read);
        return count;
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        Session? old;
        lock (_sessionLock)
        {
            _disposed = true;
            old = _session;
            _session = null;
        }
        old?.Dispose();
    }

    private sealed class AlwaysFullOutput(MicPassthrough owner) : ISampleProvider
    {
        public WaveFormat WaveFormat => owner._outputFormat;
        public int Read(float[] buffer, int offset, int count) => owner.ReadInto(buffer, offset, count);
    }

    /// <summary>One open capture: the device, its buffer and the conversion to the bus format.</summary>
    private sealed class Session : IDisposable
    {
        private readonly IWaveIn _capture;
        private readonly BufferedWaveProvider _buffer;
        private byte[] _discard = [];
        private long _lastDataTicks = Environment.TickCount64;

        private Session(IWaveIn capture, BufferedWaveProvider buffer, ISampleProvider reader)
        {
            _capture = capture;
            _buffer = buffer;
            Reader = reader;
        }

        public ISampleProvider Reader { get; }
        public volatile bool Stopped;
        public Exception? StopError { get; private set; }
        public long MillisecondsSinceData => Environment.TickCount64 - Interlocked.Read(ref _lastDataTicks);

        public static Session Open(Func<IWaveIn> openCapture, WaveFormat outputFormat)
        {
            var capture = openCapture();
            try
            {
                var buffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(1),
                    DiscardOnBufferOverflow = true,
                    ReadFully = false, // Report what's actually there; the output pads with silence.
                };
                var session = new Session(capture, buffer, buffer.ToSampleProvider().ConvertTo(outputFormat));
                capture.DataAvailable += (_, e) =>
                {
                    buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    Interlocked.Exchange(ref session._lastDataTicks, Environment.TickCount64);
                };
                capture.RecordingStopped += (_, e) =>
                {
                    session.StopError = e.Exception;
                    session.Stopped = true;
                };
                capture.StartRecording();
                return session;
            }
            catch
            {
                capture.Dispose();
                throw;
            }
        }

        /// <summary>Drops the oldest buffered audio if drift has built up too much delay.</summary>
        public void CapLatency()
        {
            if (_buffer.BufferedDuration.TotalSeconds <= MaxLatencySeconds) return;
            var format = _buffer.WaveFormat;
            var keep = (int)(TargetLatencySeconds * format.AverageBytesPerSecond);
            var drop = _buffer.BufferedBytes - keep;
            drop -= drop % format.BlockAlign;
            if (drop <= 0) return;
            if (_discard.Length < drop) _discard = new byte[drop];
            _buffer.Read(_discard, 0, drop);
        }

        public void Dispose()
        {
            try { _capture.StopRecording(); } catch (Exception) { }
            try { _capture.Dispose(); } catch (Exception) { }
        }
    }
}
