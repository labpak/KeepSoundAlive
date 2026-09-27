using System.Text.Json;
using System.Text.Json.Serialization;
using KeepSoundAlive.Audio;

namespace KeepSoundAlive.Services;

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Device to keep alive; null means the system default device.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Last known name of <see cref="DeviceId"/>, shown while the device is disconnected.</summary>
    public string? DeviceName { get; set; }

    public SignalMode Mode { get; set; } = SignalSettings.Default.Mode;

    // Each mode keeps its own parameters, so switching modes back and forth doesn't lose tuning.
    public double InfrasoundHz { get; set; } = SignalSettings.InfrasoundDefaultHz;

    public double UltrasoundHz { get; set; } = SignalSettings.UltrasoundDefaultHz;

    public double InfrasoundLevelDb { get; set; } = SignalSettings.DefaultLevel(SignalMode.Infrasound);

    public double UltrasoundLevelDb { get; set; } = SignalSettings.DefaultLevel(SignalMode.Ultrasound);

    public double NoiseLevelDb { get; set; } = SignalSettings.DefaultLevel(SignalMode.Noise);

    /// <summary>Windows only: stop the stream while the user is away so the PC can go to sleep.</summary>
    public bool PauseWhenIdle { get; set; } = true;

    public int IdleMinutes { get; set; } = 10;

    public bool CloseToTray { get; set; } = true;

    /// <summary>"auto", "en" or "ru".</summary>
    public string Language { get; set; } = "auto";

    public double LevelFor(SignalMode mode) => mode switch
    {
        SignalMode.Infrasound => InfrasoundLevelDb,
        SignalMode.Ultrasound => UltrasoundLevelDb,
        SignalMode.Noise => NoiseLevelDb,
        _ => SignalSettings.MinLevelDb,
    };

    public void SetLevel(SignalMode mode, double levelDb)
    {
        switch (mode)
        {
            case SignalMode.Infrasound: InfrasoundLevelDb = levelDb; break;
            case SignalMode.Ultrasound: UltrasoundLevelDb = levelDb; break;
            case SignalMode.Noise: NoiseLevelDb = levelDb; break;
        }
    }

    public void ResetSignal()
    {
        var defaults = new AppSettings();
        Mode = defaults.Mode;
        InfrasoundHz = defaults.InfrasoundHz;
        UltrasoundHz = defaults.UltrasoundHz;
        InfrasoundLevelDb = defaults.InfrasoundLevelDb;
        UltrasoundLevelDb = defaults.UltrasoundLevelDb;
        NoiseLevelDb = defaults.NoiseLevelDb;
    }

    public SignalSettings ToSignal() =>
        new SignalSettings(Mode, Mode == SignalMode.Ultrasound ? UltrasoundHz : InfrasoundHz, LevelFor(Mode)).Normalized();
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public static class SettingsStore
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "KeepSoundAlive");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var stream = File.OpenRead(FilePath);
                return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must never prevent the app from starting.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to persist settings is not worth crashing over.
        }
    }
}
