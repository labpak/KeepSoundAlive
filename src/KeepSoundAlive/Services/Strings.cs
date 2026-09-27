namespace KeepSoundAlive.Services;

/// <summary>UI texts. One instance per language; the view model swaps the instance when the language changes.</summary>
public sealed class Strings
{
    public required string Code { get; init; }

    public required string StatusStopped { get; init; }
    public required string StatusRunning { get; init; }
    public required string StatusPausedIdle { get; init; }
    public required string StatusWaiting { get; init; }
    public required string StatusError { get; init; }
    public required string StatusStarting { get; init; }

    public required string DeviceHeader { get; init; }
    public required string DefaultDevice { get; init; }
    public required string Disconnected { get; init; }
    public required string Refresh { get; init; }

    public required string SignalHeader { get; init; }
    public required string ModeInfrasound { get; init; }
    public required string ModeUltrasound { get; init; }
    public required string ModeNoise { get; init; }
    public required string ModeSilence { get; init; }
    public required string Frequency { get; init; }
    public required string Level { get; init; }
    public required string Decibels { get; init; }
    public required string ResetSignal { get; init; }

    public required string SettingsHeader { get; init; }
    public required string StartWithSystem { get; init; }
    public required string CloseToTray { get; init; }
    public required string PauseWhenIdle { get; init; }
    public required string Minutes { get; init; }
    public required string ShowInMenu { get; init; }

    public required string CheckUpdates { get; init; }
    public required string CheckingUpdates { get; init; }
    public required string UpToDate { get; init; }
    public required string UpdateTo { get; init; }
    public required string Downloading { get; init; }
    public required string UpdateFailed { get; init; }

    public required string TrayShow { get; init; }
    public required string TrayEnabled { get; init; }
    public required string TrayExit { get; init; }

    public static Strings English { get; } = new()
    {
        Code = "en",
        StatusStopped = "Off",
        StatusRunning = "Active: {0}",
        StatusPausedIdle = "Paused: no user activity",
        StatusWaiting = "Device not connected",
        StatusError = "Error: {0}",
        StatusStarting = "Starting…",
        DeviceHeader = "Device",
        DefaultDevice = "System default",
        Disconnected = "not connected",
        Refresh = "Refresh",
        SignalHeader = "Signal",
        ModeInfrasound = "Infrasound",
        ModeUltrasound = "Ultrasound",
        ModeNoise = "Quiet noise",
        ModeSilence = "Silence",
        Frequency = "Frequency, Hz",
        Level = "Level",
        Decibels = "dB",
        ResetSignal = "Reset",
        SettingsHeader = "Settings",
        StartWithSystem = "Start with the system",
        CloseToTray = "Minimize to tray on close",
        PauseWhenIdle = "Pause after",
        Minutes = "min of inactivity",
        ShowInMenu = "Show in the applications menu",
        CheckUpdates = "Check for updates",
        CheckingUpdates = "Checking…",
        UpToDate = "Up to date",
        UpdateTo = "Update to {0}",
        Downloading = "Downloading… {0}%",
        UpdateFailed = "Failed — try again",
        TrayShow = "Open",
        TrayEnabled = "Enabled",
        TrayExit = "Exit",
    };

    public static Strings Russian { get; } = new()
    {
        Code = "ru",
        StatusStopped = "Выключено",
        StatusRunning = "Работает: {0}",
        StatusPausedIdle = "Пауза: нет активности",
        StatusWaiting = "Устройство не подключено",
        StatusError = "Ошибка: {0}",
        StatusStarting = "Запуск…",
        DeviceHeader = "Устройство",
        DefaultDevice = "Системное по умолчанию",
        Disconnected = "не подключено",
        Refresh = "Обновить",
        SignalHeader = "Сигнал",
        ModeInfrasound = "Инфразвук",
        ModeUltrasound = "Ультразвук",
        ModeNoise = "Тихий шум",
        ModeSilence = "Тишина",
        Frequency = "Частота, Гц",
        Level = "Уровень",
        Decibels = "дБ",
        ResetSignal = "Сбросить",
        SettingsHeader = "Настройки",
        StartWithSystem = "Запускать вместе с системой",
        CloseToTray = "Сворачивать в трей при закрытии",
        PauseWhenIdle = "Пауза после",
        Minutes = "мин бездействия",
        ShowInMenu = "Показывать в меню приложений",
        CheckUpdates = "Проверить обновления",
        CheckingUpdates = "Проверка…",
        UpToDate = "Установлена последняя версия",
        UpdateTo = "Обновить до {0}",
        Downloading = "Загрузка… {0}%",
        UpdateFailed = "Ошибка — повторить",
        TrayShow = "Открыть",
        TrayEnabled = "Включено",
        TrayExit = "Выход",
    };

    /// <summary>"ru" gives Russian; anything else (including older "auto" settings) gives English.</summary>
    public static Strings For(string? language) => language == "ru" ? Russian : English;
}
