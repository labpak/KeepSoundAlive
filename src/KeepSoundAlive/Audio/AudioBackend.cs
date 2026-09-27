using KeepSoundAlive.Audio.Linux;
using KeepSoundAlive.Audio.Windows;

namespace KeepSoundAlive.Audio;

public sealed record AudioDevice(string Id, string Name);

/// <summary>A platform audio API. All methods are called from the engine's single control thread.</summary>
public interface IAudioBackend : IDisposable
{
    string Name { get; }

    /// <summary>True when a stream opened on the default device moves by itself when the default changes.</summary>
    bool FollowsDefaultDeviceAutomatically { get; }

    IReadOnlyList<AudioDevice> GetDevices();

    string? GetDefaultDeviceId();

    /// <summary>Opens a stream and starts playing the signal. <paramref name="deviceId"/> null means the default device.</summary>
    IAudioOutput Open(string? deviceId, SignalSource source);
}

public interface IAudioOutput : IDisposable
{
    /// <summary>Id of the device actually used, or null if the backend routes to the default device itself.</summary>
    string? DeviceId { get; }

    string DeviceName { get; }

    /// <summary>Non-null once the stream has died (device unplugged, audio service restarted, ...).</summary>
    string? Failure { get; }
}

public static class AudioBackends
{
    public static IAudioBackend CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WasapiBackend();
        }

        if (OperatingSystem.IsLinux())
        {
            if (PulseBackend.IsAvailable())
            {
                return new PulseBackend();
            }

            if (AlsaBackend.IsAvailable())
            {
                return new AlsaBackend();
            }

            throw new PlatformNotSupportedException(
                "Neither PulseAudio/PipeWire (libpulse-simple.so.0) nor ALSA (libasound.so.2) is available.");
        }

        throw new PlatformNotSupportedException("Only Windows and Linux are supported.");
    }
}
