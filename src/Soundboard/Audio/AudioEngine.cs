using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundboard.Audio;

public sealed record AudioDevice(string? Id, string Name)
{
    public static readonly AudioDevice None = new(null, "(none)");
    public override string ToString() => Name;
}

/// <summary>
/// Plays every sound on two outputs at once: the monitor (what you hear) and the virtual
/// mic (a virtual cable, such as VB-Cable, that Discord or Zoom uses as its microphone).
/// Your real mic can optionally be mixed into the virtual mic as well.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly MMDeviceEnumerator _devices = new();
    private OutputBus? _monitor;
    private OutputBus? _virtualMic;
    private MicPassthrough? _passthrough;
    private string? _microphoneId;
    private float _monitorVolume = 1f;
    private float _virtualMicVolume = 1f;

    /// <summary>Raised on a background thread when an output device stops unexpectedly.</summary>
    public event EventHandler<string>? DeviceError;

    public IReadOnlyList<AudioDevice> OutputDevices() => List(DataFlow.Render);
    public IReadOnlyList<AudioDevice> InputDevices() => List(DataFlow.Capture);

    public string? DefaultOutputId()
    {
        try { return _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch { return null; }
    }

    public float MonitorVolume
    {
        get => _monitorVolume;
        set { _monitorVolume = value; if (_monitor != null) _monitor.Volume = value; }
    }

    /// <summary>Boost for the mic passthrough (1 = unchanged). Kept across mic and device changes.</summary>
    public float MicGain
    {
        get => _micGain;
        set { _micGain = value; if (_passthrough != null) _passthrough.Gain = value; }
    }

    private float _micGain = 1f;

    public float VirtualMicVolume
    {
        get => _virtualMicVolume;
        set { _virtualMicVolume = value; if (_virtualMic != null) _virtualMic.Volume = value; }
    }

    public void SetMonitor(string? deviceId) => _monitor = Replace(_monitor, deviceId, _monitorVolume);

    public void SetVirtualMic(string? deviceId)
    {
        _virtualMic = Replace(_virtualMic, deviceId, _virtualMicVolume);
        RebuildPassthrough();
    }

    public void SetMicrophone(string? deviceId)
    {
        _microphoneId = deviceId;
        RebuildPassthrough();
    }

    public void Play(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Sound file not found.", path);
        _monitor?.Play(path);
        _virtualMic?.Play(path);
    }

    /// <summary>
    /// Plays audio on the monitor output only, so a clip can be checked without the call
    /// hearing it. Returns null if no monitor device is selected. Dispose the result to stop.
    /// </summary>
    public IDisposable? Preview(ISampleProvider clip) => _monitor?.Play(clip, owner: null);

    public void StopAll()
    {
        _monitor?.StopAll();
        _virtualMic?.StopAll();
    }

    private OutputBus? Replace(OutputBus? current, string? deviceId, float volume)
    {
        current?.Dispose();
        if (deviceId is null) return null;

        var bus = new OutputBus(_devices.GetDevice(deviceId)) { Volume = volume };
        bus.Stopped += (_, ex) =>
            DeviceError?.Invoke(this, $"{bus.DeviceName} stopped{(ex is null ? "" : $": {ex.Message}")}");
        return bus;
    }

    private void RebuildPassthrough()
    {
        _passthrough?.Dispose();
        _passthrough = null;
        if (_microphoneId is null || _virtualMic is null) return;

        // The passthrough's buffer can feed only one mixer, so it is rebuilt whenever the
        // virtual-mic bus is replaced.
        var micId = _microphoneId;
        var passthrough = new MicPassthrough(() => OpenCapture(micId), _virtualMic.Format) { Gain = _micGain };
        passthrough.StatusChanged += message => MicStatusChanged?.Invoke(this, message);
        _virtualMic.AddPersistentInput(passthrough.Output);
        _passthrough = passthrough;

        // The first open happens inside the constructor, before anyone could subscribe.
        if (!passthrough.IsLive)
            MicStatusChanged?.Invoke(this, "Couldn't open your mic. Retrying…");
    }

    /// <summary>
    /// Looks the mic up again on every call, because reconnects run on the watchdog's thread and the
    /// device object may be stale after a disconnect. A fresh enumerator avoids cross-thread COM use.
    /// </summary>
    private static IWaveIn OpenCapture(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        return new WasapiCapture(enumerator.GetDevice(deviceId), useEventSync: true, audioBufferMillisecondsLength: 20);
    }

    /// <summary>Raised on a background thread when the mic drops out or comes back.</summary>
    public event EventHandler<string>? MicStatusChanged;

    /// <summary>True while the passthrough mic is capturing. False if none is selected or it's reconnecting.</summary>
    public bool IsMicLive => _passthrough?.IsLive ?? false;

    public bool HasMic => _passthrough is not null;

    /// <summary>The mic's loudest sample since the last call (0 to 1).</summary>
    public float TakeMicLevel() => _passthrough?.TakeLevel() ?? 0f;

    private List<AudioDevice> List(DataFlow flow) =>
        [.. _devices.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDevice(d.ID, d.FriendlyName))
            .OrderBy(d => d.Name)];

    public void Dispose()
    {
        _passthrough?.Dispose();
        _virtualMic?.Dispose();
        _monitor?.Dispose();
        _devices.Dispose();
    }
}
