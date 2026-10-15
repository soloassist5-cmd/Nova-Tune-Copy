using System.Text.Json;
using NovaTune.Core.Common;
using NovaTune.Core.Localization;
using NovaTune.Core.Logging;

namespace NovaTune.Core.Settings;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>Пользовательские данные об игре в Game Center (заметки, профиль, ручное добавление).</summary>
public sealed class GameEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>AppID Steam; null — игра добавлена вручную.</summary>
    public int? SteamAppId { get; set; }

    public string Name { get; set; } = "";

    public string? InstallDir { get; set; }

    public string? ExecutablePath { get; set; }

    public string Profile { get; set; } = "Balanced";

    public string Notes { get; set; } = "";
}

public sealed class AppSettings
{
    public AppLanguage Language { get; set; } = AppLanguage.Ru;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Интервал мониторинга, мс (500–5000).</summary>
    public int MonitoringIntervalMs { get; set; } = 1000;

    /// <summary>Папка бэкапов; null — по умолчанию (%LOCALAPPDATA%\NovaTune\backups).</summary>
    public string? BackupDirectory { get; set; }

    /// <summary>Расширенные датчики (температура CPU через LibreHardwareMonitor + PawnIO; нужны права администратора).</summary>
    public bool AdvancedSensors { get; set; }

    public string? Cs2ManualPath { get; set; }

    public string Cs2Profile { get; set; } = "Competitive";

    public List<GameEntry> Games { get; set; } = [];

    public List<string> PingTargets { get; set; } = ["1.1.1.1", "8.8.8.8"];

    public DateTimeOffset? LastScan { get; set; }

    public void Normalize()
    {
        MonitoringIntervalMs = Math.Clamp(MonitoringIntervalMs, 500, 5000);
        Games ??= [];
        PingTargets ??= [];
    }
}

/// <summary>Загрузка и сохранение настроек (JSON, атомарная запись). Повреждённый файл не роняет приложение.</summary>
public sealed class SettingsService(string path, ILog log)
{
    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(path))
            {
                Current = Json.Read<AppSettings>(path) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Error("Настройки повреждены или недоступны — используются значения по умолчанию", ex);
            TryKeepBroken();
            Current = new AppSettings();
        }

        Current.Normalize();
        return Current;
    }

    public void Save()
    {
        Current.Normalize();
        try
        {
            Json.WriteAtomic(path, Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("Не удалось сохранить настройки", ex);
            throw;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TryKeepBroken()
    {
        try
        {
            File.Copy(path, path + ".broken", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("Не удалось сохранить копию повреждённых настроек", ex);
        }
    }
}
