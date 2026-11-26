namespace Soundboard.Audio;

/// <summary>Effect amounts. Every one at its default leaves the audio unchanged.</summary>
public sealed record EffectSettings(
    double VolumeDb = 0,       // -12..+12
    double BassDb = 0,         // 0..18, low shelf at 120 Hz
    double PitchSemitones = 0, // -12..+12, tape style: higher is also faster and shorter
    double Reverb = 0,         // 0..1 wet amount
    double Echo = 0,           // 0..1 echo level
    double Distortion = 0)     // 0..1 drive
{
    public static readonly EffectSettings None = new();

    public bool IsNone => this == None;

    /// <summary>Playback speed from the pitch change: +12 semitones is twice as fast.</summary>
    public double Speed => Math.Pow(2, PitchSemitones / 12);

    /// <summary>Extra time added after the clip so reverb and echo can ring out.</summary>
    public double TailSeconds => Math.Max(Reverb > 0 ? Effects.ReverbTailSeconds : 0, Echo > 0 ? Effects.EchoTailSeconds : 0);

    /// <summary>How long a clip of <paramref name="seconds"/> will be once the effects are applied.</summary>
    public double OutputSeconds(double seconds) => seconds / Speed + TailSeconds;
}

/// <summary>
/// Simple offline effects for short clips: everything runs on a whole buffer at once. Order: pitch,
/// distortion, bass, echo, reverb, volume, then a limiter so boosts can't clip.
/// </summary>
public static class Effects
{
    public const double ReverbTailSeconds = 1.5;
    public const double EchoTailSeconds = 1.2;

    /// <summary>Returns a new interleaved buffer with the effects applied. The input isn't changed.</summary>
    public static float[] Apply(float[] input, int sampleRate, int channels, EffectSettings fx)
    {
        if (fx.IsNone || input.Length == 0) return input;

        var samples = fx.PitchSemitones != 0 ? Resample(input, channels, fx.Speed) : (float[])input.Clone();

        var tailFrames = (int)(fx.TailSeconds * sampleRate);
        if (tailFrames > 0) Array.Resize(ref samples, samples.Length + tailFrames * channels);

        if (fx.Distortion > 0) Distort(samples, fx.Distortion);
        if (fx.BassDb > 0) BassBoost(samples, sampleRate, channels, fx.BassDb);
        if (fx.Echo > 0) Echo(samples, sampleRate, channels, fx.Echo);
        if (fx.Reverb > 0) Reverb(samples, sampleRate, channels, fx.Reverb);
        if (fx.VolumeDb != 0) Gain(samples, (float)Math.Pow(10, fx.VolumeDb / 20));
        Limit(samples);
        return samples;
    }

    /// <summary>Tape-style speed change with linear interpolation: faster means higher and shorter.</summary>
    private static float[] Resample(float[] input, int channels, double speed)
    {
        var inFrames = input.Length / channels;
        var outFrames = (int)(inFrames / speed);
        var output = new float[outFrames * channels];
        for (var i = 0; i < outFrames; i++)
        {
            var position = i * speed;
            var a = (int)position;
            var b = Math.Min(a + 1, inFrames - 1);
            var t = (float)(position - a);
            for (var c = 0; c < channels; c++)
                output[i * channels + c] = input[a * channels + c] * (1 - t) + input[b * channels + c] * t;
        }
        return output;
    }

    /// <summary>Soft clipping. The drive is matched by a gain cut, so loudness stays roughly the same.</summary>
    private static void Distort(float[] samples, double amount)
    {
        var drive = 1 + amount * 24;
        var makeup = 1 / Math.Tanh(drive * 0.5);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(Math.Tanh(samples[i] * drive) * makeup * 0.5);
    }

    /// <summary>Low shelf at 120 Hz (RBJ cookbook biquad), boosting everything below it.</summary>
    private static void BassBoost(float[] samples, int sampleRate, int channels, double gainDb)
    {
        var a = Math.Pow(10, gainDb / 40);
        var w0 = 2 * Math.PI * 120 / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / 2 * Math.Sqrt(2); // Shelf slope 1.
        var sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;

        var b0 = a * ((a + 1) - (a - 1) * cos + sqrtA2Alpha);
        var b1 = 2 * a * ((a - 1) - (a + 1) * cos);
        var b2 = a * ((a + 1) - (a - 1) * cos - sqrtA2Alpha);
        var a0 = (a + 1) + (a - 1) * cos + sqrtA2Alpha;
        var a1 = -2 * ((a - 1) + (a + 1) * cos);
        var a2 = (a + 1) + (a - 1) * cos - sqrtA2Alpha;
        (b0, b1, b2, a1, a2) = (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);

        for (var c = 0; c < channels; c++)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (var i = c; i < samples.Length; i += channels)
            {
                double x = samples[i];
                var y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                (x2, x1, y2, y1) = (x1, x, y1, y);
                samples[i] = (float)y;
            }
        }
    }

    /// <summary>A 260 ms echo that repeats and fades. The amount sets how loud the echoes are.</summary>
    private static void Echo(float[] samples, int sampleRate, int channels, double amount)
    {
        var delay = (int)(0.26 * sampleRate) * channels;
        const float feedback = 0.45f;
        var level = (float)(amount * 0.8);
        var dry = (float[])samples.Clone();
        // line[i] = dry[i - d] + feedback * line[i - d]: echoes at d, 2d, 3d…, each quieter than the last.
        var line = new float[samples.Length];
        for (var i = delay; i < samples.Length; i++)
        {
            line[i] = dry[i - delay] + line[i - delay] * feedback;
            samples[i] = dry[i] + line[i] * level;
        }
    }

    /// <summary>
    /// A small Freeverb-style room: four damped comb filters into two all-pass filters per channel, with
    /// slightly different delays on the right for width.
    /// </summary>
    private static void Reverb(float[] samples, int sampleRate, int channels, double amount)
    {
        int[] combTuning = [1116, 1188, 1277, 1356];
        int[] allpassTuning = [556, 441];
        const double roomSize = 0.84, damping = 0.2;
        var scale = sampleRate / 44100.0;
        var wet = (float)(amount * 0.9);
        var frames = samples.Length / channels;

        for (var c = 0; c < channels; c++)
        {
            var spread = c == 0 ? 0 : 23;
            var combs = combTuning.Select(t => new Comb((int)((t + spread) * scale), roomSize, damping)).ToArray();
            var allpasses = allpassTuning.Select(t => new Allpass((int)((t + spread) * scale))).ToArray();
            for (var f = 0; f < frames; f++)
            {
                var i = f * channels + c;
                var input = samples[i] * 0.03f;
                var output = 0f;
                foreach (var comb in combs) output += comb.Process(input);
                foreach (var allpass in allpasses) output = allpass.Process(output);
                samples[i] += output * wet * 7;
            }
        }
    }

    private static void Gain(float[] samples, float gain)
    {
        for (var i = 0; i < samples.Length; i++) samples[i] *= gain;
    }

    /// <summary>If anything would clip, turns the whole clip down so its loudest point sits just under full scale.</summary>
    private static void Limit(float[] samples)
    {
        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        if (peak > 0.98f) Gain(samples, 0.98f / peak);
    }

    private sealed class Comb(int size, double feedback, double damping)
    {
        private readonly float[] _buffer = new float[Math.Max(1, size)];
        private int _index;
        private float _store;

        public float Process(float input)
        {
            var output = _buffer[_index];
            _store = (float)(output * (1 - damping) + _store * damping);
            _buffer[_index] = input + (float)(_store * feedback);
            if (++_index == _buffer.Length) _index = 0;
            return output;
        }
    }

    private sealed class Allpass(int size)
    {
        private readonly float[] _buffer = new float[Math.Max(1, size)];
        private int _index;

        public float Process(float input)
        {
            var buffered = _buffer[_index];
            var output = buffered - input;
            _buffer[_index] = input + buffered * 0.5f;
            if (++_index == _buffer.Length) _index = 0;
            return output;
        }
    }
}
