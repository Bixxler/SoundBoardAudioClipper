using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Soundboard.Audio;

/// <summary>
/// Captures the audio that one process and its child processes play, using Windows' process
/// loopback (Windows 10 2004 and later). No other app's sound is included.
/// Output: 48 kHz stereo float samples, with silence filled in while the process is quiet,
/// so the timeline always matches real time.
/// </summary>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private static readonly WaveFormat CaptureFormat = new(SampleRate, 16, Channels);

    private readonly AudioClient _client;
    private readonly AudioCaptureClient _captureClient;
    private readonly AutoResetEvent _dataReady = new(false);
    private Thread? _thread;
    private volatile bool _stopping;

    /// <summary>Raised on the capture thread. The buffer is reused, so copy the samples before returning.</summary>
    public event Action<float[], int>? DataAvailable;

    /// <summary>Raised on the capture thread if capture fails.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The root of the process tree being captured.</summary>
    public int ProcessId { get; }

    private ProcessLoopbackCapture(AudioClient client, int processId)
    {
        ProcessId = processId;
        _client = client;
        _client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
            bufferDuration: 2_000_000, // 200 ms, in 100 ns units
            periodicity: 0,
            CaptureFormat,
            Guid.Empty);
        _client.SetEventHandle(_dataReady.SafeWaitHandle.DangerousGetHandle());
        _captureClient = _client.AudioCaptureClient;
    }

    public static async Task<ProcessLoopbackCapture> CreateAsync(int processId)
    {
        var client = await ActivateAsync(processId);
        // The client was created on a worker (MTA) thread. Initialize it on one too, not the UI thread.
        try { return await Task.Run(() => new ProcessLoopbackCapture(client, processId)); }
        catch { client.Dispose(); throw; }
    }

    public void Start()
    {
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Process loopback capture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void CaptureLoop()
    {
        var shorts = new short[SampleRate * Channels];
        var floats = new float[SampleRate * Channels];
        var clock = Stopwatch.StartNew();
        long framesOut = 0;

        try
        {
            _client.Start();
            while (!_stopping)
            {
                _dataReady.WaitOne(50);

                while (_captureClient.GetNextPacketSize() > 0)
                {
                    var data = _captureClient.GetBuffer(out var frames, out var flags);
                    var samples = frames * Channels;
                    if (samples > floats.Length)
                    {
                        shorts = new short[samples];
                        floats = new float[samples];
                    }

                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                    {
                        Array.Clear(floats, 0, samples);
                    }
                    else
                    {
                        Marshal.Copy(data, shorts, 0, samples);
                        for (var i = 0; i < samples; i++) floats[i] = shorts[i] / 32768f;
                    }
                    _captureClient.ReleaseBuffer(frames);

                    DataAvailable?.Invoke(floats, samples);
                    framesOut += frames;
                }

                // Process loopback sends no packets while the app is silent. Fill the gap with
                // silence so "the last 30 seconds" really means the last 30 seconds. Keep 50 ms
                // of slack, so packets that are only running late don't get duplicated.
                var expected = (long)(clock.Elapsed.TotalSeconds * SampleRate);
                var behind = expected - framesOut;
                if (behind > SampleRate / 5)
                {
                    var pad = (int)Math.Min(behind - SampleRate / 20, floats.Length / Channels);
                    Array.Clear(floats, 0, pad * Channels);
                    DataAvailable?.Invoke(floats, pad * Channels);
                    framesOut += pad;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stopping) Failed?.Invoke(ex);
        }
        finally
        {
            try { _client.Stop(); } catch (COMException) { }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _dataReady.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _captureClient.Dispose();
        _client.Dispose();
        _dataReady.Dispose();
    }

    // ---- Activation: ActivateAudioInterfaceAsync with process-loopback parameters ----

    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly Guid IAudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    private static async Task<AudioClient> ActivateAsync(int processId)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1,            // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0,       // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob { vt = 65 /* VT_BLOB */, cbSize = (uint)paramsSize, pBlobData = paramsPtr }, variantPtr, false);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(ProcessLoopbackDevice, IAudioClientId, variantPtr, handler, out _);
            return await handler.Task;
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>A PROPVARIANT that holds a BLOB: vt, 3 reserved words, then {cbSize, pBlobData}.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    /// <summary>Windows calls this back on a worker thread, so it must be agile.</summary>
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<AudioClient> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AudioClient> Task => _done.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out var hr, out var result);
                if (hr < 0) _done.SetException(Marshal.GetExceptionForHR(hr)!);
                else _done.SetResult(new AudioClient((IAudioClient)result));
            }
            catch (Exception ex)
            {
                _done.TrySetException(ex);
            }
        }
    }
}
