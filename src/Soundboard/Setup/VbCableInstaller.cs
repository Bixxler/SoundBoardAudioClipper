using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace Soundboard.Setup;

/// <summary>
/// Downloads the official VB-Cable driver pack, checks the setup program's signature and
/// runs it as administrator. The installer is fetched from vb-audio.com when needed rather
/// than bundled, because VB-Cable is VB-Audio's donationware.
/// </summary>
public static class VbCableInstaller
{
    public const string DownloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";
    public const string ProductPage = "https://vb-audio.com/Cable/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public sealed class PreparedSetup(string setupPath, string signer, string workDir) : IDisposable
    {
        public string SetupPath { get; } = setupPath;
        public string Signer { get; } = signer;

        /// <summary>True when the signing certificate names VB-Audio's author, Vincent Burel.</summary>
        public bool SignerLooksOfficial =>
            Signer.Contains("Burel", StringComparison.OrdinalIgnoreCase) ||
            Signer.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

        public void Dispose() => TryDelete(workDir);
    }

    /// <summary>Downloads and extracts the pack into a fresh temp folder, then verifies the setup program.</summary>
    public static async Task<PreparedSetup> PrepareAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "Soundboard-VBCable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            progress.Report("Downloading VB-Cable from vb-audio.com…");
            var zip = Path.Combine(work, "VBCABLE_Driver_Pack.zip");
            using (var response = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zip);
                await response.Content.CopyToAsync(file, ct);
            }

            progress.Report("Extracting…");
            var extracted = Path.Combine(work, "pack");
            ZipFile.ExtractToDirectory(zip, extracted); // Throws on entries that would escape the folder.

            var setup = FindSetup(extracted) ?? throw new FileNotFoundException(
                $"The download has no VB-Cable setup program for this PC ({RuntimeInformation.OSArchitecture}).");

            progress.Report("Checking the installer's digital signature…");
            var signer = Authenticode.VerifyAndGetSigner(setup);
            return new PreparedSetup(setup, signer, work);
        }
        catch
        {
            TryDelete(work);
            throw;
        }
    }

    /// <summary>
    /// Runs the setup program elevated and waits for it to close. Returns false if the user
    /// declined the Windows permission prompt.
    /// </summary>
    public static async Task<bool> RunAsync(PreparedSetup setup)
    {
        var start = new ProcessStartInfo(setup.SetupPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(setup.SetupPath),
        };
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The installer didn't start.");
            await process.WaitForExitAsync();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the UAC prompt was declined
        {
            return false;
        }
    }

    public static void OpenProductPage() =>
        Process.Start(new ProcessStartInfo(ProductPage) { UseShellExecute = true });

    private static string? FindSetup(string folder)
    {
        var candidates = Directory.GetFiles(folder, "VBCABLE_Setup*.exe", SearchOption.AllDirectories);
        string Name(string p) => Path.GetFileNameWithoutExtension(p);

        return RuntimeInformation.OSArchitecture switch
        {
            // Pack46 has no separate ARM64 setup program: the x64 setup (run under emulation)
            // installs the bundled vbaudio_cable64arm driver on ARM64 Windows.
            Architecture.X64 or Architecture.Arm64 =>
                candidates.FirstOrDefault(p => Name(p).EndsWith("_x64", StringComparison.OrdinalIgnoreCase)),
            _ => candidates.FirstOrDefault(p => Name(p).Equals("VBCABLE_Setup", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
