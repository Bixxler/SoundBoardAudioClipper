using System.Diagnostics;

namespace Soundboard.Audio;

/// <summary>
/// Keeps the last few seconds or minutes of audio from every app with an audio session, except the
/// ones the user excluded, mixed into one track in memory. Apps that start playing while it's on are
/// picked up automatically. Nothing is written to disk until the user saves a clip.
///
/// Windows can capture one process tree per stream, so each app gets its own capture and they're
/// mixed on a shared timeline.
/// </summary>
public sealed class ClipRecorder : IDisposable
{
    /// <summary>The lengths the user can choose. 48 kHz stereo float audio takes about 23 MB a minute.</summary>
    public static readonly TimeSpan[] BufferLengths =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];

    public static readonly TimeSpan DefaultBufferLength = BufferLengths[0];

    /// <summary>"30 s", "1 min", "2 min".</summary>
    public static string Format(TimeSpan length) =>
        length.TotalSeconds < 60 ? $"{length.TotalSeconds:0} s" : $"{length.TotalMinutes:0.#} min";

    /// <summary>Memory the buffer needs, in MB.</summary>
    public static double MemoryMegabytes(TimeSpan length) =>
        length.TotalSeconds * ProcessLoopbackCapture.SampleRate * ProcessLoopbackCapture.Channels * sizeof(float) / 1_000_000;

    /// <summary>One app process tree being captured, and where its audio goes on the shared timeline.</summary>
    private sealed class Source(AudioApp app, ProcessLoopbackCapture capture, long startFrame)
    {
        public AudioApp App { get; } = app;
        public ProcessLoopbackCapture Capture { get; } = capture;
        public long Cursor = startFrame; // Only touched on this source's capture thread.
    }

    private readonly AudioAppMonitor _monitor;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly Dictionary<int, Source> _sources = new();
    private readonly HashSet<int> _failed = new(); // Roots that couldn't be captured; not retried every poll.
    private readonly Dictionary<string, AudioApp> _recorded = new(StringComparer.OrdinalIgnoreCase); // Every app captured since start.
    private HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private MixBuffer? _buffer;
    private Stopwatch _clock = new();
    private int _session; // Bumped on every start and stop, so a capture that finishes opening late is dropped.

    /// <summary>Raised (on any thread) when the set of apps being captured changes.</summary>
    public event EventHandler? SourcesChanged;

    public ClipRecorder(AudioAppMonitor monitor)
    {
        _monitor = monitor;
        _monitor.Changed += (_, _) => _ = ReconcileAsync();
    }

    public bool IsRecording { get { lock (_gate) return _buffer is not null; } }
    public int SampleRate => ProcessLoopbackCapture.SampleRate;
    public int Channels => ProcessLoopbackCapture.Channels;

    /// <summary>How much audio is kept while recording.</summary>
    public TimeSpan BufferLength { get; private set; } = DefaultBufferLength;

    /// <summary>
    /// Every app captured since recording started, including ones that have since closed. Used to
    /// describe what's in a clip.
    /// </summary>
    public IReadOnlyList<AudioApp> RecordedApps
    {
        get { lock (_gate) return [.. _recorded.Values]; }
    }

    /// <summary>The apps being captured right now, by display name.</summary>
    public IReadOnlyList<AudioApp> CapturedApps
    {
        get { lock (_gate) return _sources.Values.Select(s => s.App).DistinctBy(a => a.Name).ToList(); }
    }

    public async Task StartAsync(TimeSpan bufferLength, IEnumerable<string> excludedApps)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Recording app audio needs Windows 10 version 2004 or later.");

        lock (_gate)
        {
            if (_buffer is not null) return;
            _excluded = new HashSet<string>(excludedApps, StringComparer.OrdinalIgnoreCase);
            _buffer = new MixBuffer(SampleRate, Channels, bufferLength);
            _clock = Stopwatch.StartNew();
            _failed.Clear();
            _recorded.Clear();
            _session++;
            BufferLength = bufferLength;
        }
        await ReconcileAsync();
    }

    /// <summary>Changes which apps are left out. Takes effect straight away if recording.</summary>
    public void SetExcluded(IEnumerable<string> excludedApps)
    {
        lock (_gate) _excluded = new HashSet<string>(excludedApps, StringComparer.OrdinalIgnoreCase);
        _ = ReconcileAsync();
    }

    public void Stop()
    {
        List<Source> stopping;
        lock (_gate)
        {
            _buffer = null; // Drop the buffered audio when recording is turned off.
            _session++;
            stopping = [.. _sources.Values];
            _sources.Clear();
        }
        foreach (var source in stopping) source.Capture.Dispose();
        if (stopping.Count > 0) SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The buffered audio, oldest first, as interleaved stereo samples at 48 kHz.</summary>
    public float[] Snapshot()
    {
        MixBuffer? buffer;
        lock (_gate) buffer = _buffer;
        return buffer?.Snapshot() ?? [];
    }

    /// <summary>
    /// Brings the running captures in line with the apps that have audio sessions and aren't excluded.
    /// Runs one at a time; called on start, when the app list changes and when exclusions change.
    /// </summary>
    private async Task ReconcileAsync()
    {
        await _reconcile.WaitAsync();
        try
        {
            MixBuffer buffer;
            HashSet<string> excluded;
            int session;
            lock (_gate)
            {
                if (_buffer is null) return;
                buffer = _buffer;
                excluded = _excluded;
                session = _session;
            }

            var wanted = WantedRoots(_monitor.Current, excluded);
            var changed = false;

            // Stop captures for apps that went away or were excluded.
            List<Source> stopping;
            lock (_gate)
            {
                stopping = _sources.Where(kv => !wanted.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
                foreach (var source in stopping) _sources.Remove(source.Capture.ProcessId);
                _failed.IntersectWith(wanted.Keys); // A root that's gone and comes back gets another try.
            }
            foreach (var source in stopping) { source.Capture.Dispose(); changed = true; }

            // Start captures for new apps.
            foreach (var (pid, app) in wanted)
            {
                lock (_gate)
                {
                    if (_sources.ContainsKey(pid) || _failed.Contains(pid)) continue;
                }

                ProcessLoopbackCapture capture;
                try
                {
                    capture = await ProcessLoopbackCapture.CreateAsync(pid);
                }
                catch (Exception ex)
                {
                    Log.Write($"Clip recorder: couldn't capture {app.Name} ({pid}): {ex.Message}");
                    lock (_gate) _failed.Add(pid);
                    continue;
                }

                Source added;
                lock (_gate)
                {
                    if (_session != session)
                    {
                        capture.Dispose(); // Stopped (or restarted) while this capture was opening.
                        return;
                    }
                    added = new Source(app, capture, (long)(_clock.Elapsed.TotalSeconds * SampleRate));
                    _sources[pid] = added;
                    _recorded[app.Name] = app;
                }
                capture.DataAvailable += (samples, count) =>
                {
                    buffer.Add(added.Cursor, samples, count);
                    added.Cursor += count / Channels;
                };
                capture.Failed += ex => OnSourceFailed(added, session, ex);
                capture.Start();
                changed = true;
            }

            if (changed) SourcesChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _reconcile.Release();
        }
    }

    /// <summary>
    /// The root processes to capture, and their apps. A tree capture includes child processes, so an app
    /// that was started by another app being captured is already covered and isn't captured twice.
    /// </summary>
    private static Dictionary<int, AudioApp> WantedRoots(IReadOnlyList<AudioApp> apps, HashSet<string> excluded)
    {
        var wanted = new Dictionary<int, AudioApp>();
        foreach (var app in apps)
        {
            if (excluded.Contains(app.Name)) continue;
            foreach (var root in app.Roots)
            {
                if (root.InsideApp is { } outer && !excluded.Contains(outer)) continue;
                wanted[root.ProcessId] = app;
            }
        }
        return wanted;
    }

    /// <summary>One app's capture failed (e.g. it closed). Drop it and keep recording the others.</summary>
    private void OnSourceFailed(Source source, int session, Exception ex)
    {
        Log.Write($"Clip recorder: capture of {source.App.Name} stopped: {ex.Message}");
        lock (_gate)
        {
            if (_session != session || !_sources.Remove(source.Capture.ProcessId)) return;
            _failed.Add(source.Capture.ProcessId); // Retried only if the app goes away and comes back.
        }
        // This runs on the capture's own thread, which Dispose waits for, so dispose from another one.
        Task.Run(source.Capture.Dispose);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}
