using System.Runtime.InteropServices;
using Avalonia;
using KeepSoundAlive.Audio;
using KeepSoundAlive.Services;

namespace KeepSoundAlive;

internal static partial class Program
{
    private const string Usage = """
        KeepSoundAlive — keeps speakers awake with an inaudible signal.

        Usage: KeepSoundAlive [options]

          (no options)       Start with the window open.
          --minimized        Start hidden in the system tray (used by autostart).
          --headless         Run without any UI using the saved settings (for servers / SSH). Ctrl+C to stop.
          --list-devices     Print the available output devices and exit.
          --add-to-menu      Linux: add KeepSoundAlive to the applications menu and exit.
          --update           Download and install the latest version from GitHub, then exit.
          --help             Show this help.

        Settings file: {0}
        """;

    public static bool StartMinimized { get; private set; }

    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var options = new HashSet<string>(args.Select(a => a.ToLowerInvariant()));

        Updater.CleanUpLeftovers();

        if (options.Overlaps(["--help", "-h", "/?"]))
        {
            AttachConsole();
            Console.WriteLine(Usage, SettingsStore.FilePath);
            return 0;
        }

        if (options.Contains("--list-devices"))
        {
            AttachConsole();
            return ListDevices();
        }

        if (options.Contains("--add-to-menu") && OperatingSystem.IsLinux())
        {
            LinuxDesktop.SetInMenu(true);
            Console.WriteLine($"Added: {LinuxDesktop.MenuFile}");
            return 0;
        }

        if (options.Contains("--headless"))
        {
            AttachConsole();
            return RunHeadless();
        }

        if (options.Contains("--update"))
        {
            AttachConsole();
            return RunUpdate();
        }

        StartMinimized = options.Contains("--minimized");

        Instance = SingleInstance.TryAcquire();
        if (Instance is null)
        {
            return 0; // The running copy has been asked to show its window.
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance.Dispose();
        }
    }

    // Also used by the Avalonia designer.
    // Software rendering: a small settings window doesn't need the GPU, and not initialising the graphics
    // driver saves tens of megabytes and dozens of threads for an app that mostly sits in the tray.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .With(new X11PlatformOptions { WmClass = "KeepSoundAlive", RenderingMode = [X11RenderingMode.Software] })
            .LogToTrace();

    private static int ListDevices()
    {
        try
        {
            using var backend = AudioBackends.CreateDefault();
            var defaultId = backend.GetDefaultDeviceId();
            Console.WriteLine($"Audio backend: {backend.Name}");
            foreach (var device in backend.GetDevices())
            {
                Console.WriteLine($"{(device.Id == defaultId ? "*" : " ")} {device.Name}");
                Console.WriteLine($"    id: {device.Id}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunUpdate()
    {
        if (!Updater.CanUpdateInPlace)
        {
            Console.Error.WriteLine($"Only the published single-file build can update itself. Download: {Updater.ReleasesPage}");
            return 1;
        }

        if (SingleInstance.IsRunning)
        {
            Console.Error.WriteLine("KeepSoundAlive is running: exit it first, or use the update button in its window.");
            return 1;
        }

        try
        {
            Console.WriteLine($"Current version: {Updater.CurrentVersion}");
            var update = Updater.CheckAsync(Updater.CurrentVersion).GetAwaiter().GetResult();
            if (update is null)
            {
                Console.WriteLine("Already up to date.");
                return 0;
            }

            Console.WriteLine($"Downloading {update.Tag} ({update.AssetName}, {update.Size / 1024 / 1024} MB)...");
            var newFile = Updater.DownloadAsync(update, Environment.ProcessPath!).GetAwaiter().GetResult();
            Updater.ApplyAfterExit(newFile, Environment.ProcessPath!, restart: false);
            Console.WriteLine($"Updated to {update.Tag}: the new version replaces this file as soon as this command exits.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Update failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunHeadless()
    {
        var settings = SettingsStore.Load();
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            Console.Error.WriteLine("KeepSoundAlive is already running.");
            return 1;
        }

        IAudioBackend backend;
        try
        {
            backend = AudioBackends.CreateDefault();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        using var engine = new KeepAliveEngine(backend);
        engine.StatusChanged += status => Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] {status.State}{(status.DeviceName is null ? "" : ": " + status.DeviceName)}" +
            $"{(status.Message is null ? "" : " — " + status.Message)}");

        var signal = settings.ToSignal();
        Console.WriteLine($"Signal: {signal.Mode}, {signal.FrequencyHz} Hz, {signal.LevelDb} dB. Settings: {SettingsStore.FilePath}");
        engine.Signal = signal;
        engine.DeviceId = settings.DeviceId;
        engine.Enabled = true;

        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            stop.Set();
        });

        stop.Wait();
        return 0;
    }

    /// <summary>A WinExe has no console; borrow the parent's one so CLI output is visible in cmd/PowerShell.</summary>
    private static void AttachConsole()
    {
        if (OperatingSystem.IsWindows() && AttachConsole(-1))
        {
            var encoding = Console.OutputEncoding; // the console's code page
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
            Console.WriteLine();
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "AttachConsole")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
