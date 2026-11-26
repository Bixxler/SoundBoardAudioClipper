using System.IO;
using System.Text;
using Concentus;
using Concentus.Oggfile;
using NAudio.Vorbis;
using NAudio.Wave;

namespace Soundboard.Audio;

/// <summary>An open audio file: its samples, its length, and what to dispose when done with it.</summary>
public sealed record OpenedAudio(ISampleProvider Samples, TimeSpan TotalTime, IDisposable Owner) : IDisposable
{
    public void Dispose() => Owner.Dispose();
}

/// <summary>
/// Opens every audio format the app accepts. NAudio handles mp3, wav, aiff, wma and m4a; Ogg files are
/// either Vorbis (most .ogg files) or Opus (Discord voice messages, .opus files), which need their own
/// decoders, so the file's header decides.
/// </summary>
public static class AudioFiles
{
    public static readonly string[] Extensions = [".mp3", ".wav", ".aiff", ".aif", ".wma", ".m4a", ".ogg", ".oga", ".opus"];

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static OpenedAudio Open(string path)
    {
        switch (DetectOgg(path))
        {
            case OggCodec.Vorbis:
                var vorbis = new VorbisWaveReader(path);
                return new OpenedAudio(vorbis, vorbis.TotalTime, vorbis);
            case OggCodec.Opus:
                var opus = new OpusFileReader(path);
                return new OpenedAudio(opus, opus.TotalTime, opus);
            case OggCodec.Other:
                throw new InvalidDataException("This Ogg file uses a codec the app can't play. Vorbis and Opus are supported.");
            default:
                var reader = new AudioFileReader(path);
                return new OpenedAudio(reader, reader.TotalTime, reader);
        }
    }

    private enum OggCodec { NotOgg, Vorbis, Opus, Other }

    /// <summary>Looks at the first Ogg page, which holds the codec's identification header.</summary>
    private static OggCodec DetectOgg(string path)
    {
        var header = new byte[512];
        int read;
        using (var file = File.OpenRead(path)) read = file.Read(header, 0, header.Length);
        if (read < 4 || Encoding.ASCII.GetString(header, 0, 4) != "OggS") return OggCodec.NotOgg;

        var text = Encoding.ASCII.GetString(header, 0, read);
        if (text.Contains("OpusHead")) return OggCodec.Opus;
        if (text.Contains("\u0001vorbis")) return OggCodec.Vorbis;
        return OggCodec.Other;
    }

    /// <summary>
    /// Decodes an Ogg Opus file packet by packet, so long files don't have to be decoded into memory first.
    /// Opus always decodes at 48 kHz.
    /// </summary>
    private sealed class OpusFileReader : ISampleProvider, IDisposable
    {
        private const int SampleRate = 48000;
        private readonly FileStream _file;
        private readonly OpusOggReadStream _ogg;
        private short[]? _packet;
        private int _packetPosition;

        public OpusFileReader(string path)
        {
            var channels = ReadChannelCount(path);
            _file = File.OpenRead(path);
            try
            {
                _ogg = new OpusOggReadStream(OpusCodecFactory.CreateDecoder(SampleRate, channels), _file);
            }
            catch
            {
                _file.Dispose();
                throw;
            }
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);
            TotalTime = _ogg.TotalTime;
        }

        public WaveFormat WaveFormat { get; }
        public TimeSpan TotalTime { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                if (_packet is null || _packetPosition >= _packet.Length)
                {
                    if (!_ogg.HasNextPacket) break;
                    _packet = _ogg.DecodeNextPacket();
                    _packetPosition = 0;
                    if (_packet is null) continue; // Header or empty packet.
                }
                var n = Math.Min(count - written, _packet.Length - _packetPosition);
                for (var i = 0; i < n; i++) buffer[offset + written + i] = _packet[_packetPosition + i] / 32768f;
                _packetPosition += n;
                written += n;
            }
            return written;
        }

        /// <summary>
        /// The channel count from the OpusHead packet, which starts the first Ogg page: 27 header bytes, then
        /// the segment table, then "OpusHead", a version byte and the channel count.
        /// </summary>
        private static int ReadChannelCount(string path)
        {
            var page = new byte[512];
            int read;
            using (var file = File.OpenRead(path)) read = file.Read(page, 0, page.Length);
            if (read < 28) throw new InvalidDataException("The Opus file is too short.");
            var packetStart = 27 + page[26];
            if (read < packetStart + 10 || Encoding.ASCII.GetString(page, packetStart, 8) != "OpusHead")
                throw new InvalidDataException("The Opus file's header is missing or damaged.");
            var channels = page[packetStart + 9];
            if (channels is < 1 or > 2)
                throw new InvalidDataException($"Opus files with {channels} channels aren't supported, only mono and stereo.");
            return channels;
        }

        public void Dispose()
        {
            _ogg.Close();
            _file.Dispose();
        }
    }
}
