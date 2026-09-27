namespace KeepSoundAlive.Audio;

public enum EngineState
{
    Stopped,
    Running,
    /// <summary>Enabled, but paused while the user is away so the computer can go to sleep.</summary>
    PausedIdle,
    WaitingForDevice,
    Error,
}

public sealed record EngineStatus(EngineState State, string? DeviceName = null, string? Message = null);

/// <summary>
/// Owns the audio stream. All backend calls happen on one dedicated control thread, which reconciles the
/// desired state (enabled, device, paused) with reality every couple of seconds or whenever something changes:
/// it reopens the stream if it dies, if the selected device reappears, or if the default device changes.
/// </summary>
public sealed class KeepAliveEngine : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IAudioBackend _backend;
    private readonly SignalSource _source = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly Queue<Action> _pendingQueries = new();

    private bool _enabled;
    private bool _paused;
    private string? _deviceId;
    private bool _disposed;
    private bool _trackOtherAudio;
    private long _lastOtherAudio = -1; // Environment.TickCount64 value; -1 = never

    // Control thread only.
    private IAudioOutput? _output;
    private int _failures;
    private long _nextAttempt;
    private EngineStatus _status = new(EngineState.Stopped);

    public KeepAliveEngine(IAudioBackend backend)
    {
        _backend = backend;
        _thread = new Thread(ControlLoop) { IsBackground = true, Name = "KeepSoundAlive control" };
        _thread.Start();
    }

    /// <summary>Raised on the control thread whenever <see cref="Status"/> changes.</summary>
    public event Action<EngineStatus>? StatusChanged;

    public EngineStatus Status => Volatile.Read(ref _status);

    public string BackendName => _backend.Name;

    public SignalSettings Signal
    {
        get => _source.Settings;
        set => _source.Settings = value; // applied live by the running generator
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set => Update(ref _enabled, value);
    }

    public bool Paused
    {
        get { lock (_gate) return _paused; }
        set => Update(ref _paused, value);
    }

    /// <summary>Device to keep alive; null means the system default device.</summary>
    public string? DeviceId
    {
        get { lock (_gate) return _deviceId; }
        set => Update(ref _deviceId, value);
    }

    /// <summary>
    /// When set, the control thread keeps checking whether other programs play sound on the device
    /// (see <see cref="TimeSinceOtherAudio"/>). Only needed for the pause-when-idle feature.
    /// </summary>
    public bool TrackOtherAudio
    {
        get => Volatile.Read(ref _trackOtherAudio);
        set => Volatile.Write(ref _trackOtherAudio, value);
    }

    /// <summary>How long ago another program last played sound on the device (<see cref="TimeSpan.MaxValue"/> = never).</summary>
    public TimeSpan TimeSinceOtherAudio
    {
        get
        {
            var last = Interlocked.Read(ref _lastOtherAudio);
            return last < 0 ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(Environment.TickCount64 - last);
        }
    }

    /// <summary>Lists output devices. Runs on the control thread; safe to call from any thread.</summary>
    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync()
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<AudioDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingQueries.Enqueue(() =>
            {
                try
                {
                    tcs.SetResult(_backend.GetDevices());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
        }

        _wake.Set();
        return tcs.Task;
    }

    private void Update<T>(ref T field, T value)
    {
        lock (_gate)
        {
            // Callers may re-apply the same value often (the idle check runs every second); only a real change
            // should wake the control thread and cut short a pending retry delay.
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            Interlocked.Exchange(ref _nextAttempt, 0);
        }

        _wake.Set();
    }

    private void ControlLoop()
    {
        while (true)
        {
            _wake.WaitOne(PollInterval);

            Action[] queries;
            bool enabled, paused, disposed;
            string? deviceId;
            lock (_gate)
            {
                queries = [.. _pendingQueries];
                _pendingQueries.Clear();
                enabled = _enabled;
                paused = _paused;
                deviceId = _deviceId;
                disposed = _disposed;
            }

            foreach (var query in queries)
            {
                query();
            }

            if (disposed)
            {
                CloseOutput();
                return;
            }

            try
            {
                Reconcile(enabled, paused, deviceId);
            }
            catch (Exception ex)
            {
                CloseOutput();
                ScheduleRetry();
                Publish(new EngineStatus(EngineState.Error, Message: ex.Message));
            }

            if (enabled && TrackOtherAudio)
            {
                CheckOtherAudio(deviceId);
            }
        }
    }

    private void CheckOtherAudio(string? deviceId)
    {
        try
        {
            if (_backend.IsOtherAudioPlaying(deviceId))
            {
                Interlocked.Exchange(ref _lastOtherAudio, Environment.TickCount64);
            }
        }
        catch
        {
            // Purely advisory: if the check fails, the pause simply relies on user input alone.
        }
    }

    private void Reconcile(bool enabled, bool paused, string? deviceId)
    {
        if (!enabled || paused)
        {
            CloseOutput();
            _failures = 0;
            Publish(new EngineStatus(enabled ? EngineState.PausedIdle : EngineState.Stopped));
            return;
        }

        if (_output is not null)
        {
            if (_output.Failure is { } failure)
            {
                CloseOutput();
                ScheduleRetry();
                Publish(new EngineStatus(EngineState.Error, Message: failure));
            }
            else if (!IsOnRequestedDevice(_output, deviceId))
            {
                CloseOutput();
            }
            else
            {
                Publish(new EngineStatus(EngineState.Running, _output.DeviceName));
                return;
            }
        }

        if (Environment.TickCount64 < Interlocked.Read(ref _nextAttempt))
        {
            return;
        }

        if (deviceId is not null && _backend.GetDevices().All(d => d.Id != deviceId))
        {
            Publish(new EngineStatus(EngineState.WaitingForDevice));
            return;
        }

        if (deviceId is null && !_backend.FollowsDefaultDeviceAutomatically && _backend.GetDefaultDeviceId() is null)
        {
            Publish(new EngineStatus(EngineState.WaitingForDevice));
            return;
        }

        _output = _backend.Open(deviceId, _source);
        _failures = 0;
        Publish(new EngineStatus(EngineState.Running, _output.DeviceName));
    }

    private bool IsOnRequestedDevice(IAudioOutput output, string? deviceId)
    {
        if (deviceId is not null)
        {
            return output.DeviceId == deviceId;
        }

        // Following the default device: WASAPI streams are bound to a device, so reopen when the default changes.
        return _backend.FollowsDefaultDeviceAutomatically || output.DeviceId == _backend.GetDefaultDeviceId();
    }

    private void ScheduleRetry()
    {
        // 1, 2, 4, 5, 5... seconds. Kept short: the device may be back (e.g. an exclusive-mode app released it)
        // and speakers can fall asleep within seconds, so a long back-off would bring the original problem back.
        _failures++;
        var delay = TimeSpan.FromSeconds(Math.Min(5, Math.Pow(2, _failures - 1)));
        Interlocked.Exchange(ref _nextAttempt, Environment.TickCount64 + (long)delay.TotalMilliseconds);
    }

    private void CloseOutput()
    {
        var output = _output;
        _output = null;
        try
        {
            output?.Dispose();
        }
        catch
        {
            // Closing a stream on a vanished device may throw; nothing useful to do about it.
        }
    }

    private void Publish(EngineStatus status)
    {
        if (status == _status)
        {
            return;
        }

        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _backend.Dispose();
        _wake.Dispose();
    }
}
