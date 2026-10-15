using NovaTune.Core.Backup;
using NovaTune.Core.Common;
using NovaTune.Core.Localization;

namespace NovaTune.Core.Startup;

public enum StartupSource
{
    HkcuRun,
    HkcuRunOnce,
    HklmRun,
    HklmRunOnce,
    HklmRun32,
    HklmRunOnce32,
    UserStartupFolder,
    CommonStartupFolder,
    ScheduledTaskLogon,
}

/// <summary>Запись автозагрузки.</summary>
public sealed record StartupEntry
{
    public required string Name { get; init; }

    public required string Command { get; init; }

    public required StartupSource Source { get; init; }

    /// <summary>Включена ли запись. null — состояние определить нельзя (RunOnce, задачи планировщика).</summary>
    public bool? Enabled { get; init; }

    /// <summary>Где хранится флаг включения (StartupApproved). null — переключение не поддерживается.</summary>
    public StartupApprovedLocation? Approved { get; init; }

    public string? Publisher { get; init; }

    /// <summary>Похоже на системный/драйверный компонент — отключать с осторожностью.</summary>
    public bool LooksLikeSystemComponent { get; init; }

    public bool CanToggle => Approved is not null;

    public bool RequiresAdmin => Approved?.Hive == RegHive.LocalMachine;

    public string SourceDisplay => Source switch
    {
        StartupSource.HkcuRun => @"HKCU\...\Run",
        StartupSource.HkcuRunOnce => @"HKCU\...\RunOnce",
        StartupSource.HklmRun => @"HKLM\...\Run",
        StartupSource.HklmRunOnce => @"HKLM\...\RunOnce",
        StartupSource.HklmRun32 => @"HKLM\WOW6432Node\...\Run",
        StartupSource.HklmRunOnce32 => @"HKLM\WOW6432Node\...\RunOnce",
        StartupSource.UserStartupFolder => Loc.T("Папка автозагрузки пользователя", "User Startup folder"),
        StartupSource.CommonStartupFolder => Loc.T("Папка автозагрузки (все пользователи)", "Common Startup folder"),
        StartupSource.ScheduledTaskLogon => Loc.T("Планировщик: при входе", "Task Scheduler: at logon"),
        _ => Source.ToString(),
    };
}

/// <summary>Расположение значения в ключе ...\Explorer\StartupApproved\... для конкретной записи.</summary>
public sealed record StartupApprovedLocation(RegHive Hive, string Key, string ValueName);

/// <summary>
/// Флаг «включено/отключено», который использует диспетчер задач Windows 10.
/// Значение REG_BINARY из 12 байт: первый байт чётный (0x02, 0x06) — включено, нечётный (0x03, 0x07) — отключено;
/// байты 4–11 — FILETIME момента отключения. Отсутствие значения означает «включено».
/// </summary>
public static class StartupApproved
{
    public const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static string RunKey => BaseKey + @"\Run";

    public static string Run32Key => BaseKey + @"\Run32";

    public static string StartupFolderKey => BaseKey + @"\StartupFolder";

    public static bool IsEnabled(RegValue? value)
    {
        if (value?.Bytes is not { Length: > 0 } bytes || value.Kind != RegKind.Binary)
        {
            return true;
        }

        return (bytes[0] & 1) == 0;
    }

    public static DateTime? DisabledAtUtc(RegValue? value)
    {
        if (value?.Bytes is not { Length: >= 12 } bytes || IsEnabled(value))
        {
            return null;
        }

        var fileTime = BitConverter.ToInt64(bytes, 4);
        return fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : null;
    }

    public static RegValue Enabled() => RegValue.Binary([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    public static RegValue Disabled(DateTime utcNow)
    {
        var bytes = new byte[12];
        bytes[0] = 0x03;
        BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(bytes, 4);
        return RegValue.Binary(bytes);
    }

    /// <summary>Где хранится флаг для источника; null — источник не поддерживает переключение.</summary>
    public static StartupApprovedLocation? LocationFor(StartupSource source, string valueName) => source switch
    {
        StartupSource.HkcuRun => new(RegHive.CurrentUser, RunKey, valueName),
        StartupSource.HklmRun => new(RegHive.LocalMachine, RunKey, valueName),
        StartupSource.HklmRun32 => new(RegHive.LocalMachine, Run32Key, valueName),
        StartupSource.UserStartupFolder => new(RegHive.CurrentUser, StartupFolderKey, valueName),
        StartupSource.CommonStartupFolder => new(RegHive.LocalMachine, StartupFolderKey, valueName),
        _ => null,
    };

    /// <summary>Изменение для включения/отключения записи — применяется через бэкап, как любая оптимизация.</summary>
    public static RegistryChange PlanToggle(StartupEntry entry, bool enable, DateTime utcNow)
    {
        var location = entry.Approved ?? throw new InvalidOperationException(Loc.T("Эту запись нельзя переключить.", "This entry cannot be toggled."));
        return new RegistryChange(location.Hive, location.Key, location.ValueName, enable ? Enabled() : Disabled(utcNow))
        {
            Title = (enable ? Loc.T("Включить автозапуск: ", "Enable startup: ") : Loc.T("Отключить автозапуск: ", "Disable startup: ")) + entry.Name,
            RequiresAdmin = location.Hive == RegHive.LocalMachine,
        };
    }

    /// <summary>Эвристика «похоже на системный компонент»: драйверы и службы производителей оборудования, Защитник, звук.</summary>
    public static bool LooksLikeSystemComponent(string name, string command)
    {
        string[] markers =
        [
            "securityhealth", "windowsdefender", "defender", "realtek", "rtkaud", "rthdvcpl", "nvidia", "nvcpl", "nvbackend",
            "amd", "radeon", "igfx", "intel", "synaptics", "elan", "audio", "driver", "\\windows\\system32\\",
        ];
        var text = (name + " " + command).ToLowerInvariant();
        return markers.Any(text.Contains);
    }
}
