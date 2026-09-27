using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace KeepSoundAlive.Services;

/// <summary>Start with the system: HKCU Run key on Windows, an XDG autostart entry on Linux.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KeepSoundAlive";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    private static string Command => $"\"{AppInfo.ExecutablePath}\" --minimized";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(ValueName) is string;
                }

                return OperatingSystem.IsLinux() && File.Exists(LinuxDesktop.AutostartFile);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindows(enabled);
        }
        else if (OperatingSystem.IsLinux())
        {
            if (enabled)
            {
                LinuxDesktop.WriteEntry(LinuxDesktop.AutostartFile, "--minimized");
            }
            else
            {
                File.Delete(LinuxDesktop.AutostartFile);
            }
        }
    }

    /// <summary>Re-points an existing autostart entry at the current executable (the user may have moved it).</summary>
    public static void RefreshIfEnabled()
    {
        try
        {
            if (IsEnabled)
            {
                Set(true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>Freedesktop integration: autostart entry and an optional application menu entry with an icon.</summary>
public static class LinuxDesktop
{
    private static string ConfigHome => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string DataHome
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdg;
        }
    }

    public static string AutostartFile => Path.Combine(ConfigHome, "autostart", "keepsoundalive.desktop");

    public static string MenuFile => Path.Combine(DataHome, "applications", "keepsoundalive.desktop");

    private static string IconFile => Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", "keepsoundalive.png");

    public static bool IsInMenu => File.Exists(MenuFile);

    public static void SetInMenu(bool enabled)
    {
        if (enabled)
        {
            WriteEntry(MenuFile, string.Empty);
        }
        else
        {
            File.Delete(MenuFile);
        }
    }

    public static void RefreshIfInMenu()
    {
        try
        {
            if (IsInMenu)
            {
                WriteEntry(MenuFile, string.Empty);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    internal static void WriteEntry(string path, string arguments)
    {
        InstallIcon();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var exec = $"\"{AppInfo.ExecutablePath.Replace("\"", "\\\"")}\" {arguments}".TrimEnd();
        File.WriteAllText(path, $"""
            [Desktop Entry]
            Type=Application
            Name=KeepSoundAlive
            Comment=Keeps speakers awake with an inaudible signal
            Comment[ru]=Не даёт колонкам засыпать с помощью неслышимого сигнала
            Exec={exec}
            Icon=keepsoundalive
            Terminal=false
            Categories=AudioVideo;Audio;Utility;
            StartupWMClass=KeepSoundAlive
            X-GNOME-Autostart-enabled=true

            """);
    }

    private static void InstallIcon()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!);
            using var source = typeof(LinuxDesktop).Assembly.GetManifestResourceStream("icon.png")!;
            using var target = File.Create(IconFile);
            source.CopyTo(target);
        }
        catch
        {
            // The entry still works without an icon.
        }
    }
}

/// <summary>Windows: how long the user has not touched the keyboard or mouse.</summary>
public static partial class UserIdle
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static TimeSpan GetIdleTime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return TimeSpan.Zero;
        }

        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both values are 32-bit millisecond tick counts; unsigned subtraction handles the 49-day wrap-around.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);
}

public static class AppInfo
{
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "KeepSoundAlive");

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    public const string RepositoryUrl = "https://github.com/labpak/KeepSoundAlive";
}
