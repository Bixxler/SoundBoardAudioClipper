using System.IO;
using System.Windows;
using Microsoft.Win32;
using NAudio.Wave;
using Soundboard.Audio;

namespace Soundboard;

/// <summary>How the clip editor is set up: for a new recorded clip, or for trimming an existing pad.</summary>
public sealed record ClipEditorOptions(string Title, string Description, string InitialName, string SaveButtonText, bool SelectAll)
{
    public static ClipEditorOptions NewRecordedClip(string appName, string length) => new(
        "Make a clip",
        $"The last {length} of {appName} audio. Drag across the waveform to select the part you want. " +
        "Preview plays only on your monitor output, so the call doesn't hear it.",
        $"Clip {DateTime.Now:MMM d HHmmss}",
        "Add to soundboard",
        SelectAll: false);

    public static ClipEditorOptions Trim(string name) => new(
        $"Trim \"{name}\"",
        "Drag across the waveform to select the part to keep, then click \"Save trimmed clip\". You can rename it here too. " +
        "Your original file is never changed: the trimmed version is saved as a new clip.",
        name,
        "Save trimmed clip",
        SelectAll: true);
}

public partial class ClipEditorWindow : Window
{
    private const double DiscordSoundboardMax = 5.2;
    private const double VoteBotDefaultMax = 4.0;
    private const double MinClip = 0.1;

    private readonly float[] _samples;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly AudioEngine _engine;
    private readonly Action<string, string> _onSave;
    private IDisposable? _preview;

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
        else SelectLast(VoteBotDefaultMax);
    }

    private double Length => Waveform.SelectionEnd - Waveform.SelectionStart;

    private void SelectLast(double seconds) =>
        Waveform.SetSelection(Math.Max(0, Waveform.Duration - seconds), Waveform.Duration);

    private void UpdateSelectionText()
    {
        SelectionText.Text = $"Selected {Waveform.SelectionStart:0.00} s – {Waveform.SelectionEnd:0.00} s  ({Length:0.00} s)";
        FitText.Text = Length switch
        {
            < MinClip => "Drag across the waveform to choose a clip.",
            <= VoteBotDefaultMax => "✓ Fits the vote bot (4 s by default) and Discord's soundboard (5.2 s).",
            <= DiscordSoundboardMax => "✓ Fits Discord's soundboard (5.2 s), but it's longer than the vote bot's 4 s default.",
            _ => "Too long for Discord's soundboard (5.2 s max). It's still fine as a pad in this app.",
        };
    }

    private ISampleProvider Selection() =>
        ClipExport.Segment(_samples, _sampleRate, _channels, Waveform.SelectionStart, Waveform.SelectionEnd);

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
        _preview = _engine.Preview(Selection());
        if (_preview is null)
            MessageBox.Show(this, "Choose a device under \"You hear it on\" to preview clips.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPreview();

    private void StopPreview()
    {
        _preview?.Dispose();
        _preview = null;
    }

    private void SelectLast_Click(object sender, RoutedEventArgs e) => SelectLast(VoteBotDefaultMax);

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Waveform.SetSelection(0, Waveform.Duration);

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

    private void Window_Closed(object? sender, EventArgs e) => StopPreview();
}
