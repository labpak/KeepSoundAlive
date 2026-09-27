using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using KeepSoundAlive.Audio;
using KeepSoundAlive.Services;
using KeepSoundAlive.ViewModels;
using KeepSoundAlive.Views;

namespace KeepSoundAlive;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private KeepAliveEngine? _engine;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _trayShow;
    private NativeMenuItem? _trayEnabled;
    private NativeMenuItem? _trayExit;
    private WindowIcon? _iconOn;
    private WindowIcon? _iconOff;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var settings = SettingsStore.Load();
            _engine = new KeepAliveEngine(CreateBackend());
            _viewModel = new MainViewModel(_engine, settings);
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.RestartRequested += Exit; // the update helper swaps the file and restarts after we exit
            _viewModel.OpenUrlRequested += url => _window?.Launcher.LaunchUriAsync(new Uri(url));

            AutoStart.RefreshIfEnabled();
            if (OperatingSystem.IsLinux())
            {
                LinuxDesktop.RefreshIfInMenu();
            }

            CreateTray();

            if (Program.Instance is { } instance)
            {
                instance.ActivationRequested += () => Dispatcher.UIThread.Post(ShowMainWindow);
            }

            if (!Program.StartMinimized)
            {
                ShowMainWindow();
            }

            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                _viewModel.Dispose();
                _engine.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IAudioBackend CreateBackend()
    {
        try
        {
            return AudioBackends.CreateDefault();
        }
        catch (Exception ex)
        {
            return new UnavailableBackend(ex.Message);
        }
    }

    private void CreateTray()
    {
        _iconOn = new WindowIcon(AssetLoader.Open(new Uri("avares://KeepSoundAlive/Assets/tray-on.png")));
        _iconOff = new WindowIcon(AssetLoader.Open(new Uri("avares://KeepSoundAlive/Assets/tray-off.png")));

        _trayShow = new NativeMenuItem();
        _trayShow.Click += (_, _) => ShowMainWindow();

        _trayEnabled = new NativeMenuItem { ToggleType = MenuItemToggleType.CheckBox };
        _trayEnabled.Click += (_, _) => _viewModel!.Enabled = !_viewModel.Enabled;

        _trayExit = new NativeMenuItem();
        _trayExit.Click += (_, _) => Exit();

        _tray = new TrayIcon
        {
            Menu = [_trayShow, _trayEnabled, new NativeMenuItemSeparator(), _trayExit],
        };
        _tray.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, [_tray]);

        UpdateTray();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.T) or nameof(MainViewModel.Enabled)
            or nameof(MainViewModel.StatusText) or nameof(MainViewModel.IsActive))
        {
            UpdateTray();
        }
    }

    private void UpdateTray()
    {
        if (_tray is null || _viewModel is null)
        {
            return;
        }

        var t = _viewModel.T;
        _trayShow!.Header = t.TrayShow;
        _trayEnabled!.Header = t.TrayEnabled;
        _trayEnabled.IsChecked = _viewModel.Enabled;
        _trayExit!.Header = t.TrayExit;
        _tray.Icon = _viewModel.IsActive ? _iconOn : _iconOff;
        _tray.ToolTipText = "KeepSoundAlive — " + _viewModel.StatusText;
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow { DataContext = _viewModel };
            _window.ExitRequested += Exit;
            _window.Closed += (_, _) =>
            {
                _window = null;
                // Living in the tray: give the window's memory back to the OS.
                DispatcherTimer.RunOnce(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }, TimeSpan.FromSeconds(1));
            };
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _ = _viewModel?.RefreshDevicesAsync();
    }

    private void Exit()
    {
        if (_window is { } window)
        {
            window.ExitRequested -= Exit;
            window.Close();
        }

        _desktop?.Shutdown();
    }

    /// <summary>Stands in when no audio system could be initialised, so the UI can still show why.</summary>
    private sealed class UnavailableBackend(string reason) : IAudioBackend
    {
        public string Name => "—";

        public bool FollowsDefaultDeviceAutomatically => true;

        public IReadOnlyList<AudioDevice> GetDevices() => [];

        public string? GetDefaultDeviceId() => null;

        public IAudioOutput Open(string? deviceId, SignalSource source) => throw new InvalidOperationException(reason);

        public void Dispose()
        {
        }
    }
}
