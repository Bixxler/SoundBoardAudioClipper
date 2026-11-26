namespace Soundboard.Audio;

/// <summary>A stretch of a clip, in seconds from its start.</summary>
public readonly record struct TimeRange(double Start, double End);

/// <summary>
/// Finds the parts of a clip that sound like someone talking or singing. A short frame counts as voice when
/// it is clearly louder than the clip's background noise in the voice band (200–3500 Hz) and has a pitch
/// (voices are periodic, hiss and most noise aren't). Nearby voice frames are then joined into phrases.
/// It's a heuristic, not speech recognition: humming, whistling and tonal music can count as voice too.
/// </summary>
public static class VoiceDetector
{
    private const double HopSeconds = 0.02;
    private const double WindowSeconds = 0.032;
    private const double MinPitchHz = 75, MaxPitchHz = 400;
    private const double Periodicity = 0.5;        // Normalized autocorrelation needed for a "pitched" frame.
    private const double AboveNoiseDb = 8;         // How far above the background a frame must be.
    private const double QuietestDb = -55;         // Never count anything quieter than this.
    private const double JoinGapSeconds = 0.3;     // Pauses between words shorter than this are bridged.
    private const double MinBlipSeconds = 0.06;    // Single voiced frames shorter than this are ignored…
    private const double MinPhraseSeconds = 0.15;  // …and so are phrases shorter than this after joining.
    private const double PadSeconds = 0.06;        // Extended a little so consonants at the edges are kept.

    public static IReadOnlyList<TimeRange> Detect(float[] samples, int sampleRate, int channels, CancellationToken cancel = default)
    {
        if (samples.Length == 0 || channels < 1 || sampleRate < 8000) return [];

        // Mono, band-limited to the voice range, then decimated to about 16 kHz to keep it fast.
        var step = Math.Max(1, sampleRate / 16000);
        double rate = sampleRate / (double)step;
        var frames = samples.Length / channels;
        var x = new float[frames / step];
        var highPass = Biquad.HighPass(sampleRate, 200);
        var lowPass = Biquad.LowPass(sampleRate, 3500);
        for (var f = 0; f < frames; f++)
        {
            double mono = 0;
            for (var c = 0; c < channels; c++) mono += samples[f * channels + c];
            var y = lowPass.Process(highPass.Process(mono / channels));
            if (f % step == 0 && f / step < x.Length) x[f / step] = (float)y;
        }

        var hop = (int)(HopSeconds * rate);
        var window = (int)(WindowSeconds * rate);
        var count = x.Length < window ? 0 : (x.Length - window) / hop + 1;
        if (count == 0) return [];

        var levelDb = new double[count];
        for (var i = 0; i < count; i++)
        {
            double sum = 0;
            for (int j = i * hop, end = j + window; j < end; j++) sum += x[j] * x[j];
            levelDb[i] = 10 * Math.Log10(sum / window + 1e-12);
        }
        var sorted = (double[])levelDb.Clone();
        Array.Sort(sorted);
        var noiseDb = sorted[(int)(sorted.Length * 0.15)];
        // A clip that's loud all the way through has no quiet background to measure, so the threshold is
        // also kept well under its loud parts; the pitch test then decides.
        var loudDb = sorted[(int)(sorted.Length * 0.95)];
        var thresholdDb = Math.Max(Math.Min(noiseDb + AboveNoiseDb, loudDb - 12), QuietestDb);

        var minLag = (int)(rate / MaxPitchHz);
        var maxLag = Math.Min((int)(rate / MinPitchHz), window / 2);
        var voiced = new bool[count];
        for (var i = 0; i < count; i++)
        {
            if ((i & 63) == 0) cancel.ThrowIfCancellationRequested();
            if (levelDb[i] < thresholdDb) continue;
            voiced[i] = Pitched(x, i * hop, window, minLag, maxLag);
        }

        return Smooth(voiced, HopSeconds, count * HopSeconds + (WindowSeconds - HopSeconds));
    }

    /// <summary>Whether the frame repeats itself at some lag in the voice pitch range.</summary>
    private static bool Pitched(float[] x, int start, int window, int minLag, int maxLag)
    {
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = start, end = start + window - lag; i < end; i++)
            {
                double a = x[i], b = x[i + lag];
                xy += a * b; xx += a * a; yy += b * b;
            }
            if (xy > 0 && xy / Math.Sqrt(xx * yy + 1e-20) >= Periodicity) return true;
        }
        return false;
    }

    /// <summary>Turns per-frame flags into phrases: drops blips, bridges short pauses, pads the edges.</summary>
    private static List<TimeRange> Smooth(bool[] voiced, double hop, double duration)
    {
        var runs = new List<TimeRange>();
        for (var i = 0; i < voiced.Length;)
        {
            if (!voiced[i]) { i++; continue; }
            var start = i;
            while (i < voiced.Length && voiced[i]) i++;
            if ((i - start) * hop >= MinBlipSeconds) runs.Add(new TimeRange(start * hop, i * hop));
        }

        var joined = new List<TimeRange>();
        foreach (var run in runs)
        {
            if (joined.Count > 0 && run.Start - joined[^1].End <= JoinGapSeconds)
                joined[^1] = joined[^1] with { End = run.End };
            else
                joined.Add(run);
        }

        var result = new List<TimeRange>();
        foreach (var phrase in joined)
        {
            if (phrase.End - phrase.Start < MinPhraseSeconds) continue;
            var padded = new TimeRange(Math.Max(0, phrase.Start - PadSeconds), Math.Min(duration, phrase.End + PadSeconds));
            if (result.Count > 0 && padded.Start <= result[^1].End) result[^1] = result[^1] with { End = padded.End };
            else result.Add(padded);
        }
        return result;
    }

    /// <summary>RBJ cookbook second-order filter.</summary>
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1, _x2, _y1, _y2;

        public static Biquad HighPass(double rate, double hz) => Make(rate, hz, highPass: true);
        public static Biquad LowPass(double rate, double hz) => Make(rate, hz, highPass: false);

        private static Biquad Make(double rate, double hz, bool highPass)
        {
            var w0 = 2 * Math.PI * hz / rate;
            var cos = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5)); // Q = 0.707
            var a0 = 1 + alpha;
            var (b0, b1, b2) = highPass
                ? ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2)
                : ((1 - cos) / 2, 1 - cos, (1 - cos) / 2);
            return new Biquad(b0 / a0, b1 / a0, b2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        public double Process(double x)
        {
            var y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
            return y;
        }
    }
}
