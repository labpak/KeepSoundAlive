using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeepSoundAlive.Audio;
using KeepSoundAlive.Services;

namespace KeepSoundAlive.ViewModels;

public sealed partial class OptionItem(object value, string name) : ObservableObject
{
    public object Value { get; } = value;

    [ObservableProperty]
    public partial string Name { get; set; } = name;
}

public sealed partial class DeviceItem(string? id, string rawName, bool connected) : ObservableObject
{
    /// <summary>Null for the "system default" entry.</summary>
    public string? Id { get; } = id;

    public string RawName { get; } = rawName;

    public bool Connected { get; } = connected;

    [ObservableProperty]
    public partial string Name { get; set; } = rawName;

    public void Localize(Strings t) =>
        Name = Id is null ? t.DefaultDevice : Connected ? RawName : $"{RawName} ({t.Disconnected})";
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush OffBrush = new SolidColorBrush(Color.Parse("#9CA3AF"));

    private readonly KeepAliveEngine _engine;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _idleTimer;
    private readonly DeviceItem _defaultDevice;
    private bool _suppress;

    public MainViewModel(KeepAliveEngine engine, AppSettings settings)
    {
        _engine = engine;
        _settings = settings;
        T = Strings.For(settings.Language);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SettingsStore.Save(_settings);
        };

        Modes =
        [
            new(SignalMode.Infrasound, T.ModeInfrasound),
            new(SignalMode.Ultrasound, T.ModeUltrasound),
            new(SignalMode.Noise, T.ModeNoise),
            new(SignalMode.Silence, T.ModeSilence),
        ];

        _defaultDevice = new DeviceItem(null, string.Empty, true);
        Devices.Add(_defaultDevice);
        if (settings.DeviceId is not null)
        {
            Devices.Add(new DeviceItem(settings.DeviceId, settings.DeviceName ?? settings.DeviceId, true));
        }

        _suppress = true;
        LoadSignalSettings();
        SelectedDevice = Devices[^1];
        SelectedLanguage = Languages.First(l => (string)l.Value == T.Code);
        Enabled = settings.Enabled;
        StartWithSystem = AutoStart.IsEnabled;
        ShowInMenu = OperatingSystem.IsLinux() && LinuxDesktop.IsInMenu;
        CloseToTray = settings.CloseToTray;
        PauseWhenIdle = settings.PauseWhenIdle;
        IdleMinutes = Math.Clamp(settings.IdleMinutes, 1, 240);
        _suppress = false;

        Localize();

        _engine.Signal = settings.ToSignal();
        _engine.DeviceId = settings.DeviceId;
        _engine.Enabled = settings.Enabled;
        _engine.StatusChanged += status => Dispatcher.UIThread.Post(() => ApplyStatus(status));
        ApplyStatus(_engine.Status);

        // GetLastInputInfo is a cheap call; polling every second makes resuming feel instant.
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleTimer.Tick += (_, _) => UpdateIdlePause();
        if (UserIdle.IsSupported)
        {
            _idleTimer.Start();
        }

        _ = RefreshDevicesAsync();
    }

    [ObservableProperty]
    public partial Strings T { get; private set; }

    public string Version => "v" + AppInfo.Version;

    public string RepositoryUrl => AppInfo.RepositoryUrl;

    public string BackendName => _engine.BackendName;

    public bool IsIdlePauseSupported => UserIdle.IsSupported;

    public bool IsMenuEntrySupported => OperatingSystem.IsLinux();

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    public IReadOnlyList<OptionItem> Modes { get; }

    // ---- Status ----

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IBrush StatusBrush { get; private set; } = OffBrush;

    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    private void ApplyStatus(EngineStatus status)
    {
        StatusText = status.State switch
        {
            EngineState.Running => string.Format(T.StatusRunning, status.DeviceName),
            EngineState.PausedIdle => T.StatusPausedIdle,
            EngineState.WaitingForDevice => T.StatusWaiting,
            EngineState.Error => string.Format(T.StatusError, status.Message?.TrimEnd('.')),
            _ => Enabled ? T.StatusStarting : T.StatusStopped,
        };
        StatusBrush = status.State switch
        {
            EngineState.Running => ActiveBrush,
            EngineState.PausedIdle or EngineState.WaitingForDevice => WarningBrush,
            EngineState.Error => ErrorBrush,
            _ => OffBrush,
        };
        IsActive = status.State == EngineState.Running;
    }

    // ---- Main switch ----

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _settings.Enabled = value;
        _engine.Enabled = value;
        ScheduleSave();
        ApplyStatus(_engine.Status);
    }

    // ---- Device ----

    [ObservableProperty]
    public partial DeviceItem? SelectedDevice { get; set; }

    partial void OnSelectedDeviceChanged(DeviceItem? value)
    {
        if (_suppress || value is null)
        {
            return;
        }

        _settings.DeviceId = value.Id;
        _settings.DeviceName = value.Id is null ? null : value.RawName;
        _engine.DeviceId = value.Id;
        ScheduleSave();
    }

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        IReadOnlyList<AudioDevice> devices;
        try
        {
            devices = await _engine.GetDevicesAsync();
        }
        catch
        {
            devices = [];
        }

        var selectedId = _settings.DeviceId;
        var items = new List<DeviceItem> { _defaultDevice };
        items.AddRange(devices.Select(d => new DeviceItem(d.Id, d.Name, true)));
        if (selectedId is not null && devices.All(d => d.Id != selectedId))
        {
            items.Add(new DeviceItem(selectedId, _settings.DeviceName ?? selectedId, false));
        }

        foreach (var item in items)
        {
            item.Localize(T);
        }

        _suppress = true;
        try
        {
            SelectedDevice = null;
            Devices.Clear();
            foreach (var item in items)
            {
                Devices.Add(item);
            }

            SelectedDevice = items.First(i => i.Id == selectedId);
        }
        finally
        {
            _suppress = false;
        }

        if (SelectedDevice is { Id: not null, Connected: true } device && device.RawName != _settings.DeviceName)
        {
            _settings.DeviceName = device.RawName;
            ScheduleSave();
        }
    }

    // ---- Signal ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInfrasound), nameof(IsUltrasound), nameof(IsLevelVisible))]
    public partial OptionItem? SelectedMode { get; set; }

    partial void OnSelectedModeChanged(OptionItem? value)
    {
        if (_suppress || value is null)
        {
            return;
        }

        _settings.Mode = (SignalMode)value.Value;
        _suppress = true;
        LevelDb = _settings.LevelFor((SignalMode)value.Value);
        _suppress = false;
        ApplySignal();
    }

    private SignalMode Mode => SelectedMode?.Value is SignalMode mode ? mode : SignalMode.Infrasound;

    public bool IsInfrasound => Mode == SignalMode.Infrasound;

    public bool IsUltrasound => Mode == SignalMode.Ultrasound;

    public bool IsLevelVisible => Mode != SignalMode.Silence;

    public decimal InfrasoundMinimum => (decimal)SignalSettings.InfrasoundMinHz;

    public decimal InfrasoundMaximum => (decimal)SignalSettings.InfrasoundMaxHz;

    public decimal UltrasoundMinimum => (decimal)SignalSettings.UltrasoundMinHz;

    public decimal UltrasoundMaximum => (decimal)SignalSettings.UltrasoundMaxHz;

    [ObservableProperty]
    public partial decimal? InfrasoundHz { get; set; }

    partial void OnInfrasoundHzChanged(decimal? value)
    {
        if (!_suppress && value is { } hz)
        {
            _settings.InfrasoundHz = (double)hz;
            ApplySignal();
        }
    }

    [ObservableProperty]
    public partial decimal? UltrasoundHz { get; set; }

    partial void OnUltrasoundHzChanged(decimal? value)
    {
        if (!_suppress && value is { } hz)
        {
            _settings.UltrasoundHz = (double)hz;
            ApplySignal();
        }
    }

    public double LevelMinimum => SignalSettings.MinLevelDb;

    public double LevelMaximum => SignalSettings.MaxLevelDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelText))]
    public partial double LevelDb { get; set; }

    partial void OnLevelDbChanged(double value)
    {
        if (!_suppress)
        {
            _settings.SetLevel(Mode, Math.Round(value));
            ApplySignal();
        }
    }

    public string LevelText => $"{Math.Round(LevelDb):0} {T.Decibels}".Replace("-", "−");

    [RelayCommand]
    private void ResetSignal()
    {
        _settings.ResetSignal();
        LoadSignalSettings();
        ApplySignal();
    }

    private void LoadSignalSettings()
    {
        var suppress = _suppress;
        _suppress = true;
        SelectedMode = Modes.First(m => (SignalMode)m.Value == _settings.Mode);
        InfrasoundHz = (decimal)_settings.InfrasoundHz;
        UltrasoundHz = (decimal)_settings.UltrasoundHz;
        LevelDb = _settings.LevelFor(_settings.Mode);
        _suppress = suppress;
    }

    private void ApplySignal()
    {
        _engine.Signal = _settings.ToSignal();
        ScheduleSave();
    }

    // ---- Settings ----

    [ObservableProperty]
    public partial bool StartWithSystem { get; set; }

    partial void OnStartWithSystemChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        try
        {
            AutoStart.Set(value);
        }
        catch
        {
            _suppress = true;
            StartWithSystem = AutoStart.IsEnabled;
            _suppress = false;
        }
    }

    [ObservableProperty]
    public partial bool ShowInMenu { get; set; }

    partial void OnShowInMenuChanged(bool value)
    {
        if (_suppress || !OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            LinuxDesktop.SetInMenu(value);
        }
        catch
        {
            _suppress = true;
            ShowInMenu = LinuxDesktop.IsInMenu;
            _suppress = false;
        }
    }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (!_suppress)
        {
            _settings.CloseToTray = value;
            ScheduleSave();
        }
    }

    [ObservableProperty]
    public partial bool PauseWhenIdle { get; set; }

    partial void OnPauseWhenIdleChanged(bool value)
    {
        if (!_suppress)
        {
            _settings.PauseWhenIdle = value;
            ScheduleSave();
            UpdateIdlePause();
        }
    }

    [ObservableProperty]
    public partial decimal? IdleMinutes { get; set; }

    partial void OnIdleMinutesChanged(decimal? value)
    {
        if (!_suppress && value is { } minutes)
        {
            _settings.IdleMinutes = (int)minutes;
            ScheduleSave();
            UpdateIdlePause();
        }
    }

    /// <summary>
    /// An open audio stream keeps Windows from sleeping, so the signal pauses once the computer is really unused:
    /// no keyboard/mouse input AND no sound from other programs for the configured time. Sound counts as activity
    /// because pausing during a long listening session would let the speakers sleep between tracks (and Windows
    /// can't sleep while that music plays anyway).
    /// </summary>
    private void UpdateIdlePause()
    {
        var enabled = UserIdle.IsSupported && _settings.PauseWhenIdle;
        _engine.TrackOtherAudio = enabled;
        if (!enabled)
        {
            _engine.Paused = false;
            return;
        }

        var inactive = TimeSpan.FromTicks(Math.Min(UserIdle.GetIdleTime().Ticks, _engine.TimeSinceOtherAudio.Ticks));
        _engine.Paused = inactive >= TimeSpan.FromMinutes(Math.Max(1, _settings.IdleMinutes));
    }

    // ---- Language ----

    // Each language is listed under its own name, so these never need translating.
    public IReadOnlyList<OptionItem> Languages { get; } =
    [
        new("en", "English"),
        new("ru", "Русский"),
    ];

    [ObservableProperty]
    public partial OptionItem? SelectedLanguage { get; set; }

    partial void OnSelectedLanguageChanged(OptionItem? value)
    {
        if (_suppress || value is null || (string)value.Value == T.Code)
        {
            return;
        }

        _settings.Language = (string)value.Value;
        ScheduleSave();
        T = Strings.For(_settings.Language);
        Localize();
    }

    private void Localize()
    {
        foreach (var mode in Modes)
        {
            mode.Name = (SignalMode)mode.Value switch
            {
                SignalMode.Infrasound => T.ModeInfrasound,
                SignalMode.Ultrasound => T.ModeUltrasound,
                SignalMode.Noise => T.ModeNoise,
                _ => T.ModeSilence,
            };
        }

        foreach (var device in Devices)
        {
            device.Localize(T);
        }

        OnPropertyChanged(nameof(LevelText));
        ApplyStatus(_engine.Status);
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Dispose()
    {
        _idleTimer.Stop();
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            SettingsStore.Save(_settings);
        }
    }
}
