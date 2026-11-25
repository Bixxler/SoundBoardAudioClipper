using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Soundboard.Audio;

/// <summary>
/// An app the clip recorder can capture, found from one of its open windows. Process loopback
/// captures a whole process tree, so this points at the app's root process: for apps like
/// Discord or Chrome, the audio is played by a child process, not the one that owns the window.
/// </summary>
public sealed record AudioSource(int ProcessId, string ProcessName, string WindowTitle)
{
    private static readonly string[] DiscordProcessNames = ["Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment"];

    public bool IsDiscord => DiscordProcessNames.Contains(ProcessName, StringComparer.OrdinalIgnoreCase);

    /// <summary>A short name for status messages, e.g. "Discord" or "chrome".</summary>
    public string AppName => IsDiscord ? "Discord" : ProcessName;

    public override string ToString() =>
        string.Equals(WindowTitle, ProcessName, StringComparison.OrdinalIgnoreCase) ? WindowTitle : $"{WindowTitle}  ({ProcessName})";

    /// <summary>
    /// One entry per app that has a visible window, in z-order (front-most first). The title is that
    /// app's front-most window. This app's own windows are left out.
    /// </summary>
    public static List<AudioSource> ListWindows()
    {
        var processes = ProcessTable.Snapshot();
        var ownPid = Environment.ProcessId;
        var sources = new List<AudioSource>();
        var seenRoots = new HashSet<int>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidate(hwnd)) return true;
            var title = GetTitle(hwnd);
            if (title.Length == 0) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            pid = ResolveFrameHost(hwnd, (int)pid, processes);
            if (pid == ownPid || pid == 0) return true;
            if (!processes.TryGetValue((int)pid, out var entry)) return true;

            var root = processes.FindRoot((int)pid);
            if (root == ownPid || !seenRoots.Add(root)) return true;
            sources.Add(new AudioSource(root, Path.GetFileNameWithoutExtension(entry.ExeName), title));
            return true;
        }, IntPtr.Zero);

        return sources;
    }

    /// <summary>The running Discord desktop app, if any, so the recorder can default to it.</summary>
    public static AudioSource? FindDiscord()
    {
        var processes = ProcessTable.Snapshot();
        foreach (var (pid, entry) in processes)
        {
            var name = Path.GetFileNameWithoutExtension(entry.ExeName);
            if (!DiscordProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var root = processes.FindRoot(pid);
            return new AudioSource(root, name, "Discord");
        }
        return null;
    }

    // ---- Window filtering ----

    private static bool IsCandidate(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false; // Dialogs and popups belong to another window.
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;
        // Suspended Store apps and windows on other virtual desktops are "cloaked": visible, but not shown.
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return true;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length == 0) return "";
        var text = new StringBuilder(length + 1);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString().Trim();
    }

    /// <summary>
    /// Store apps' windows are owned by ApplicationFrameHost, which plays no audio. The real app
    /// owns a child window inside the frame.
    /// </summary>
    private static uint ResolveFrameHost(IntPtr hwnd, int pid, ProcessTable processes)
    {
        if (!processes.TryGetValue(pid, out var entry) ||
            !string.Equals(entry.ExeName, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return (uint)pid;

        uint appPid = 0;
        EnumChildWindows(hwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childPid);
            if (childPid != pid) { appPid = childPid; return false; }
            return true;
        }, IntPtr.Zero);
        return appPid; // 0 if the app is suspended: skip it.
    }

    // ---- Process tree ----

    private readonly record struct ProcessEntry(int ParentId, string ExeName);

    private sealed class ProcessTable : Dictionary<int, ProcessEntry>
    {
        public static ProcessTable Snapshot()
        {
            var table = new ProcessTable();
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE) return table;
            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (!Process32FirstW(snapshot, ref entry)) return table;
                do table[(int)entry.th32ProcessID] = new((int)entry.th32ParentProcessID, entry.szExeFile);
                while (Process32NextW(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return table;
        }

        /// <summary>
        /// Walks up through parents with the same exe name, e.g. from a chrome.exe window process to
        /// the first chrome.exe. Start times guard against a parent ID that was reused by a newer process.
        /// </summary>
        public int FindRoot(int pid)
        {
            var current = pid;
            for (var depth = 0; depth < 32; depth++)
            {
                if (!TryGetValue(current, out var entry)) break;
                var parent = entry.ParentId;
                if (parent == 0 || parent == current) break;
                if (!TryGetValue(parent, out var parentEntry)) break;
                if (!string.Equals(parentEntry.ExeName, entry.ExeName, StringComparison.OrdinalIgnoreCase)) break;
                if (!StartedBefore(parent, current)) break;
                current = parent;
            }
            return current;
        }

        private static bool StartedBefore(int parent, int child)
        {
            try
            {
                using var p = Process.GetProcessById(parent);
                using var c = Process.GetProcessById(child);
                return p.StartTime <= c.StartTime;
            }
            catch (Exception)
            {
                return false; // Exited, or not accessible.
            }
        }
    }

    // ---- Win32 ----

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

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
