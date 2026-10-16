using System.Text.Json.Serialization;
using NovaTune.Core.Common;

namespace NovaTune.Core.Backup;

public enum BackupKind
{
    /// <summary>Создан автоматически перед оптимизацией.</summary>
    Automatic,

    /// <summary>Создан вручную по кнопке.</summary>
    Manual,

    /// <summary>Бэкап конфигов игры (CS2).</summary>
    GameConfig,
}

public enum BackupStatus
{
    /// <summary>Операция начата и не завершена — после сбоя предлагаем откат.</summary>
    InProgress,
    Completed,
    PartiallyCompleted,
    Failed,

    /// <summary>Снимок без изменений (ручной бэкап).</summary>
    Snapshot,
    RolledBack,
    PartiallyRolledBack,
}

public enum BackupItemState
{
    Captured,
    Applying,
    Applied,
    Skipped,
    Failed,
    Restored,
    RestoreFailed,
}

/// <summary>Элемент бэкапа: исходное состояние одного объекта до изменения.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RegistryBackupItem), "registry")]
[JsonDerivedType(typeof(FileBackupItem), "file")]
[JsonDerivedType(typeof(PowerSchemeBackupItem), "power")]
public abstract class BackupItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public string Target { get; set; } = "";

    public string OriginalDisplay { get; set; } = "";

    public string? NewDisplay { get; set; }

    public bool RequiresAdmin { get; set; }

    public bool RequiresRestart { get; set; }

    public BackupItemState State { get; set; } = BackupItemState.Captured;

    public string? Error { get; set; }
}

public sealed class RegistryBackupItem : BackupItem
{
    public RegHive Hive { get; set; }

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Существовал ли ключ до изменения: если нет — при откате удаляем созданный приложением пустой ключ.</summary>
    public bool KeyExisted { get; set; }

    /// <summary>Исходное значение; null — значения не было, при откате его нужно удалить.</summary>
    public RegValue? Original { get; set; }
}

public sealed class FileBackupItem : BackupItem
{
    public string OriginalPath { get; set; } = "";

    public bool Existed { get; set; }

    public long Size { get; set; }

    public DateTime? LastWriteUtc { get; set; }

    public string? Sha256 { get; set; }

    /// <summary>Имя копии внутри папки бэкапа (files\...).</summary>
    public string? StoredFile { get; set; }
}

public sealed class PowerSchemeBackupItem : BackupItem
{
    public Guid? PreviousActive { get; set; }

    public string? PreviousActiveName { get; set; }

    /// <summary>Схема, созданная приложением (её удаляем при откате).</summary>
    public Guid? CreatedScheme { get; set; }
}

public sealed class BackupRecord
{
    public string Id { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public BackupKind Kind { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Метка для фильтрации (например, «game:730» — бэкапы конфигов конкретной игры).</summary>
    public string? Tag { get; set; }

    public BackupStatus Status { get; set; }

    public bool RequiresRestart { get; set; }

    public List<BackupItem> Items { get; set; } = [];

    /// <summary>Папка бэкапа (не сериализуется — определяется при загрузке).</summary>
    [JsonIgnore]
    public string Directory { get; set; } = "";
}

/// <summary>Итог применения набора изменений.</summary>
public sealed record TransactionResult(
    BackupRecord Backup,
    int Applied,
    int Skipped,
    IReadOnlyList<(string Title, string Error)> Failures,
    bool RequiresRestart)
{
    public bool Success => Failures.Count == 0;
}

public sealed record RestoreResult(
    int Restored,
    IReadOnlyList<(string Title, string Error)> Failures,
    bool RequiresRestart)
{
    public bool Success => Failures.Count == 0;
}
