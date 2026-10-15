using NovaTune.Core.Common;

namespace NovaTune.Core.Backup;

/// <summary>
/// Одно запланированное изменение системы. Само по себе ничего не меняет:
/// применяет его только <see cref="BackupService"/> после создания бэкапа.
/// </summary>
public abstract record PlannedChange
{
    /// <summary>Что меняется, человеческим языком.</summary>
    public required string Title { get; init; }

    public bool RequiresAdmin { get; init; }

    public bool RequiresRestart { get; init; }

    /// <summary>Точное место изменения (путь в реестре, файл, схема питания).</summary>
    public abstract string TargetDisplay { get; }
}

/// <summary>Запись (или удаление при <see cref="NewValue"/> = null) значения реестра.</summary>
public sealed record RegistryChange(RegHive Hive, string Key, string Name, RegValue? NewValue) : PlannedChange
{
    public override string TargetDisplay => RegPath.Format(Hive, Key, Name);
}

/// <summary>Запись содержимого файла (например, конфиг CS2).</summary>
public sealed record FileWriteChange(string Path, byte[] Content) : PlannedChange
{
    public override string TargetDisplay => Path;
}

/// <summary>Смена активной схемы питания или создание схемы «Максимальная производительность».</summary>
public sealed record PowerSchemeChange(Guid? Target, bool CreateUltimate) : PlannedChange
{
    public override string TargetDisplay => "powercfg: " + (CreateUltimate ? "Ultimate Performance" : Target?.ToString() ?? "");
}

/// <summary>Предпросмотр: что и на что будет изменено.</summary>
public sealed record ChangePreview(
    PlannedChange Change,
    string CurrentDisplay,
    string NewDisplay,
    bool AlreadyInTargetState);
