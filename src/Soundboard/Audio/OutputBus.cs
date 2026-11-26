using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundboard.Audio;

/// <summary>
/// One output device that is always running. Sounds go into a sub-mixer that has its own
/// volume and can be cleared by Stop All. Persistent inputs, such as mic passthrough, go
/// straight into the master mixer, so they ignore that volume and survive Stop All.
/// </summary>
internal sealed class OutputBus : IDisposable
{
    private readonly WasapiOut _output;
    private readonly MixingSampleProvider _master;
    private readonly MixingSampleProvider _sounds;
    private readonly VolumeSampleProvider _soundsVolume;
    private readonly List<PlayingSound> _playing = [];

    public OutputBus(MMDevice device)
    {
        DeviceName = device.FriendlyName;

        // Use the device's own sample rate so Windows doesn't have to resample.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(device.AudioClient.MixFormat.SampleRate, 2);
        _sounds = new MixingSampleProvider(format) { ReadFully = true };
        _sounds.MixerInputEnded += OnSoundEnded;
        _soundsVolume = new VolumeSampleProvider(_sounds);
        _master = new MixingSampleProvider(format) { ReadFully = true };
        _master.AddMixerInput(_soundsVolume);

        _output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
        _output.PlaybackStopped += (_, e) => Stopped?.Invoke(this, e.Exception);
        _output.Init(_master);
        _output.Play();
    }

    public string DeviceName { get; }
    public WaveFormat Format => _master.WaveFormat;

    /// <summary>Raised on the audio thread when the device stops, for example because it was unplugged.</summary>
    public event EventHandler<Exception?>? Stopped;

    public float Volume
    {
        get => _soundsVolume.Volume;
        set => _soundsVolume.Volume = value;
    }

    public void Play(string path)
    {
        var audio = AudioFiles.Open(path);
        Play(audio.Samples, owner: audio);
    }

    /// <summary>Plays a source. Disposing the returned handle stops it early.</summary>
    public IDisposable Play(ISampleProvider source, IDisposable? owner)
    {
        var sound = new PlayingSound(owner, source.ConvertTo(Format));
        lock (_playing) _playing.Add(sound);
        _sounds.AddMixerInput(sound);
        return sound;
    }

    public void StopAll()
    {
        // RemoveAllMixerInputs takes the mixer's lock, so the audio thread can't still be
        // reading these readers when they are disposed below.
        _sounds.RemoveAllMixerInputs();
        PlayingSound[] stopped;
        lock (_playing)
        {
            stopped = [.. _playing];
            _playing.Clear();
        }
        foreach (var s in stopped) s.Dispose();
    }

    public void AddPersistentInput(ISampleProvider input) => _master.AddMixerInput(input.ConvertTo(Format));

    private void OnSoundEnded(object? sender, SampleProviderEventArgs e)
    {
        if (e.SampleProvider is not PlayingSound sound) return;
        lock (_playing) _playing.Remove(sound);
        sound.Dispose();
    }

    public void Dispose()
    {
        _output.Dispose();
        StopAll();
    }

    /// <summary>
    /// Wraps a converted stream and owns its source (such as a file reader), so the source is
    /// closed when the sound ends. Once disposed it reads 0 samples, and the mixer then drops it.
    /// </summary>
    private sealed class PlayingSound(IDisposable? owner, ISampleProvider converted) : ISampleProvider, IDisposable
    {
        private int _disposed;
        public WaveFormat WaveFormat => converted.WaveFormat;
        public int Read(float[] buffer, int offset, int count) =>
            Volatile.Read(ref _disposed) == 1 ? 0 : converted.Read(buffer, offset, count);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner?.Dispose();
        }
    }
}
