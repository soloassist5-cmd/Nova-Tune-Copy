using NovaTune.Core.Backup;
using NovaTune.Core.Common;
using NovaTune.Core.Localization;

namespace NovaTune.Core.Optimization;

public enum OptimizationCategory
{
    Gaming,
    Power,
    Graphics,
}

public enum TriState
{
    Unknown,
    No,
    Yes,
}

/// <summary>Факты о системе, от которых зависит доступность и рекомендации модулей.</summary>
public sealed record SystemContext
{
    /// <summary>Номер сборки Windows (например, 19045).</summary>
    public int OsBuild { get; init; }

    public bool IsWindows11 => OsBuild >= 22000;

    public bool IsLaptop { get; init; }

    /// <summary>Ноутбук сейчас работает от батареи.</summary>
    public bool OnBattery { get; init; }

    /// <summary>Поддерживает ли драйвер видеокарты HAGS (по данным D3DKMT).</summary>
    public TriState HagsSupported { get; init; }

    /// <summary>Включён ли HAGS фактически (действующее состояние драйвера, а не только реестр).</summary>
    public TriState HagsEnabled { get; init; }
}

public sealed record ModuleAvailability(bool IsAvailable, string? Reason)
{
    public static readonly ModuleAvailability Available = new(true, null);

    public static ModuleAvailability Unavailable(string reason) => new(false, reason);
}

/// <summary>Текущее состояние, прочитанное из системы.</summary>
public sealed record ModuleState(string CurrentDisplay, bool IsRecommendedState, string? Note = null);

/// <summary>Вариант действия модуля (у переключателей их два, у плана питания — по числу схем).</summary>
public sealed record ModuleOption(string Id, string Title, bool IsRecommended, string? Warning = null);

/// <summary>
/// Модуль оптимизации. Сам ничего не меняет: только читает состояние и строит список изменений,
/// которые потом проходят через предпросмотр, бэкап, подтверждение и проверку.
/// </summary>
public interface IOptimizationModule
{
    string Id { get; }

    LocText Title { get; }

    LocText Description { get; }

    LocText Benefit { get; }

    LocText Risks { get; }

    OptimizationCategory Category { get; }

    bool RequiresAdmin { get; }

    bool RequiresRestart { get; }

    bool Reversible { get; }

    /// <summary>Входит ли в режим Safe Optimize (только понятные обратимые изменения без прав администратора).</summary>
    bool IncludedInSafeOptimize { get; }

    ModuleAvailability CheckAvailability(SystemContext context);

    ModuleState ReadState(ISystemSettingsStore store, SystemContext context);

    IReadOnlyList<ModuleOption> GetOptions(ISystemSettingsStore store, SystemContext context);

    IReadOnlyList<PlannedChange> Plan(ISystemSettingsStore store, SystemContext context, string optionId);

    /// <summary>Цели для ручного бэкапа: всё, что модуль может изменить.</summary>
    IReadOnlyList<PlannedChange> BackupTargets(ISystemSettingsStore store, SystemContext context);
}
