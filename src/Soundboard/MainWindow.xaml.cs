using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Soundboard.Audio;
using Soundboard.Setup;

namespace Soundboard;

public partial class MainWindow : Window
{
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".aiff", ".aif", ".wma", ".m4a"];

    private const string AppTitle = "Soundboard";

    private readonly AudioEngine _engine = new();
    private readonly ClipRecorder _recorder = new();
    private bool _settingClipToggle;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<SoundItem> _sounds;
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();

        _sounds = new ObservableCollection<SoundItem>(_settings.Sounds);
        _sounds.CollectionChanged += (_, _) => UpdateEmptyHint();
        Pads.ItemsSource = _sounds;
        UpdateEmptyHint();

        _engine.DeviceError += (_, message) => Dispatcher.BeginInvoke(() => ShowStatus(message));
        _engine.MicStatusChanged += (_, message) => Dispatcher.BeginInvoke(() => ShowStatus(message));
        StartMicMeter();
        _recorder.StoppedUnexpectedly += (_, message) => Dispatcher.BeginInvoke(() =>
        {
            StopClipRecorder(message);
            ShowStatus(message);
        });

        MonitorVolume.Value = _settings.MonitorVolume;
        VirtualMicVolume.Value = _settings.VirtualMicVolume;
        _engine.MonitorVolume = _settings.MonitorVolume;
        _engine.VirtualMicVolume = _settings.VirtualMicVolume;

        if (_settings.FirstRun)
        {
            _settings.MonitorDeviceId = _engine.DefaultOutputId();
            _settings.VirtualMicDeviceId = _engine.OutputDevices()
                .FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))?.Id;
            _settings.FirstRun = false;
        }

        LoadDevices();
        LoadClipSources();
        LoadClipLengths();
        _loading = false;
    }

    // ---- Devices ----

    private void LoadDevices()
    {
        _loading = true;
        var outputs = _engine.OutputDevices();
        var inputs = _engine.InputDevices();

        Select(MonitorDevice, outputs, _settings.MonitorDeviceId, "monitor");
        Select(VirtualMicDevice, outputs, _settings.VirtualMicDeviceId, "virtual mic");
        Select(MicrophoneDevice, inputs, _settings.MicrophoneDeviceId, "microphone");

        Apply(() => _engine.SetMonitor(Id(MonitorDevice)), "monitor output");
        Apply(() => _engine.SetVirtualMic(Id(VirtualMicDevice)), "virtual mic output");
        Apply(() => _engine.SetMicrophone(Id(MicrophoneDevice)), "microphone");

        var cableInstalled = FindCableInput(outputs) is not null;
        InstallCableButton.Visibility = cableInstalled ? Visibility.Collapsed : Visibility.Visible;

        if (Id(VirtualMicDevice) is null && string.IsNullOrEmpty(Status.Text))
            ShowStatus(cableInstalled
                ? "No virtual mic output selected. Choose \"CABLE Input\" so Discord or Zoom can hear the sounds."
                : "VB-Cable isn't installed. Click \"Install VB-Cable…\" so Discord or Zoom can hear the sounds.");
        _loading = false;
    }

    private static readonly System.Windows.Media.Brush MicOff = Frozen("#5A5C66");
    private static readonly System.Windows.Media.Brush MicLive = Frozen("#3FB950");
    private static readonly System.Windows.Media.Brush MicDown = Frozen("#E5484D");

    private static System.Windows.Media.Brush Frozen(string color)
    {
        var brush = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFrom(color)!;
        brush.Freeze();
        return brush;
    }

    /// <summary>Polls the mic level about 30 times a second. Shown on a 60 dB scale, falling back smoothly.</summary>
    private void StartMicMeter()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            var peak = _engine.TakeMicLevel();
            var target = peak <= 0.001f ? 0 : Math.Clamp(1 + 20 * Math.Log10(peak) / 60, 0, 1);
            MicLevel.Value = Math.Max(target, MicLevel.Value * 0.85);

            var (fill, tip) = !_engine.HasMic ? (MicOff, "No mic selected")
                : _engine.IsMicLive ? (MicLive, "Mic is live")
                : (MicDown, "Mic is disconnected. Reconnecting…");
            if (MicDot.Fill != fill) { MicDot.Fill = fill; MicDot.ToolTip = tip; }
        };
        timer.Start();
    }

    private static AudioDevice? FindCableInput(IEnumerable<AudioDevice> outputs) =>
        outputs.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));

    private async void InstallCable_Click(object sender, RoutedEventArgs e)
    {
        var proceed = MessageBox.Show(this,
            "This downloads VB-Audio Virtual Cable from vb-audio.com and runs its installer.\n\n" +
            "• Windows will ask for administrator permission.\n" +
            "• In the VB-Cable window, click \"Install Driver\", then close it.\n" +
            "• VB-Cable is donationware from VB-Audio (vb-audio.com/Cable).\n\nContinue?",
            "Install VB-Cable", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (proceed != MessageBoxResult.OK) return;

        InstallCableButton.IsEnabled = false;
        try
        {
            using var setup = await VbCableInstaller.PrepareAsync(new Progress<string>(ShowStatus));

            if (!setup.SignerLooksOfficial)
            {
                var trust = MessageBox.Show(this,
                    $"The installer is validly signed, but by \"{setup.Signer}\" rather than VB-Audio.\n\nRun it anyway?",
                    "Unexpected signer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (trust != MessageBoxResult.Yes) { ShowStatus("VB-Cable install cancelled."); return; }
            }

            ShowStatus("Waiting for the VB-Cable installer to finish…");
            if (!await VbCableInstaller.RunAsync(setup))
            {
                ShowStatus("VB-Cable install cancelled at the Windows permission prompt.");
                return;
            }

            var cable = FindCableInput(_engine.OutputDevices());
            if (cable is null)
            {
                ShowStatus("VB-Cable devices aren't showing yet. Restart Windows, then click \"Refresh devices\".");
                return;
            }

            _settings.VirtualMicDeviceId = cable.Id;
            Save();
            ShowStatus("");
            LoadDevices();
            ShowStatus("VB-Cable installed and selected. In Discord, set Input Device to \"CABLE Output\". Restart Windows if it doesn't work.");
        }
        catch (Exception ex)
        {
            ShowStatus($"VB-Cable install failed: {ex.Message}");
            var open = MessageBox.Show(this,
                $"Couldn't install VB-Cable automatically:\n{ex.Message}\n\nOpen the VB-Cable download page to install it manually?",
                "Install VB-Cable", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (open == MessageBoxResult.Yes) VbCableInstaller.OpenProductPage();
        }
        finally
        {
            InstallCableButton.IsEnabled = true;
        }
    }

    private void Select(ComboBox box, IReadOnlyList<AudioDevice> devices, string? savedId, string role)
    {
        List<AudioDevice> items = [AudioDevice.None, .. devices];
        box.ItemsSource = items;
        var match = items.FirstOrDefault(d => d.Id == savedId);
        if (match is null)
        {
            ShowStatus($"The saved {role} device isn't connected, so it is set to (none).");
            match = AudioDevice.None;
        }
        box.SelectedItem = match;
    }

    private static string? Id(ComboBox box) => (box.SelectedItem as AudioDevice)?.Id;

    private void MonitorDevice_Changed(object sender, SelectionChangedEventArgs e) =>
        OnDeviceChanged(() => { _settings.MonitorDeviceId = Id(MonitorDevice); _engine.SetMonitor(_settings.MonitorDeviceId); }, "monitor output");

    private void VirtualMicDevice_Changed(object sender, SelectionChangedEventArgs e) =>
        OnDeviceChanged(() => { _settings.VirtualMicDeviceId = Id(VirtualMicDevice); _engine.SetVirtualMic(_settings.VirtualMicDeviceId); }, "virtual mic output");

    private void MicrophoneDevice_Changed(object sender, SelectionChangedEventArgs e) =>
        OnDeviceChanged(() => { _settings.MicrophoneDeviceId = Id(MicrophoneDevice); _engine.SetMicrophone(_settings.MicrophoneDeviceId); }, "microphone");

    private void OnDeviceChanged(Action change, string what)
    {
        if (_loading) return;
        ShowStatus("");
        Apply(change, what);
        Save();
    }

    private void Apply(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { ShowStatus($"Couldn't open the {what}: {ex.Message}"); }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus("");
        LoadDevices();
    }

    private void MonitorVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _engine.MonitorVolume = _settings.MonitorVolume = (float)e.NewValue;
    }

    private void VirtualMicVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _engine.VirtualMicVolume = _settings.VirtualMicVolume = (float)e.NewValue;
    }

    // ---- Sounds ----

    private void Pad_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SoundItem sound) return;
        try
        {
            _engine.Play(sound.Path);
            ShowStatus($"Playing {sound.Name}");
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't play {sound.Name}: {ex.Message}");
        }
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _engine.StopAll();
        ShowStatus("Stopped");
    }

    private void RemovePad_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SoundItem sound) return;
        _sounds.Remove(sound);
        Save();
    }

    private static readonly TimeSpan MaxTrimLength = TimeSpan.FromMinutes(10);

    private void RenamePad_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SoundItem sound) return;
        var dialog = new RenameWindow(sound.Name) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.NewName == sound.Name) return;

        var oldName = sound.Name;
        sound.Name = dialog.NewName;
        Save();
        ShowStatus($"Renamed \"{oldName}\" to \"{sound.Name}\".");
    }

    private void TrimPad_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SoundItem sound) return;

        float[] samples;
        int sampleRate, channels;
        try
        {
            (samples, sampleRate, channels) = ClipExport.Load(sound.Path, MaxTrimLength);
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't open \"{sound.Name}\" for trimming: {ex.Message}");
            return;
        }

        new ClipEditorWindow(samples, sampleRate, channels, _engine, ClipEditorOptions.Trim(sound.Name),
            (name, path) => ReplaceWithTrimmed(sound, name, path)) { Owner = this }.ShowDialog();
    }

    private void ReplaceWithTrimmed(SoundItem sound, string name, string trimmedPath)
    {
        var oldPath = sound.Path;
        sound.Name = name;
        sound.Path = trimmedPath;
        Save();

        // The app's own clips are replaced; the user's own files are never touched.
        var note = ". Your original file was left unchanged.";
        if (AppSettings.IsAppClip(oldPath))
        {
            note = "";
            try { File.Delete(oldPath); }
            catch (IOException) { } // Still playing. The old copy just stays in the Clips folder.
            catch (UnauthorizedAccessException) { }
        }
        ShowStatus($"Trimmed \"{name}\"{note}");
    }

    private void AddSounds_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add sounds",
            Multiselect = true,
            Filter = "Audio files|" + string.Join(';', AudioExtensions.Select(x => "*" + x)),
        };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
    }

    private void AddFiles(IEnumerable<string> files)
    {
        var added = 0;
        foreach (var file in files)
        {
            if (!AudioExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
            if (_sounds.Any(s => string.Equals(s.Path, file, StringComparison.OrdinalIgnoreCase))) continue;
            _sounds.Add(new SoundItem { Name = Path.GetFileNameWithoutExtension(file), Path = file });
            added++;
        }
        ShowStatus(added == 0 ? "No new audio files to add." : $"Added {added} sound{(added == 1 ? "" : "s")}.");
        Save();
    }

    // ---- Clip recorder ----

    private bool _loadingClipSources;

    /// <summary>
    /// Fills the "Record from" list with the apps that have open windows. Discord is added even when it's
    /// only in the tray. Keeps the current choice, or picks the saved app, or Discord.
    /// </summary>
    private void LoadClipSources()
    {
        var current = ClipSource.SelectedItem as AudioSource;
        var sources = AudioSource.ListWindows();
        if (!sources.Any(s => s.IsDiscord) && AudioSource.FindDiscord() is { } discord)
            sources.Add(discord with { WindowTitle = "Discord (in the tray)" });

        var wanted = current?.ProcessName ?? _settings.ClipSourceProcessName;
        var selected =
            (current is null ? null : sources.FirstOrDefault(s => s.ProcessId == current.ProcessId)) ??
            (wanted is null ? null : sources.FirstOrDefault(s => string.Equals(s.ProcessName, wanted, StringComparison.OrdinalIgnoreCase))) ??
            sources.FirstOrDefault(s => s.IsDiscord);

        _loadingClipSources = true;
        ClipSource.ItemsSource = sources;
        ClipSource.SelectedItem = selected;
        _loadingClipSources = false;
    }

    private void LoadClipLengths()
    {
        var saved = TimeSpan.FromSeconds(_settings.ClipBufferSeconds);
        // A saved length that's no longer offered becomes the longest option that fits within it.
        if (!ClipRecorder.BufferLengths.Contains(saved))
            saved = ClipRecorder.BufferLengths.Where(l => l <= saved).DefaultIfEmpty(ClipRecorder.DefaultBufferLength).Max();

        foreach (var length in ClipRecorder.BufferLengths)
        {
            var item = new ComboBoxItem
            {
                Content = $"{ClipRecorder.Format(length)}  (~{ClipRecorder.MemoryMegabytes(length):0} MB)",
                Tag = length,
            };
            ClipLength.Items.Add(item);
            if (length == saved) ClipLength.SelectedItem = item;
        }
        UpdateClipLengthText();
    }

    private TimeSpan SelectedClipLength =>
        (ClipLength.SelectedItem as ComboBoxItem)?.Tag as TimeSpan? ?? ClipRecorder.DefaultBufferLength;

    private void ClipLength_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateClipLengthText();
        if (_loading) return;
        _settings.ClipBufferSeconds = (int)SelectedClipLength.TotalSeconds;
        Save();
    }

    /// <summary>While recording, the button shows the length actually being kept; when off, the one that's chosen.</summary>
    private void UpdateClipLengthText()
    {
        var length = _recorder.IsRecording ? _recorder.BufferLength : SelectedClipLength;
        SaveClipButton.Content = $"✂  Clip last {ClipRecorder.Format(length)}…";
    }

    private void ClipSource_DropDownOpened(object? sender, EventArgs e) => LoadClipSources();

    private void RefreshClipSources_Click(object sender, RoutedEventArgs e) => LoadClipSources();

    private void ClipSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingClipSources || ClipSource.SelectedItem is not AudioSource source) return;
        _settings.ClipSourceProcessName = source.ProcessName;
        Save();
    }

    private async void ClipToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingClipToggle) return;

        if (ClipSource.SelectedItem is not AudioSource source)
        {
            SetClipToggle(false);
            ClipStatus.Text = "Pick a window to record from first. If Discord isn't listed, open the Discord desktop app.";
            return;
        }

        if (!_settings.ClipConsentAcknowledged)
        {
            var ok = MessageBox.Show(this,
                "The clip recorder keeps the last few seconds or minutes of the chosen app's audio, which can include other people's voices.\n\n" +
                "• Tell people in the call when it's on, and only clip people who agree. Recording people without consent " +
                "breaks Discord's rules and is illegal in many places.\n" +
                "• Audio stays in memory and is only saved when you save a clip.\n" +
                "• Only the app you pick is captured: no other apps, and not your own mic.\n\nTurn it on?",
                "Clip recorder", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (ok != MessageBoxResult.OK)
            {
                SetClipToggle(false);
                return;
            }
            _settings.ClipConsentAcknowledged = true;
            Save();
        }

        ClipToggle.IsEnabled = false;
        SetClipSourceEnabled(false);
        ClipStatus.Text = $"Connecting to {source.AppName}…";
        try
        {
            var length = SelectedClipLength;
            await _recorder.StartAsync(source, length);
            ClipToggle.Content = "Clip recorder: On";
            var kept = $"the last {ClipRecorder.Format(length)} are kept in memory.";
            ClipStatus.Text = source.IsDiscord ? $"Recording Discord: {kept} Let the call know." : $"Recording {source.AppName}: {kept}";
            UpdateClipLengthText();
            RecDot.Visibility = Visibility.Visible;
            SaveClipButton.IsEnabled = true;
            Title = "● REC  " + AppTitle;
        }
        catch (Exception ex)
        {
            StopClipRecorder(ex.Message);
        }
        finally
        {
            ClipToggle.IsEnabled = true;
        }
    }

    private void ClipToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_settingClipToggle) return;
        StopClipRecorder("Off. Buffered audio was discarded.");
    }

    private void StopClipRecorder(string message)
    {
        _recorder.Stop();
        SetClipToggle(false);
        SetClipSourceEnabled(true);
        UpdateClipLengthText();
        ClipToggle.Content = "Clip recorder: Off";
        ClipStatus.Text = message;
        RecDot.Visibility = Visibility.Collapsed;
        SaveClipButton.IsEnabled = false;
        Title = AppTitle;
    }

    /// <summary>The source can't change while recording: the buffer holds one app's audio.</summary>
    private void SetClipSourceEnabled(bool enabled)
    {
        ClipSource.IsEnabled = enabled;
        RefreshClipSourcesButton.IsEnabled = enabled;
        ClipLength.IsEnabled = enabled;
    }

    private void SetClipToggle(bool on)
    {
        _settingClipToggle = true;
        ClipToggle.IsChecked = on;
        _settingClipToggle = false;
    }

    private void SaveClip_Click(object sender, RoutedEventArgs e)
    {
        var appName = _recorder.Source?.AppName ?? "the app's";
        var length = ClipRecorder.Format(_recorder.BufferLength);
        var samples = _recorder.Snapshot();
        if (samples.Length == 0)
        {
            ShowStatus("Nothing captured yet. Wait a moment after turning the clip recorder on.");
            return;
        }
        new ClipEditorWindow(samples, _recorder.SampleRate, _recorder.Channels, _engine,
            ClipEditorOptions.NewRecordedClip(appName, length), AddClipPad) { Owner = this }.ShowDialog();
    }

    private void AddClipPad(string name, string path)
    {
        _sounds.Add(new SoundItem { Name = name, Path = path });
        Save();
        ShowStatus($"Added \"{name}\" to the soundboard. Its file is in {Path.GetDirectoryName(path)}.");
    }

    // ---- Shared ----

    private void UpdateEmptyHint() => EmptyHint.Visibility = _sounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ShowStatus(string message) => Status.Text = message;

    private void Save()
    {
        _settings.Sounds = [.. _sounds];
        try { _settings.Save(); }
        catch (Exception ex) { ShowStatus($"Couldn't save settings: {ex.Message}"); }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Save();
        _recorder.Dispose();
        _engine.Dispose();
    }
}
