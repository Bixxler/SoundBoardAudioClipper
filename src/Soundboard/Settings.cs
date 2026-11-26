using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Soundboard;

/// <summary>A sound pad. It raises change notifications so a rename or trim updates its button straight away.</summary>
public sealed class SoundItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _path = "";

    public required string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public required string Path
    {
        get => _path;
        set => Set(ref _path, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? property = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

public sealed class AppSettings
{
    public string? MonitorDeviceId { get; set; }
    public string? VirtualMicDeviceId { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public float MonitorVolume { get; set; } = 1f;
    public float VirtualMicVolume { get; set; } = 1f;

    /// <summary>Boost for the mic passthrough, in dB (0 to 24).</summary>
    public double MicBoostDb { get; set; }
    public bool FirstRun { get; set; } = true;
    public bool ClipConsentAcknowledged { get; set; }

    /// <summary>
    /// Exe names (without .exe) the clip recorder leaves out. Every other app with audio is recorded.
    /// Soundboard itself is left out by default, so clips don't pick up your own pads.
    /// </summary>
    public List<string> ClipExcludedApps { get; set; } = ["Soundboard"];

    /// <summary>How many seconds of audio the clip recorder keeps. Should be one of <c>ClipRecorder.BufferLengths</c>.</summary>
    public int ClipBufferSeconds { get; set; } = 30;
    public List<SoundItem> Sounds { get; set; } = [];

    private static readonly string AppFolder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Soundboard");

    private static readonly string FilePath = System.IO.Path.Combine(AppFolder, "settings.json");

    /// <summary>Where the app stores clips it creates.</summary>
    public static readonly string ClipsFolder = System.IO.Path.Combine(AppFolder, "Clips");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (JsonException)
        {
            // Unreadable settings: start fresh rather than refusing to open.
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}
