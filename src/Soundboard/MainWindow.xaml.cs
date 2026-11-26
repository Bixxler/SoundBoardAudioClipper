using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Soundboard.Audio;
using Soundboard.Setup;

namespace Soundboard;

public partial class MainWindow : Window
{
    private static readonly string[] AudioExtensions = AudioFiles.Extensions;

    private const string AppTitle = "Soundboard";

    private readonly AudioEngine _engine = new();
    private readonly AudioAppMonitor _appMonitor = new();
    private readonly ClipRecorder _recorder;
    private bool _settingClipToggle;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<SoundItem> _sounds;
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();
        ClearShareFolder();

        _sounds = new ObservableCollection<SoundItem>(_settings.Sounds);
        _sounds.CollectionChanged += (_, _) => UpdateEmptyHint();
        Pads.ItemsSource = _sounds;
        UpdateEmptyHint();

        _engine.DeviceError += (_, message) => Dispatcher.BeginInvoke(() => ShowStatus(message));
        _engine.MicStatusChanged += (_, message) => Dispatcher.BeginInvoke(() => ShowStatus(message));
        StartMicMeter();
        _recorder = new ClipRecorder(_appMonitor);
        ClipApps.ItemsSource = _clipApps;
        _appMonitor.Changed += (_, apps) => Dispatcher.BeginInvoke(() => UpdateClipApps(apps));
        _recorder.SourcesChanged += (_, _) => Dispatcher.BeginInvoke(() => UpdateClipApps(_appMonitor.Current));
        UpdateClipApps(_appMonitor.Current);

        MonitorVolume.Value = _settings.MonitorVolume;
        VirtualMicVolume.Value = _settings.VirtualMicVolume;
        _engine.MonitorVolume = _settings.MonitorVolume;
        _engine.VirtualMicVolume = _settings.VirtualMicVolume;
        MicBoost.Value = _settings.MicBoostDb;
        _engine.MicGain = DbToGain(_settings.MicBoostDb);
        UpdateMicBoostText();

        if (_settings.FirstRun)
        {
            _settings.MonitorDeviceId = _engine.DefaultOutputId();
            _settings.VirtualMicDeviceId = _engine.OutputDevices()
                .FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))?.Id;
            _settings.FirstRun = false;
        }

        LoadDevices();
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

    private void MicBoost_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MicBoostText is null) return; // Fires during InitializeComponent.
        UpdateMicBoostText();
        if (_loading) return;
        _settings.MicBoostDb = e.NewValue;
        _engine.MicGain = DbToGain(e.NewValue);
    }

    private void UpdateMicBoostText() => MicBoostText.Text = MicBoost.Value == 0 ? "Off" : $"+{MicBoost.Value:0} dB";

    private static float DbToGain(double db) => (float)Math.Pow(10, db / 20);

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

    // ---- Export ----

    /// <summary>Where named copies for dragging and the clipboard go. Cleared at startup.</summary>
    private static readonly string ShareFolder = Path.Combine(Path.GetTempPath(), "Soundboard", "Share");

    private Point? _padDragStart;
    private bool _draggingPad;

    private static SoundItem? SoundOf(object sender) => (sender as FrameworkElement)?.DataContext as SoundItem;

    /// <summary>The sound's file, or null (with a status message) if it's gone.</summary>
    private string? ExistingFile(SoundItem sound)
    {
        if (File.Exists(sound.Path)) return sound.Path;
        ShowStatus($"Can't find the file for \"{sound.Name}\". It was at {sound.Path}.");
        return null;
    }

    /// <summary>
    /// A copy of the sound named after its pad, for dragging or pasting somewhere else, so the other app
    /// gets "west side.mp3" rather than whatever the file happens to be called. The file itself is used
    /// when its name already matches.
    /// </summary>
    private static string NamedCopy(SoundItem sound)
    {
        var name = FileNames.Safe(sound.Name, "Sound");
        if (string.Equals(Path.GetFileNameWithoutExtension(sound.Path), name, StringComparison.OrdinalIgnoreCase))
            return sound.Path;

        // One folder per copy, so two pads with the same name don't overwrite each other's copy.
        var folder = Path.Combine(ShareFolder, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, name + Path.GetExtension(sound.Path));
        File.Copy(sound.Path, copy);
        return copy;
    }

    private static void ClearShareFolder()
    {
        try { if (Directory.Exists(ShareFolder)) Directory.Delete(ShareFolder, recursive: true); }
        catch (Exception) { } // Something still has a copy open. It's a temp folder, so leave it.
    }

    private void ExportPad_Click(object sender, RoutedEventArgs e)
    {
        if (SoundOf(sender) is not { } sound || ExistingFile(sound) is not { } file) return;
        var extension = Path.GetExtension(file);
        var dialog = new SaveFileDialog
        {
            Title = $"Export \"{sound.Name}\"",
            FileName = FileNames.Safe(sound.Name, "Sound") + extension,
            DefaultExt = extension,
            Filter = $"{extension.TrimStart('.').ToUpperInvariant()} file|*{extension}|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                File.Copy(file, dialog.FileName, overwrite: true); // The dialog already asked about overwriting.
            ShowStatus($"Exported \"{sound.Name}\" to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't export \"{sound.Name}\": {ex.Message}");
        }
    }

    private void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_sounds.Count == 0)
        {
            ShowStatus("There are no sounds to export yet.");
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Export all sounds to a folder" };
        if (dialog.ShowDialog(this) != true) return;

        int exported = 0, missing = 0, failed = 0;
        foreach (var sound in _sounds)
        {
            if (!File.Exists(sound.Path)) { missing++; continue; }
            try
            {
                var target = FileNames.Unique(dialog.FolderName, FileNames.Safe(sound.Name, "Sound"), Path.GetExtension(sound.Path));
                File.Copy(sound.Path, target);
                exported++;
            }
            catch (Exception ex)
            {
                Log.Write($"Export of {sound.Path} failed: {ex.Message}");
                failed++;
            }
        }

        var message = $"Exported {exported} sound{(exported == 1 ? "" : "s")} to {dialog.FolderName}.";
        if (missing > 0) message += $" {missing} couldn't be found.";
        if (failed > 0) message += $" {failed} couldn't be copied (see the log).";
        ShowStatus(message);
    }

    private void CopyPadFile_Click(object sender, RoutedEventArgs e)
    {
        if (SoundOf(sender) is not { } sound || ExistingFile(sound) is null) return;
        try
        {
            Clipboard.SetFileDropList([NamedCopy(sound)]);
            ShowStatus($"Copied \"{sound.Name}\". Paste it into a folder, a Discord message or another app.");
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't copy \"{sound.Name}\": {ex.Message}");
        }
    }

    private void ShowPadInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (SoundOf(sender) is not { } sound || ExistingFile(sound) is not { } file) return;
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"")?.Dispose();
    }

    private void Pad_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _padDragStart = e.GetPosition(this);

    /// <summary>Dragging a pad drops its file into Explorer, Discord, a browser upload or any app that takes files.</summary>
    private void Pad_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_padDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _padDragStart = null;
        if (SoundOf(sender) is not { } sound || !File.Exists(sound.Path)) return;

        string file;
        try { file = NamedCopy(sound); }
        catch (Exception ex) { ShowStatus($"Couldn't drag \"{sound.Name}\": {ex.Message}"); return; }

        // Letting go of the button's mouse capture means the drag doesn't also count as a click that plays it.
        ((UIElement)sender).ReleaseMouseCapture();
        _draggingPad = true;
        try
        {
            var result = DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, new[] { file }),
                DragDropEffects.Copy);
            if (result != DragDropEffects.None) ShowStatus($"Copied \"{sound.Name}\".");
        }
        finally
        {
            _draggingPad = false;
        }
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

    /// <summary>Adds a backwards copy of the sound as a new pad next to it. The original isn't touched.</summary>
    private async void ReversePad_Click(object sender, RoutedEventArgs e)
    {
        if (SoundOf(sender) is not { } sound || ExistingFile(sound) is not { } file) return;

        var name = $"{sound.Name} (reversed)";
        ShowStatus($"Reversing \"{sound.Name}\"…");
        try
        {
            var path = await Task.Run(() =>
            {
                var (samples, sampleRate, channels) = ClipExport.Load(file, MaxTrimLength);
                ClipExport.Reverse(samples, channels);
                Directory.CreateDirectory(AppSettings.ClipsFolder);
                var target = FileNames.Unique(AppSettings.ClipsFolder, FileNames.Safe(name), ".mp3");
                var duration = samples.Length / (double)channels / sampleRate;
                ClipExport.SaveMp3(ClipExport.Segment(samples, sampleRate, channels, 0, duration), target);
                return target;
            });

            var index = _sounds.IndexOf(sound);
            var reversed = new SoundItem { Name = name, Path = path };
            if (index >= 0) _sounds.Insert(index + 1, reversed);
            else _sounds.Add(reversed);
            Save();
            ShowStatus($"Added \"{name}\".");
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't reverse \"{sound.Name}\": {ex.Message}");
        }
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
            ShowStatus($"Couldn't open \"{sound.Name}\" for editing: {ex.Message}");
            return;
        }

        new ClipEditorWindow(samples, sampleRate, channels, _engine, ClipEditorOptions.Edit(sound.Name),
            (name, path) => AddEditedCopy(sound, name, path)) { Owner = this }.ShowDialog();
    }

    /// <summary>Adds the edited version as a new pad right after the original. The original pad and file are kept.</summary>
    private void AddEditedCopy(SoundItem original, string name, string editedPath)
    {
        var index = _sounds.IndexOf(original);
        var edited = new SoundItem { Name = name, Path = editedPath };
        if (index >= 0) _sounds.Insert(index + 1, edited);
        else _sounds.Add(edited);
        Save();
        ShowStatus($"Added \"{name}\" next to \"{original.Name}\". The original is unchanged.");
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
        // A pad dragged onto this window would just add itself again.
        e.Effects = !_draggingPad && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_draggingPad && e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
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

    private readonly ObservableCollection<ClipAppRow> _clipApps = [];

    /// <summary>Called on the UI thread with the monitor's latest list. Updates rows in place, so ticks don't flicker.</summary>
    private void UpdateClipApps(IReadOnlyList<AudioApp> apps)
    {
        var excluded = new HashSet<string>(_settings.ClipExcludedApps, StringComparer.OrdinalIgnoreCase);
        var capturing = _recorder.CapturedApps.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displayNames = apps.ToDictionary(a => a.Name, a => a.DisplayName, StringComparer.OrdinalIgnoreCase);

        foreach (var row in _clipApps.Where(r => !displayNames.ContainsKey(r.Name)).ToList())
        {
            row.PropertyChanged -= ClipAppRow_Changed;
            _clipApps.Remove(row);
        }

        for (var i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            var row = _clipApps.FirstOrDefault(r => r.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new ClipAppRow { Name = app.Name, IsIncluded = !excluded.Contains(app.Name) };
                row.PropertyChanged += ClipAppRow_Changed;
                _clipApps.Insert(Math.Min(i, _clipApps.Count), row);
            }
            else if (_clipApps.IndexOf(row) is var at && at != i && i < _clipApps.Count)
            {
                _clipApps.Move(at, i);
            }

            row.DisplayName = app.DisplayName;
            row.IsActive = app.IsActive;
            row.IsCapturing = capturing.Contains(app.Name);

            // A process tree capture includes child processes, so an app started by a ticked app can't be left out.
            var host = app.Roots.Select(r => r.InsideApp).FirstOrDefault(outer => outer is not null && !excluded.Contains(outer));
            row.Note = row.IsIncluded || host is null
                ? ""
                : $"Still recorded: {(displayNames.TryGetValue(host, out var hostName) ? hostName : host)} started it";
        }

        ClipAppsEmpty.Visibility = _clipApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_recorder.IsRecording) UpdateRecordingStatus();
    }

    private void ClipAppRow_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ClipAppRow.IsIncluded) || sender is not ClipAppRow row) return;

        var excluded = _settings.ClipExcludedApps;
        excluded.RemoveAll(n => n.Equals(row.Name, StringComparison.OrdinalIgnoreCase));
        if (!row.IsIncluded) excluded.Add(row.Name);
        Save();

        _recorder.SetExcluded(excluded);
        UpdateClipApps(_appMonitor.Current); // Refresh the "still recorded" notes.
    }

    private void UpdateRecordingStatus()
    {
        var apps = _recorder.CapturedApps;
        var kept = $"the last {ClipRecorder.Format(_recorder.BufferLength)} are kept in memory.";
        ClipStatus.Text = apps.Count == 0
            ? "Recording, but no ticked app has audio open yet. They're picked up as soon as they do."
            : $"Recording {JoinNames(apps.Select(a => a.DisplayName).ToList())}: {kept}" +
              (apps.Any(a => a.IsDiscord) ? " Let the call know." : "");
    }

    /// <summary>"Discord", "Discord and Spotify", "Discord, Spotify and 2 more".</summary>
    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "nothing",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        3 => $"{names[0]}, {names[1]} and {names[2]}",
        _ => $"{names[0]}, {names[1]} and {names.Count - 2} more",
    };

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
        SaveClipText.Text = $"Clip {ClipRecorder.Format(length)}…";
    }

    private async void ClipToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingClipToggle) return;

        if (!_settings.ClipConsentAcknowledged)
        {
            var ok = MessageBox.Show(this,
                "The clip recorder keeps the last few seconds or minutes of the ticked apps' audio, which can include other people's voices.\n\n" +
                "• Tell people in the call when it's on, and only clip people who agree. Recording people without consent " +
                "breaks Discord's rules and is illegal in many places.\n" +
                "• Audio stays in memory and is only saved when you save a clip.\n" +
                "• Only the apps you leave ticked are captured, and never your own mic.\n\nTurn it on?",
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
        ClipLength.IsEnabled = false;
        ClipStatus.Text = "Starting…";
        try
        {
            await _recorder.StartAsync(SelectedClipLength, _settings.ClipExcludedApps);
            ClipToggleText.Text = "Stop recording";
            UpdateRecordingStatus();
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
        StopClipRecorder("Stopped. The buffered audio was discarded.");
    }

    private void StopClipRecorder(string message)
    {
        _recorder.Stop();
        SetClipToggle(false);
        ClipLength.IsEnabled = true;
        UpdateClipLengthText();
        ClipToggleText.Text = "Start recording";
        ClipStatus.Text = message;
        RecDot.Visibility = Visibility.Collapsed;
        SaveClipButton.IsEnabled = false;
        Title = AppTitle;
    }

    private void SetClipToggle(bool on)
    {
        _settingClipToggle = true;
        ClipToggle.IsChecked = on;
        _settingClipToggle = false;
    }

    private void SaveClip_Click(object sender, RoutedEventArgs e)
    {
        var appName = _recorder.RecordedApps is { Count: > 0 } apps ? JoinNames(apps.Select(a => a.DisplayName).ToList()) : "recorded";
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
        _appMonitor.Dispose();
        _engine.Dispose();
    }
}
