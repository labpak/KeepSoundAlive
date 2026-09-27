namespace KeepSoundAlive.Audio;

public enum SignalMode
{
    /// <summary>Low-frequency sine below the range of human hearing.</summary>
    Infrasound,
    /// <summary>High-frequency sine above the range of adult hearing.</summary>
    Ultrasound,
    /// <summary>Very quiet white noise.</summary>
    Noise,
    /// <summary>Digital zeros: keeps the audio stream open without any signal.</summary>
    Silence,
}

public sealed record SignalSettings(SignalMode Mode, double FrequencyHz, double LevelDb)
{
    public static SignalSettings Default { get; } = new(SignalMode.Infrasound, 20, -40);

    public const double InfrasoundMinHz = 1;
    public const double InfrasoundMaxHz = 30;
    public const double InfrasoundDefaultHz = 20;

    public const double UltrasoundMinHz = 16000;
    public const double UltrasoundMaxHz = 23000;
    public const double UltrasoundDefaultHz = 21000;

    public const double MinLevelDb = -90;
    public const double MaxLevelDb = -10;

    public static double DefaultFrequency(SignalMode mode) => mode switch
    {
        SignalMode.Infrasound => InfrasoundDefaultHz,
        SignalMode.Ultrasound => UltrasoundDefaultHz,
        _ => 0,
    };

    public static double DefaultLevel(SignalMode mode) => mode switch
    {
        SignalMode.Noise => -70,
        _ => -40,
    };

    public SignalSettings Normalized()
    {
        var freq = Mode switch
        {
            SignalMode.Infrasound => Math.Clamp(FrequencyHz, InfrasoundMinHz, InfrasoundMaxHz),
            SignalMode.Ultrasound => Math.Clamp(FrequencyHz, UltrasoundMinHz, UltrasoundMaxHz),
            _ => FrequencyHz,
        };
        return this with { FrequencyHz = freq, LevelDb = Math.Clamp(LevelDb, MinLevelDb, MaxLevelDb) };
    }
}

/// <summary>Shared, thread-safe holder of the current signal settings. Outputs read it on every buffer.</summary>
public sealed class SignalSource
{
    private SignalSettings _settings = SignalSettings.Default;

    public SignalSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set => Volatile.Write(ref _settings, value.Normalized());
    }
}

/// <summary>
/// Generates the keep-alive signal for one output stream. Amplitude changes (including start-up)
/// are smoothed so that switching modes or levels never produces an audible click.
/// </summary>
public sealed class SignalGenerator
{
    private const double FadeSeconds = 0.25;

    private readonly SignalSource _source;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly double _smoothing;

    private double _phase;
    private double _amplitude;
    private uint _noiseState = 0x12345678;

    public SignalGenerator(SignalSource source, int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        _source = source;
        _sampleRate = sampleRate;
        _channels = channels;
        _smoothing = 1 - Math.Exp(-1.0 / (FadeSeconds / 5 * sampleRate));
    }

    public int SampleRate => _sampleRate;
    public int Channels => _channels;

    /// <summary>Fills an interleaved float buffer. Its length should be a multiple of the channel count.</summary>
    public void Fill(Span<float> buffer)
    {
        var settings = _source.Settings;
        var mode = settings.Mode;
        var target = mode == SignalMode.Silence ? 0 : Math.Pow(10, settings.LevelDb / 20);

        // Keep the tone strictly below Nyquist so it can't alias into the audible range.
        var frequency = mode == SignalMode.Ultrasound
            ? Math.Min(settings.FrequencyHz, _sampleRate * 0.47)
            : settings.FrequencyHz;
        var step = 2 * Math.PI * frequency / _sampleRate;

        var frames = buffer.Length / _channels;
        for (var i = 0; i < frames; i++)
        {
            var diff = target - _amplitude;
            _amplitude = Math.Abs(diff) < 1e-7 ? target : _amplitude + diff * _smoothing;

            double value;
            if (_amplitude == 0)
            {
                value = 0;
            }
            else if (mode == SignalMode.Noise)
            {
                value = _amplitude * NextNoise();
            }
            else
            {
                value = _amplitude * Math.Sin(_phase);
            }

            _phase += step;
            if (_phase >= 2 * Math.PI)
            {
                _phase -= 2 * Math.PI;
            }

            var sample = (float)value;
            var offset = i * _channels;
            for (var c = 0; c < _channels; c++)
            {
                buffer[offset + c] = sample;
            }
        }

        buffer[(frames * _channels)..].Clear();
    }

    private double NextNoise()
    {
        // xorshift32: cheap and good enough for noise.
        var x = _noiseState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _noiseState = x;
        return x / (double)uint.MaxValue * 2 - 1;
    }
}
