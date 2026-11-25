using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Soundboard.Audio;

/// <summary>A snapshot of every running process's parent and exe name, taken in one call.</summary>
internal sealed class ProcessTree
{
    private readonly record struct Entry(int ParentId, string ExeName);

    private readonly Dictionary<int, Entry> _entries = new();
    private readonly Dictionary<int, DateTime?> _startTimes = new();

    public static ProcessTree Snapshot()
    {
        var tree = new ProcessTree();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return tree;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return tree;
            do tree._entries[(int)entry.th32ProcessID] = new((int)entry.th32ParentProcessID, entry.szExeFile);
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return tree;
    }

    public bool Contains(int pid) => _entries.ContainsKey(pid);

    /// <summary>The exe name without ".exe", e.g. "Discord".</summary>
    public string? NameOf(int pid) =>
        _entries.TryGetValue(pid, out var e) ? System.IO.Path.GetFileNameWithoutExtension(e.ExeName) : null;

    /// <summary>
    /// Walks up through parents with the same exe name, e.g. from Chrome's audio process to the first
    /// chrome.exe. Capturing that root with its process tree includes all of the app's processes.
    /// </summary>
    public int FindRoot(int pid)
    {
        var current = pid;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!_entries.TryGetValue(current, out var entry)) break;
            var parent = ParentOf(current);
            if (parent is not { } p || !_entries.TryGetValue(p, out var parentEntry)) break;
            if (!string.Equals(parentEntry.ExeName, entry.ExeName, StringComparison.OrdinalIgnoreCase)) break;
            current = p;
        }
        return current;
    }

    /// <summary>The closest ancestor of <paramref name="pid"/> that's in <paramref name="candidates"/>, if any.</summary>
    public int? FindAncestorIn(int pid, IReadOnlySet<int> candidates)
    {
        var visited = new HashSet<int> { pid };
        var current = pid;
        for (var depth = 0; depth < 64; depth++)
        {
            if (ParentOf(current) is not { } parent || !visited.Add(parent)) return null;
            if (candidates.Contains(parent)) return parent;
            current = parent;
        }
        return null;
    }

    /// <summary>
    /// The parent's ID, or null if there isn't a live one. Windows reuses process IDs, so a "parent"
    /// that started after the child is a different process that got the old ID.
    /// </summary>
    private int? ParentOf(int pid)
    {
        if (!_entries.TryGetValue(pid, out var entry)) return null;
        var parent = entry.ParentId;
        if (parent == 0 || parent == pid || !_entries.ContainsKey(parent)) return null;
        var parentStart = StartTime(parent);
        var childStart = StartTime(pid);
        if (parentStart is null || childStart is null || parentStart > childStart) return null;
        return parent;
    }

    private DateTime? StartTime(int pid)
    {
        if (_startTimes.TryGetValue(pid, out var cached)) return cached;
        DateTime? start;
        try
        {
            using var process = Process.GetProcessById(pid);
            start = process.StartTime;
        }
        catch (Exception)
        {
            start = null; // Exited, or not accessible (e.g. a system process).
        }
        _startTimes[pid] = start;
        return start;
    }

    // ---- Win32 ----

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
