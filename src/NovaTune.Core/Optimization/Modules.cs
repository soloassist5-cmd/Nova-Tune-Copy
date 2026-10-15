using NovaTune.Core.Backup;
using NovaTune.Core.Common;
using NovaTune.Core.Localization;

namespace NovaTune.Core.Optimization;

/// <summary>Ключи и значения реестра, которые использует приложение. Сверены с документацией и поведением Windows 10 22H2.</summary>
public static class KnownKeys
{
    public const string GameBar = @"Software\Microsoft\GameBar";
    public const string AutoGameModeEnabled = "AutoGameModeEnabled";

    public const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string AppCaptureEnabled = "AppCaptureEnabled";

    public const string GameConfigStore = @"System\GameConfigStore";
    public const string GameDvrEnabled = "GameDVR_Enabled";

    public const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public const string HwSchMode = "HwSchMode";

    public const int HagsMinBuild = 19041;
}

/// <summary>Игровой режим Windows (HKCU, без прав администратора).</summary>
public sealed class GameModeModule : IOptimizationModule
{
    public string Id => "game-mode";

    public LocText Title => new("Игровой режим Windows", "Windows Game Mode");

    public LocText Description => new(
        "Windows отдаёт приоритет запущенной игре и откладывает часть фоновых задач (например, установку обновлений драйверов). В Windows 10 включён по умолчанию.",
        "Windows prioritises the running game and defers some background work (such as driver update installs). Enabled by default in Windows 10.");

    public LocText Benefit => new(
        "Меньше вмешательства фоновых задач во время игры. Эффект зависит от системы и может быть незаметен.",
        "Less background interference while gaming. The effect depends on the system and may be unnoticeable.");

    public LocText Risks => new(
        "Практически нет. На некоторых старых системах встречались единичные жалобы на нестабильный FPS — тогда режим можно выключить.",
        "Practically none. A few older systems reported FPS instability — the mode can be turned off then.");

    public OptimizationCategory Category => OptimizationCategory.Gaming;

    public bool RequiresAdmin => false;

    public bool RequiresRestart => false;

    public bool Reversible => true;

    public bool IncludedInSafeOptimize => true;

    public ModuleAvailability CheckAvailability(SystemContext context) =>
        context.OsBuild >= 15063
            ? ModuleAvailability.Available
            : ModuleAvailability.Unavailable(Loc.T("Игровой режим появился в Windows 10 1703.", "Game Mode requires Windows 10 1703."));

    public ModuleState ReadState(ISystemSettingsStore store, SystemContext context)
    {
        var value = store.ReadValue(RegHive.CurrentUser, KnownKeys.GameBar, KnownKeys.AutoGameModeEnabled)?.AsDWord;
        return value switch
        {
            null => new ModuleState(Loc.T("Включён (значение по умолчанию, параметр не задан)", "On (default, value not set)"), true),
            0 => new ModuleState(Loc.T("Выключен", "Off"), false),
            _ => new ModuleState(Loc.T("Включён", "On"), true),
        };
    }

    public IReadOnlyList<ModuleOption> GetOptions(ISystemSettingsStore store, SystemContext context) =>
    [
        new("on", Loc.T("Включить", "Turn on"), true),
        new("off", Loc.T("Выключить", "Turn off"), false),
    ];

    public IReadOnlyList<PlannedChange> Plan(ISystemSettingsStore store, SystemContext context, string optionId) =>
    [
        new RegistryChange(RegHive.CurrentUser, KnownKeys.GameBar, KnownKeys.AutoGameModeEnabled, RegValue.DWord(optionId == "off" ? 0u : 1u))
        {
            Title = optionId == "off" ? Loc.T("Выключить игровой режим", "Turn off Game Mode") : Loc.T("Включить игровой режим", "Turn on Game Mode"),
        },
    ];

    public IReadOnlyList<PlannedChange> BackupTargets(ISystemSettingsStore store, SystemContext context) => Plan(store, context, "on");
}

/// <summary>Фоновая запись Xbox Game Bar / Game DVR (HKCU, без прав администратора).</summary>
public sealed class GameDvrModule : IOptimizationModule
{
    public string Id => "game-dvr";

    public LocText Title => new("Фоновая запись Xbox Game Bar", "Xbox Game Bar background recording");

    public LocText Description => new(
        "Функция «Записать то, что было» постоянно пишет видео игры в буфер, чтобы можно было сохранить последние секунды. Выключение отключает только запись, сам Game Bar остаётся.",
        "\"Record what happened\" continuously captures gameplay into a buffer. Turning it off disables capture only; Game Bar itself stays.");

    public LocText Benefit => new(
        "Постоянная фоновая запись нагружает GPU-кодировщик и диск; без неё этой нагрузки нет.",
        "Continuous background capture uses the GPU encoder and disk; without it that load is gone.");

    public LocText Risks => new(
        "Перестанут работать запись клипов через Game Bar (Win+Alt+G) и фоновая запись. Другие программы записи (OBS, ShadowPlay) не затрагиваются.",
        "Game Bar clip capture (Win+Alt+G) and background recording stop working. Other recorders (OBS, ShadowPlay) are not affected.");

    public OptimizationCategory Category => OptimizationCategory.Gaming;

    public bool RequiresAdmin => false;

    public bool RequiresRestart => false;

    public bool Reversible => true;

    public bool IncludedInSafeOptimize => true;

    public ModuleAvailability CheckAvailability(SystemContext context) => ModuleAvailability.Available;

    public ModuleState ReadState(ISystemSettingsStore store, SystemContext context)
    {
        var capture = store.ReadValue(RegHive.CurrentUser, KnownKeys.GameDvr, KnownKeys.AppCaptureEnabled)?.AsDWord;
        var dvr = store.ReadValue(RegHive.CurrentUser, KnownKeys.GameConfigStore, KnownKeys.GameDvrEnabled)?.AsDWord;

        // Рекомендуемым считаем только явно выключенное состояние обоих параметров;
        // отсутствие значений трактуем как «не настроено», не гадая о значениях по умолчанию.
        var off = capture == 0 && dvr == 0;
        var text = Loc.T(
            $"AppCaptureEnabled = {Show(capture)}, GameDVR_Enabled = {Show(dvr)}",
            $"AppCaptureEnabled = {Show(capture)}, GameDVR_Enabled = {Show(dvr)}");
        return new ModuleState(
            (off ? Loc.T("Выключена. ", "Off. ") : Loc.T("Включена или не настроена. ", "On or not configured. ")) + text,
            off);
    }

    private static string Show(uint? v) => v?.ToString() ?? Loc.T("не задан", "not set");

    public IReadOnlyList<ModuleOption> GetOptions(ISystemSettingsStore store, SystemContext context) =>
    [
        new("off", Loc.T("Выключить фоновую запись", "Turn off background recording"), true),
        new("on", Loc.T("Включить запись", "Turn on recording"), false),
    ];

    public IReadOnlyList<PlannedChange> Plan(ISystemSettingsStore store, SystemContext context, string optionId)
    {
        var value = optionId == "on" ? 1u : 0u;
        return
        [
            new RegistryChange(RegHive.CurrentUser, KnownKeys.GameDvr, KnownKeys.AppCaptureEnabled, RegValue.DWord(value))
            {
                Title = Loc.T("Фоновая запись Game Bar (AppCaptureEnabled)", "Game Bar background capture (AppCaptureEnabled)"),
            },
            new RegistryChange(RegHive.CurrentUser, KnownKeys.GameConfigStore, KnownKeys.GameDvrEnabled, RegValue.DWord(value))
            {
                Title = Loc.T("Game DVR (GameDVR_Enabled)", "Game DVR (GameDVR_Enabled)"),
            },
        ];
    }

    public IReadOnlyList<PlannedChange> BackupTargets(ISystemSettingsStore store, SystemContext context) => Plan(store, context, "off");
}

/// <summary>Аппаратное ускорение планирования GPU (HKLM, права администратора, перезагрузка).</summary>
public sealed class HagsModule : IOptimizationModule
{
    public string Id => "hags";

    public LocText Title => new("Аппаратное ускорение планирования GPU (HAGS)", "Hardware-accelerated GPU scheduling (HAGS)");

    public LocText Description => new(
        "Видеокарта сама управляет своей очередью задач вместо Windows. Нужны Windows 10 2004+ и поддержка драйвером.",
        "The GPU manages its own work queue instead of Windows. Requires Windows 10 2004+ and driver support.");

    public LocText Benefit => new(
        "На части систем немного снижает задержку и нагрузку на CPU, на других — без разницы или хуже. Проверяйте замером FPS/frametime до и после.",
        "On some systems slightly reduces latency and CPU load, on others it makes no difference or is worse. Verify with FPS/frametime logs before and after.");

    public LocText Risks => new(
        "Изменение вступает в силу только после перезагрузки. Со старыми драйверами возможны артефакты или нестабильность — тогда верните прежнее значение.",
        "Takes effect only after a restart. Older drivers may show artefacts or instability — revert in that case.");

    public OptimizationCategory Category => OptimizationCategory.Graphics;

    public bool RequiresAdmin => true;

    public bool RequiresRestart => true;

    public bool Reversible => true;

    public bool IncludedInSafeOptimize => false;

    public ModuleAvailability CheckAvailability(SystemContext context)
    {
        if (context.OsBuild < KnownKeys.HagsMinBuild)
        {
            return ModuleAvailability.Unavailable(Loc.T("Нужна Windows 10 версии 2004 (сборка 19041) или новее.", "Requires Windows 10 version 2004 (build 19041) or newer."));
        }

        if (context.HagsSupported == TriState.No)
        {
            return ModuleAvailability.Unavailable(Loc.T("Драйвер видеокарты не поддерживает HAGS.", "The graphics driver does not support HAGS."));
        }

        return ModuleAvailability.Available;
    }

    public ModuleState ReadState(ISystemSettingsStore store, SystemContext context)
    {
        var value = store.ReadValue(RegHive.LocalMachine, KnownKeys.GraphicsDrivers, KnownKeys.HwSchMode)?.AsDWord;
        var setting = value switch
        {
            2 => Loc.T("включено в реестре (HwSchMode = 2)", "enabled in registry (HwSchMode = 2)"),
            1 => Loc.T("выключено в реестре (HwSchMode = 1)", "disabled in registry (HwSchMode = 1)"),
            null => Loc.T("не задано (решает драйвер)", "not set (driver default)"),
            _ => $"HwSchMode = {value}",
        };
        var actual = context.HagsEnabled switch
        {
            TriState.Yes => Loc.T("; сейчас активно", "; currently active"),
            TriState.No => Loc.T("; сейчас не активно", "; currently inactive"),
            _ => "",
        };
        var support = context.HagsSupported == TriState.Unknown
            ? Loc.T("Поддержку драйвером определить не удалось.", "Driver support could not be determined.")
            : null;

        // Однозначно «правильного» состояния нет — поэтому модуль никогда не помечается как требующий действия.
        return new ModuleState(char.ToUpperInvariant(setting[0]) + setting[1..] + actual, true, support);
    }

    public IReadOnlyList<ModuleOption> GetOptions(ISystemSettingsStore store, SystemContext context) =>
    [
        new("enable", Loc.T("Включить (HwSchMode = 2)", "Enable (HwSchMode = 2)"), false,
            Loc.T("Требует перезагрузки. Сравните FPS до и после.", "Requires restart. Compare FPS before and after.")),
        new("disable", Loc.T("Выключить (HwSchMode = 1)", "Disable (HwSchMode = 1)"), false,
            Loc.T("Требует перезагрузки.", "Requires restart.")),
    ];

    public IReadOnlyList<PlannedChange> Plan(ISystemSettingsStore store, SystemContext context, string optionId) =>
    [
        new RegistryChange(RegHive.LocalMachine, KnownKeys.GraphicsDrivers, KnownKeys.HwSchMode, RegValue.DWord(optionId == "enable" ? 2u : 1u))
        {
            Title = optionId == "enable" ? Loc.T("Включить HAGS", "Enable HAGS") : Loc.T("Выключить HAGS", "Disable HAGS"),
            RequiresAdmin = true,
            RequiresRestart = true,
        },
    ];

    public IReadOnlyList<PlannedChange> BackupTargets(ISystemSettingsStore store, SystemContext context) => Plan(store, context, "enable");
}

/// <summary>Схема электропитания (без прав администратора; «Максимальная производительность» — только по явному выбору).</summary>
public sealed class PowerPlanModule : IOptimizationModule
{
    private const string UltimateOption = "create-ultimate";

    public string Id => "power-plan";

    public LocText Title => new("План электропитания", "Power plan");

    public LocText Description => new(
        "Определяет, как быстро процессор повышает частоту и уходит в энергосбережение.",
        "Controls how quickly the CPU ramps up clocks and enters power-saving states.");

    public LocText Benefit => new(
        "«Высокая производительность» может уменьшить задержки повышения частоты. На современных CPU разница со «Сбалансированной» часто минимальна.",
        "\"High performance\" can reduce clock ramp-up delays. On modern CPUs the difference from \"Balanced\" is often minimal.");

    public LocText Risks => new(
        "Выше энергопотребление и нагрев в простое; на ноутбуке быстрее разряжается батарея.",
        "Higher idle power use and heat; on a laptop the battery drains faster.");

    public OptimizationCategory Category => OptimizationCategory.Power;

    public bool RequiresAdmin => false;

    public bool RequiresRestart => false;

    public bool Reversible => true;

    public bool IncludedInSafeOptimize => false;

    public ModuleAvailability CheckAvailability(SystemContext context) => ModuleAvailability.Available;

    public ModuleState ReadState(ISystemSettingsStore store, SystemContext context)
    {
        var active = store.GetActivePowerScheme();
        var schemes = store.GetPowerSchemes();
        var name = schemes.FirstOrDefault(s => s.Id == active)?.Name ?? Loc.T("неизвестно", "unknown");
        var isSaver = active == PowerSchemeInfo.PowerSaver;
        string? note = isSaver
            ? Loc.T("«Экономия энергии» ограничивает производительность в играх.", "\"Power saver\" limits gaming performance.")
            : null;
        return new ModuleState(name, !isSaver, note);
    }

    public IReadOnlyList<ModuleOption> GetOptions(ISystemSettingsStore store, SystemContext context)
    {
        var active = store.GetActivePowerScheme();
        var options = store.GetPowerSchemes()
            .Where(s => s.Id != active)
            .Select(s => new ModuleOption(
                s.Id.ToString(),
                s.Name,
                IsRecommended: !context.IsLaptop && s.Id == PowerSchemeInfo.HighPerformance,
                Warning: context.IsLaptop && s.Id != PowerSchemeInfo.Balanced
                    ? Loc.T("На ноутбуке увеличит расход батареи.", "Increases battery drain on a laptop.")
                    : null))
            .ToList();

        // Скрытую схему предлагаем только на настольном ПК и не при работе от батареи.
        if (!context.IsLaptop && !context.OnBattery)
        {
            options.Add(new ModuleOption(
                UltimateOption,
                Loc.T("Создать «Максимальная производительность»", "Create \"Ultimate Performance\""),
                false,
                Loc.T("Создаёт новую схему копированием скрытого шаблона Windows. При откате схема удаляется.", "Creates a new scheme from the hidden Windows template. It is deleted on rollback.")));
        }

        return options;
    }

    public IReadOnlyList<PlannedChange> Plan(ISystemSettingsStore store, SystemContext context, string optionId)
    {
        if (optionId == UltimateOption)
        {
            if (context.OnBattery)
            {
                throw new InvalidOperationException(Loc.T("Ноутбук работает от батареи — схема не создаётся.", "Running on battery — scheme not created."));
            }

            return [new PowerSchemeChange(null, CreateUltimate: true) { Title = Loc.T("Создать и включить «Максимальная производительность»", "Create and activate \"Ultimate Performance\"") }];
        }

        var id = Guid.Parse(optionId);
        var name = store.GetPowerSchemes().FirstOrDefault(s => s.Id == id)?.Name ?? optionId;
        return [new PowerSchemeChange(id, CreateUltimate: false) { Title = Loc.T("Включить схему питания: ", "Activate power plan: ") + name }];
    }

    public IReadOnlyList<PlannedChange> BackupTargets(ISystemSettingsStore store, SystemContext context) =>
        store.GetActivePowerScheme() is { } active
            ? [new PowerSchemeChange(active, false) { Title = Loc.T("Активная схема питания", "Active power plan") }]
            : [];
}

public static class OptimizationCatalog
{
    public static IReadOnlyList<IOptimizationModule> CreateAll() =>
    [
        new GameModeModule(),
        new GameDvrModule(),
        new PowerPlanModule(),
        new HagsModule(),
    ];

    /// <summary>Изменения для режима Safe Optimize: только безопасные модули, которые ещё не в рекомендуемом состоянии.</summary>
    public static IReadOnlyList<(IOptimizationModule Module, IReadOnlyList<PlannedChange> Changes)> PlanSafeOptimize(
        IEnumerable<IOptimizationModule> modules, ISystemSettingsStore store, SystemContext context)
    {
        var result = new List<(IOptimizationModule, IReadOnlyList<PlannedChange>)>();
        foreach (var module in modules)
        {
            if (!module.IncludedInSafeOptimize || module.RequiresAdmin || !module.CheckAvailability(context).IsAvailable)
            {
                continue;
            }

            if (module.ReadState(store, context).IsRecommendedState)
            {
                continue;
            }

            var option = module.GetOptions(store, context).FirstOrDefault(o => o.IsRecommended);
            if (option is not null)
            {
                result.Add((module, module.Plan(store, context, option.Id)));
            }
        }

        return result;
    }
}
