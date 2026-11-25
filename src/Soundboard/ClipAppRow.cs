using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Soundboard;

/// <summary>One app in the clip recorder's checklist. Ticked means its audio is recorded.</summary>
public sealed class ClipAppRow : INotifyPropertyChanged
{
    private string _displayName = "";
    private bool _isIncluded;
    private bool _isActive;
    private bool _isCapturing;
    private string _note = "";

    /// <summary>The exe name, which exclusions are saved by.</summary>
    public required string Name { get; init; }

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

    public bool IsIncluded { get => _isIncluded; set => Set(ref _isIncluded, value); }

    /// <summary>The app has an audio stream open right now.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    /// <summary>The recorder is capturing this app right now.</summary>
    public bool IsCapturing { get => _isCapturing; set => Set(ref _isCapturing, value); }

    /// <summary>A caveat shown under the name, e.g. that it can't be left out. Empty when there's none.</summary>
    public string Note
    {
        get => _note;
        set { if (Set(ref _note, value)) OnPropertyChanged(nameof(NoteVisibility)); }
    }

    public Visibility NoteVisibility => _note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property!);
        return true;
    }
}
