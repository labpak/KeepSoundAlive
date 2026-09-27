using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KeepSoundAlive.Audio.Linux;

/// <summary>Fallback for systems without a PulseAudio/PipeWire server: talks to ALSA directly.</summary>
[SupportedOSPlatform("linux")]
internal sealed class AlsaBackend : IAudioBackend
{
    private const string DefaultDevice = "default";
    private const int SampleRate = 48000;
    private const int Channels = 2;

    public string Name => "ALSA";

    public bool FollowsDefaultDeviceAutomatically => true;

    public static bool IsAvailable() => NativeLibrary.TryLoad(AlsaNative.Lib, out _);

    public unsafe IReadOnlyList<AudioDevice> GetDevices()
    {
        var result = new List<AudioDevice>();
        void** hints;
        if (AlsaNative.snd_device_name_hint(-1, "pcm", &hints) < 0)
        {
            return result;
        }

        try
        {
            for (var hint = hints; *hint != null; hint++)
            {
                var name = TakeString(AlsaNative.snd_device_name_get_hint(*hint, "NAME"));
                var description = TakeString(AlsaNative.snd_device_name_get_hint(*hint, "DESC"));
                var ioid = TakeString(AlsaNative.snd_device_name_get_hint(*hint, "IOID"));
                if (name is null || ioid == "Input" || !IsPlaybackDevice(name))
                {
                    continue;
                }

                // DESC is multi-line ("Card name\nDevice description").
                var label = description?.Replace('\n', ' ').Trim();
                result.Add(new AudioDevice(name, string.IsNullOrEmpty(label) ? name : $"{label} ({name})"));
            }
        }
        finally
        {
            AlsaNative.snd_device_name_free_hint(hints);
        }

        return result;
    }

    /// <summary>
    /// ALSA hints also list converter/effect plugins (samplerate, upmix, speex, ...). Keep only entries that are
    /// real outputs: the default device, sound servers and card-specific devices.
    /// </summary>
    private static bool IsPlaybackDevice(string name) =>
        name is "default" or "pulse" or "pipewire" or "jack" ||
        (name.Contains(':') && !name.StartsWith("hw:", StringComparison.Ordinal) &&
         !name.StartsWith("dsnoop:", StringComparison.Ordinal) && !name.StartsWith("surround", StringComparison.Ordinal));

    public string? GetDefaultDeviceId() => DefaultDevice;

    public IAudioOutput Open(string? deviceId, SignalSource source) => new AlsaOutput(deviceId ?? DefaultDevice, source);

    public void Dispose()
    {
    }

    private static unsafe string? TakeString(nint ptr)
    {
        if (ptr == 0)
        {
            return null;
        }

        var value = Marshal.PtrToStringUTF8(ptr);
        NativeMemory.Free((void*)ptr); // allocated by ALSA with malloc()
        return value;
    }

    private sealed unsafe class AlsaOutput : IAudioOutput
    {
        private readonly nint _pcm;
        private readonly Thread _thread;
        private readonly SignalGenerator _generator;
        private volatile bool _stop;
        private volatile string? _failure;

        public AlsaOutput(string device, SignalSource source)
        {
            DeviceId = device;
            DeviceName = device;
            _generator = new SignalGenerator(source, SampleRate, Channels);

            nint pcm;
            var error = AlsaNative.snd_pcm_open(&pcm, device, AlsaNative.StreamPlayback, 0);
            if (error < 0)
            {
                throw new InvalidOperationException(AlsaNative.ErrorText(error));
            }

            error = AlsaNative.snd_pcm_set_params(pcm, AlsaNative.FormatS16Le, AlsaNative.AccessRwInterleaved,
                Channels, SampleRate, 1, 200_000);
            if (error < 0)
            {
                AlsaNative.snd_pcm_close(pcm);
                throw new InvalidOperationException(AlsaNative.ErrorText(error));
            }

            _pcm = pcm;
            _thread = new Thread(Run) { IsBackground = true, Name = "KeepSoundAlive ALSA" };
            _thread.Start();
        }

        public string? DeviceId { get; }

        public string DeviceName { get; }

        public string? Failure => _failure;

        private void Run()
        {
            const int frames = SampleRate / 20; // 50 ms
            var floats = new float[frames * Channels];
            var pcm16 = new short[frames * Channels];
            fixed (short* ptr = pcm16)
            {
                while (!_stop)
                {
                    _generator.Fill(floats);
                    for (var i = 0; i < floats.Length; i++)
                    {
                        pcm16[i] = (short)Math.Clamp(MathF.Round(floats[i] * 32767f), -32768f, 32767f);
                    }

                    var written = AlsaNative.snd_pcm_writei(_pcm, ptr, frames);
                    if (written < 0 && AlsaNative.snd_pcm_recover(_pcm, (int)written, 1) < 0)
                    {
                        _failure = AlsaNative.ErrorText((int)written);
                        return;
                    }
                }
            }
        }

        public void Dispose()
        {
            _failure ??= "Closed";
            _stop = true;
            if (_thread.Join(TimeSpan.FromSeconds(3)))
            {
                AlsaNative.snd_pcm_close(_pcm);
            }
        }
    }
}

[SupportedOSPlatform("linux")]
internal static unsafe partial class AlsaNative
{
    public const string Lib = "libasound.so.2";

    public const int StreamPlayback = 0;
    public const int FormatS16Le = 2;
    public const int AccessRwInterleaved = 3;

    public static string ErrorText(int error) => Marshal.PtrToStringUTF8(snd_strerror(error)) ?? $"ALSA error {error}";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int snd_pcm_open(nint* pcm, string name, int stream, int mode);

    [LibraryImport(Lib)]
    public static partial int snd_pcm_set_params(nint pcm, int format, int access, uint channels, uint rate,
        int softResample, uint latencyUs);

    [LibraryImport(Lib)]
    public static partial nint snd_pcm_writei(nint pcm, void* buffer, nuint frames);

    [LibraryImport(Lib)]
    public static partial int snd_pcm_recover(nint pcm, int error, int silent);

    [LibraryImport(Lib)]
    public static partial int snd_pcm_close(nint pcm);

    [LibraryImport(Lib)]
    public static partial nint snd_strerror(int error);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int snd_device_name_hint(int card, string iface, void*** hints);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint snd_device_name_get_hint(void* hint, string id);

    [LibraryImport(Lib)]
    public static partial int snd_device_name_free_hint(void** hints);
}
