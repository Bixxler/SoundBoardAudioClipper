using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Soundboard.Audio;

/// <summary>One process tree of an app, and the other listed app that started it, if any.</summary>
public sealed record AppRoot(int ProcessId, string? InsideApp);

/// <summary>
/// An app with an audio session, as in the Windows volume mixer. <see cref="Name"/> is the exe name
/// without ".exe", which is what exclusions are saved by. An app can have several roots, e.g. two
/// separate instances.
/// </summary>
public sealed record AudioApp(string Name, string DisplayName, IReadOnlyList<AppRoot> Roots, bool IsActive)
{
    private static readonly string[] DiscordNames = ["Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment"];

    public bool IsDiscord => DiscordNames.Contains(Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Polls the audio sessions on every playback device, like the volume mixer shows, and reports the
/// apps that own them. Sessions are grouped by app root process, because capture works per process tree.
/// </summary>
public sealed class AudioAppMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    public static readonly string OwnName = Process.GetCurrentProcess().ProcessName;

    private readonly Timer _timer;
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private MMDeviceEnumerator? _enumerator;
    private string _signature = "";
    private volatile bool _disposed;
    private int _polling;

    /// <summary>Raised on a background thread when the list of apps, or whether they're active, changes.</summary>
    public event EventHandler<IReadOnlyList<AudioApp>>? Changed;

    public IReadOnlyList<AudioApp> Current { get; private set; } = [];

    public AudioAppMonitor()
    {
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Polls now instead of waiting for the next tick.</summary>
    public void Refresh() => _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);

    private void Poll()
    {
        if (_disposed) return;
        // Refresh() can fire the timer while a poll is running. The running poll reschedules itself.
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var apps = Scan();
            var signature = string.Join("|", apps.Select(a =>
                $"{a.Name}:{a.IsActive}:{string.Join(",", a.Roots.Select(r => $"{r.ProcessId}>{r.InsideApp}"))}"));
            Current = apps;
            if (signature != _signature)
            {
                _signature = signature;
                Changed?.Invoke(this, apps);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Audio app scan failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
            try { if (!_disposed) _timer.Change(PollInterval, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    private List<AudioApp> Scan()
    {
        // Session owner PID -> whether any of its sessions is active.
        var sessions = new Dictionary<int, bool>();
        _enumerator ??= new MMDeviceEnumerator();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var list = manager.Sessions;
                for (var i = 0; i < list.Count; i++)
                {
                    using var session = list[i];
                    if (session.IsSystemSoundsSession) continue;
                    var state = session.State;
                    if (state == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                    var pid = (int)session.GetProcessID;
                    if (pid == 0) continue;
                    var active = state == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive;
                    sessions[pid] = sessions.TryGetValue(pid, out var was) ? was || active : active;
                }
            }
        }

        var tree = ProcessTree.Snapshot();

        // Group sessions by root process, then by app name.
        var roots = new Dictionary<int, (string Name, bool Active)>();
        foreach (var (pid, active) in sessions)
        {
            if (!tree.Contains(pid)) continue; // Exited since the session was listed.
            var root = tree.FindRoot(pid);
            var name = tree.NameOf(root) ?? tree.NameOf(pid);
            if (name is null) continue;
            roots[root] = roots.TryGetValue(root, out var r) ? (name, r.Active || active) : (name, active);
        }

        var rootIds = roots.Keys.ToHashSet();
        var apps = roots
            .GroupBy(r => r.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AudioApp(
                g.Key,
                DisplayNameFor(g.Key, g.First().Key),
                g.Select(r => new AppRoot(r.Key,
                        tree.FindAncestorIn(r.Key, rootIds) is { } outer ? roots[outer].Name : null))
                    .OrderBy(r => r.ProcessId).ToList(),
                g.Any(r => r.Value.Active)))
            .ToList();

        // Different apps can share a description (e.g. two builds of one program). Add the exe name so
        // the rows can be told apart.
        var shared = apps.GroupBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        return apps
            .Select(a => shared.Contains(a.DisplayName) ? a with { DisplayName = $"{a.DisplayName} ({a.Name})" } : a)
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>"Google Chrome" rather than "chrome", from the exe's description when it has one.</summary>
    private string DisplayNameFor(string name, int pid)
    {
        if (string.Equals(name, OwnName, StringComparison.OrdinalIgnoreCase)) return "Soundboard (this app)";
        if (_displayNames.TryGetValue(name, out var cached)) return cached;

        var display = name;
        try
        {
            using var process = Process.GetProcessById(pid);
            var description = process.MainModule?.FileVersionInfo.FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && description.Length <= 40) display = description;
        }
        catch (Exception)
        {
            // Not accessible (e.g. elevated or protected): fall back to the exe name, and don't retry.
        }
        _displayNames[name] = display;
        return display;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
