using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace KeepSoundAlive.Audio.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WasapiBackend : IAudioBackend
{
    private MMDeviceEnumerator? _enumerator;

    public string Name => "WASAPI";

    public bool FollowsDefaultDeviceAutomatically => false;

    private MMDeviceEnumerator Enumerator => _enumerator ??= new MMDeviceEnumerator();

    public IReadOnlyList<AudioDevice> GetDevices()
    {
        var result = new List<AudioDevice>();
        foreach (var device in Enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDevice(device.ID, device.FriendlyName));
            }
        }

        return result;
    }

    public string? GetDefaultDeviceId()
    {
        if (!Enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) || device is null)
        {
            return null;
        }

        using (device)
        {
            return device.ID;
        }
    }

    public IAudioOutput Open(string? deviceId, SignalSource source)
    {
        var device = deviceId is null
            ? Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : Enumerator.GetDevice(deviceId);

        try
        {
            return new WasapiOutput(device, source);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public bool IsOtherAudioPlaying(string? deviceId)
    {
        var ownProcess = (uint)Environment.ProcessId;
        MMDevice? device;
        if (deviceId is not null)
        {
            device = Enumerator.GetDevice(deviceId);
        }
        else if (!Enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device) || device is null)
        {
            return false;
        }

        using (device)
        using (var manager = device.AudioSessionManager)
        using (var sessions = manager.Sessions)
        {
            for (var i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                // An open but silent stream (a paused video, for example) doesn't count: only actual sound does.
                if (session.GetProcessID != ownProcess &&
                    session.State == AudioSessionState.AudioSessionStateActive &&
                    session.AudioMeterInformation.MasterPeakValue > 0.0001f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private sealed class WasapiOutput : IAudioOutput, IWaveProvider
    {
        private readonly MMDevice _device;
        private readonly WasapiPlayer _player;
        private readonly SignalGenerator _generator;
        private volatile string? _failure;

        public WasapiOutput(MMDevice device, SignalSource source)
        {
            _device = device;
            DeviceId = device.ID;
            DeviceName = device.FriendlyName;

            _player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(200)
                .Build();

            // Generate directly in the engine's mix format so shared mode needs no conversion or resampling.
            var mix = _player.DeviceMixFormat;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
            _generator = new SignalGenerator(source, mix.SampleRate, mix.Channels);

            _player.PlaybackStopped += (_, e) => _failure = e.Exception?.Message ?? "Playback stopped";
            _player.Init(this);
            _player.Play();
        }

        public string? DeviceId { get; }

        public string DeviceName { get; }

        public string? Failure => _failure;

        public WaveFormat WaveFormat { get; }

        public int Read(Span<byte> buffer)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            _generator.Fill(samples);
            return samples.Length * sizeof(float);
        }

        public void Dispose()
        {
            _failure ??= "Closed";
            try
            {
                _player.Stop();
            }
            catch
            {
                // The device may already be gone.
            }

            _player.Dispose();
            _device.Dispose();
        }
    }
}
