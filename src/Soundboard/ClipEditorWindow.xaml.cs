using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using Soundboard.Audio;

namespace Soundboard;

/// <summary>How the clip editor is set up: for a new recorded clip, or for editing an existing pad.</summary>
public sealed record ClipEditorOptions(string Title, string Description, string InitialName, string SaveButtonText, bool SelectAll)
{
    public static ClipEditorOptions NewRecordedClip(string appName, string length) => new(
        "Make a clip",
        $"The last {length} of {appName} audio. Drag across the waveform to select the part you want. " +
        "Preview plays only on your monitor output, so the call doesn't hear it.",
        $"Clip {DateTime.Now:MMM d HHmmss}",
        "Add to soundboard",
        SelectAll: false);

    public static ClipEditorOptions Edit(string name) => new(
        $"Edit \"{name}\"",
        "Drag across the waveform to select the part to keep, and add effects if you like. " +
        "The edited version is added as a new sound next to the original, which stays as it is.",
        $"{name} (edited)",
        "Save as new sound",
        SelectAll: true);
}

public partial class ClipEditorWindow : Window
{
    private const double DefaultClipSeconds = 4.0;
    private const double MinClip = 0.1;

    private readonly float[] _samples;
    private EffectSettings _fx = EffectSettings.None;
    private bool _loadingFx;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly AudioEngine _engine;
    private readonly Action<string, string> _onSave;
    private IDisposable? _preview;

    // Playhead: how far the preview has got, mapped back onto the waveform.
    private const double OutputLatencySeconds = 0.05;
    private readonly DispatcherTimer _playheadTimer;
    private CountingSampleProvider? _playing;
    private double _playFrom, _playTo, _playSpeed = 1;

    private readonly CancellationTokenSource _closing = new();

    /// <param name="onSave">Called with (name, path of the new MP3 in the Clips folder) after the primary button saves.</param>
    public ClipEditorWindow(float[] samples, int sampleRate, int channels, AudioEngine engine,
        ClipEditorOptions options, Action<string, string> onSave)
    {
        InitializeComponent();
        _samples = samples;
        _sampleRate = sampleRate;
        _channels = channels;
        _engine = engine;
        _onSave = onSave;

        Title = options.Title;
        Description.Text = options.Description;
        PrimaryButton.Content = options.SaveButtonText;
        ClipName.Text = options.InitialName;
        Waveform.SelectionChanged += (_, _) => UpdateSelectionText();
        Waveform.SetAudio(samples, sampleRate, channels);
        if (options.SelectAll) Waveform.SetSelection(0, Waveform.Duration);
        else SelectLast(DefaultClipSeconds);
        UpdateEffectLabels();

        _playheadTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();
        _ = FindVoiceAsync();
    }

    /// <summary>Marks where there's voice. Runs in the background; Reverse waits for it because it flips the audio in place.</summary>
    private async Task FindVoiceAsync()
    {
        ReverseToggle.IsEnabled = false;
        try
        {
            var ranges = await Task.Run(() => VoiceDetector.Detect(_samples, _sampleRate, _channels, _closing.Token));
            Waveform.Voice = ranges;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Write($"Voice detection failed: {ex.Message}");
        }
        ReverseToggle.IsEnabled = true;
    }

    private double Length => Waveform.SelectionEnd - Waveform.SelectionStart;

    private void SelectLast(double seconds) =>
        Waveform.SetSelection(Math.Max(0, Waveform.Duration - seconds), Waveform.Duration);

    /// <summary>How long the saved clip will be: effects like pitch, reverb and echo change it.</summary>
    private double OutputLength => Length < MinClip ? Length : _fx.OutputSeconds(Length);

    private void UpdateSelectionText()
    {
        SelectionText.Text = $"Selected {Waveform.SelectionStart:0.00} s – {Waveform.SelectionEnd:0.00} s  ({Length:0.00} s)" +
            (_fx.IsNone ? "" : $"  →  {OutputLength:0.00} s with effects");
    }

    /// <summary>The selected audio with the effects applied, ready to preview or save.</summary>
    private ISampleProvider Selection()
    {
        var slice = ClipExport.Slice(_samples, _sampleRate, _channels, Waveform.SelectionStart, Waveform.SelectionEnd);
        return ClipExport.FromArray(Effects.Apply(slice, _sampleRate, _channels, _fx), _sampleRate, _channels);
    }

    // ---- Effects ----

    private void Effect_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingFx || !IsLoaded) return;
        _fx = new EffectSettings(
            VolumeDb: VolumeSlider.Value,
            BassDb: BassSlider.Value,
            PitchSemitones: PitchSlider.Value,
            Reverb: ReverbSlider.Value / 100,
            Echo: EchoSlider.Value / 100,
            Distortion: DistortionSlider.Value / 100);
        StopPreview(); // The preview was made with the old settings.
        UpdateEffectLabels();
        UpdateSelectionText();
    }

    private void ResetEffects_Click(object sender, RoutedEventArgs e)
    {
        _loadingFx = true;
        VolumeSlider.Value = BassSlider.Value = PitchSlider.Value = ReverbSlider.Value = EchoSlider.Value = DistortionSlider.Value = 0;
        _loadingFx = false;
        _fx = EffectSettings.None;
        StopPreview();
        UpdateEffectLabels();
        UpdateSelectionText();
    }

    private void UpdateEffectLabels()
    {
        VolumeValue.Text = _fx.VolumeDb == 0 ? "0 dB" : $"{_fx.VolumeDb:+0;-0} dB";
        BassValue.Text = _fx.BassDb == 0 ? "Off" : $"+{_fx.BassDb:0} dB";
        PitchValue.Text = _fx.PitchSemitones == 0 ? "Normal" : $"{_fx.PitchSemitones:+0;-0} st";
        ReverbValue.Text = Percent(_fx.Reverb);
        EchoValue.Text = Percent(_fx.Echo);
        DistortionValue.Text = Percent(_fx.Distortion);
        ResetEffectsButton.IsEnabled = !_fx.IsNone;

        static string Percent(double amount) => amount == 0 ? "Off" : $"{amount * 100:0}%";
    }

    private bool HasSelection()
    {
        if (Length >= MinClip) return true;
        MessageBox.Show(this, "Select part of the waveform first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelection()) return;
        StopPreview();
        var selection = new CountingSampleProvider(Selection());
        _preview = _engine.Preview(selection);
        if (_preview is null)
        {
            MessageBox.Show(this, "Choose a device under \"You hear it on\" to preview clips.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        (_playing, _playFrom, _playTo, _playSpeed) = (selection, Waveform.SelectionStart, Waveform.SelectionEnd, _fx.Speed);
        Waveform.Playhead = _playFrom;
        _playheadTimer.Start();
    }

    /// <summary>
    /// Moves the playhead. Pitch changes the speed, so the time played is scaled back to the original
    /// audio. During a reverb or echo tail it waits at the end of the selection.
    /// </summary>
    private void UpdatePlayhead()
    {
        if (_playing is null) return;
        // Once everything has been handed to the output, give the device a moment to play it, then hide the line.
        if (_playing.FinishedFor > TimeSpan.FromSeconds(OutputLatencySeconds * 2))
        {
            StopPlayhead();
            return;
        }
        var heard = Math.Max(0, _playing.Seconds - OutputLatencySeconds);
        Waveform.Playhead = Math.Min(_playTo, _playFrom + heard * _playSpeed);
    }

    private void StopPlayhead()
    {
        _playheadTimer.Stop();
        _playing = null;
        Waveform.Playhead = null;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPreview();

    private void StopPreview()
    {
        _preview?.Dispose();
        _preview = null;
        StopPlayhead();
    }

    private void SelectLast_Click(object sender, RoutedEventArgs e) => SelectLast(DefaultClipSeconds);

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Waveform.SetSelection(0, Waveform.Duration);

    /// <summary>
    /// Flips the audio so it plays backwards. The selection is mirrored too, so it still covers the same
    /// moment, now played backwards. Preview and save use whichever way round it is.
    /// </summary>
    private void Reverse_Changed(object sender, RoutedEventArgs e)
    {
        StopPreview();
        var (start, end) = (Waveform.SelectionStart, Waveform.SelectionEnd);
        ClipExport.Reverse(_samples, _channels);
        var duration = Waveform.Duration;
        Waveform.SetAudio(_samples, _sampleRate, _channels);
        Waveform.Voice = Waveform.Voice.Reverse().Select(r => new TimeRange(duration - r.End, duration - r.Start)).ToList();
        Waveform.SetSelection(duration - end, duration - start);
    }

    private void SaveMp3_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelection()) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save clip",
            Filter = "MP3 audio|*.mp3",
            FileName = FileNames.Safe(ClipName.Text) + ".mp3",
        };
        if (dialog.ShowDialog(this) != true) return;
        if (TrySave(dialog.FileName))
            MessageBox.Show(this, $"Saved {Path.GetFileName(dialog.FileName)}.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelection()) return;
        Directory.CreateDirectory(AppSettings.ClipsFolder);
        var name = string.IsNullOrWhiteSpace(ClipName.Text) ? "Clip" : ClipName.Text.Trim();
        var path = FileNames.Unique(AppSettings.ClipsFolder, FileNames.Safe(name), ".mp3");
        if (!TrySave(path)) return;
        StopPreview();
        _onSave(name, path);
        Close();
    }

    private bool TrySave(string path)
    {
        try
        {
            ClipExport.SaveMp3(Selection(), path);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the clip:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closing.Cancel();
        StopPreview();
    }

    /// <summary>Passes audio through unchanged, counting how much has been played.</summary>
    private sealed class CountingSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private long _samples;
        private long _finishedAt; // Environment.TickCount64 when the source ran out, 0 while playing.

        public WaveFormat WaveFormat => source.WaveFormat;
        public double Seconds => Interlocked.Read(ref _samples) / (double)WaveFormat.Channels / WaveFormat.SampleRate;
        public bool Finished => Interlocked.Read(ref _finishedAt) != 0;
        public TimeSpan FinishedFor => Finished ? TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _finishedAt)) : TimeSpan.Zero;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            Interlocked.Add(ref _samples, read);
            if (read < count && !Finished) Interlocked.Exchange(ref _finishedAt, Environment.TickCount64);
            return read;
        }
    }
}
