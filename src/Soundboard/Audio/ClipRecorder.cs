using System.Diagnostics;

namespace Soundboard.Audio;

/// <summary>
/// Keeps the last few seconds or minutes of one app's audio in memory, and nothing from any other app.
/// Nothing is written to disk until the user saves a clip.
/// </summary>
public sealed class ClipRecorder : IDisposable
{
    /// <summary>The lengths the user can choose. 48 kHz stereo float audio takes about 23 MB a minute.</summary>
    public static readonly TimeSpan[] BufferLengths =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];

    public static readonly TimeSpan DefaultBufferLength = BufferLengths[0];

    /// <summary>"30 s", "1 min", "10 min".</summary>
    public static string Format(TimeSpan length) =>
        length.TotalSeconds < 60 ? $"{length.TotalSeconds:0} s" : $"{length.TotalMinutes:0.#} min";

    /// <summary>Memory the buffer needs, in MB.</summary>
    public static double MemoryMegabytes(TimeSpan length) =>
        length.TotalSeconds * ProcessLoopbackCapture.SampleRate * ProcessLoopbackCapture.Channels * sizeof(float) / 1_000_000;

    private ProcessLoopbackCapture? _capture;
    private ReplayBuffer? _buffer;
    private Process? _process;

    /// <summary>Raised on a background thread when recording stops by itself. The message says why.</summary>
    public event EventHandler<string>? StoppedUnexpectedly;

    public bool IsRecording => _capture is not null;
    public int SampleRate => ProcessLoopbackCapture.SampleRate;
    public int Channels => ProcessLoopbackCapture.Channels;

    /// <summary>How much audio is kept while recording.</summary>
    public TimeSpan BufferLength { get; private set; } = DefaultBufferLength;

    /// <summary>The app being recorded, or null when off.</summary>
    public AudioSource? Source { get; private set; }

    public async Task StartAsync(AudioSource source, TimeSpan bufferLength)
    {
        if (IsRecording) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Recording one app's audio needs Windows 10 version 2004 or later.");

        Process process;
        try { process = Process.GetProcessById(source.ProcessId); }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{source.AppName} isn't running any more. Pick another window and try again.");
        }

        try
        {
            var buffer = new ReplayBuffer(SampleRate, Channels, bufferLength);
            var capture = await ProcessLoopbackCapture.CreateAsync(source.ProcessId);
            capture.DataAvailable += buffer.Write;
            capture.Failed += ex => StoppedUnexpectedly?.Invoke(this, $"Clip recorder stopped: {ex.Message}");

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => StoppedUnexpectedly?.Invoke(this, $"{source.AppName} closed, so the clip recorder stopped.");

            _buffer = buffer;
            _capture = capture;
            _process = process;
            Source = source;
            BufferLength = bufferLength;
            capture.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        _capture?.Dispose();
        _capture = null;
        _buffer = null; // Drop the buffered audio when recording is turned off.
        _process?.Dispose();
        _process = null;
        Source = null;
    }

    /// <summary>The buffered audio, oldest first, as interleaved stereo samples at 48 kHz.</summary>
    public float[] Snapshot() => _buffer?.Snapshot() ?? [];

    public void Dispose() => Stop();
}
