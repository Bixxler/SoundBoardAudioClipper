using System.IO;

namespace Soundboard;

/// <summary>Turns pad names into file names, and picks names that don't overwrite anything.</summary>
public static class FileNames
{
    /// <summary>The name with characters Windows doesn't allow in file names replaced by "_".</summary>
    public static string Safe(string name, string fallback = "Clip")
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    /// <summary>"folder\name.mp3", or "folder\name (2).mp3" and so on if that's taken.</summary>
    public static string Unique(string folder, string baseName, string extension)
    {
        var path = Path.Combine(folder, baseName + extension);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName} ({i}){extension}");
        return path;
    }
}
