using System.Globalization;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;

namespace NovaTune.Core.Analysis;

/// <summary>Факты о системе для оценки. null — факт неизвестен, критерий не учитывается (а не считается плохим или хорошим).</summary>
public sealed record SystemFacts
{
    public VolumeSummary? SystemVolume { get; init; }

    public double? RamUsedPercent { get; init; }

    /// <summary>Средняя загрузка CPU за последнюю минуту (фоновая нагрузка).</summary>
    public double? CpuLoadAverage { get; init; }

    public int? EnabledStartupCount { get; init; }

    public bool? GameModeRecommended { get; init; }

    public bool? GameDvrOff { get; init; }

    public bool? PowerSaverActive { get; init; }

    public bool? PendingReboot { get; init; }

    public DateTime? GpuDriverDate { get; init; }

    public bool IsLaptop { get; init; }
}

public enum CriterionLevel
{
    Good,
    Warning,
    Bad,
}

public sealed record HealthCriterion(string Id, string Title, int Earned, int Max, CriterionLevel Level, string Detail, string? Recommendation);

public sealed record HealthResult(int Score, string StatusText, CriterionLevel Level, IReadOnlyList<HealthCriterion> Criteria, IReadOnlyList<string> NotEvaluated);

/// <summary>
/// Общая оценка состояния по прозрачным правилам: каждый критерий даёт баллы, итог — доля набранных
/// от максимума по известным критериям. Пороги подобраны консервативно и описаны в тексте критерия.
/// </summary>
public static class HealthScore
{
    public static HealthResult Evaluate(SystemFacts facts, DateTime now)
    {
        var criteria = new List<HealthCriterion>();
        var missing = new List<string>();

        if (facts.SystemVolume is { TotalBytes: > 0 } vol)
        {
            var freePct = 100.0 * vol.FreeBytes / vol.TotalBytes;
            var (pts, level) = freePct switch
            {
                >= 20 => (25, CriterionLevel.Good),
                >= 10 => (15, CriterionLevel.Warning),
                >= 5 => (5, CriterionLevel.Bad),
                _ => (0, CriterionLevel.Bad),
            };
            criteria.Add(new HealthCriterion(
                "disk",
                Loc.T($"Свободно на диске {vol.Letter}:", $"Free space on {vol.Letter}:"),
                pts,
                25,
                level,
                Loc.T($"{Units.Bytes(vol.FreeBytes)} из {Units.Bytes(vol.TotalBytes)} ({F(freePct)}%). Норма — от 20%.", $"{Units.Bytes(vol.FreeBytes)} of {Units.Bytes(vol.TotalBytes)} ({F(freePct)}%). Healthy: 20% or more."),
                level == CriterionLevel.Good ? null : Loc.T("Освободите место: очистка временных файлов, удаление ненужных игр.", "Free up space: clean temporary files, uninstall unused games.")));
        }
        else
        {
            missing.Add(Loc.T("Свободное место на системном диске", "System drive free space"));
        }

        if (facts.RamUsedPercent is { } ram)
        {
            var (pts, level) = ram switch
            {
                < 70 => (15, CriterionLevel.Good),
                < 85 => (8, CriterionLevel.Warning),
                _ => (0, CriterionLevel.Bad),
            };
            criteria.Add(new HealthCriterion(
                "ram",
                Loc.T("Загрузка памяти", "Memory usage"),
                pts,
                15,
                level,
                Loc.T($"Занято {F(ram)}%. Норма в простое — до 70%.", $"{F(ram)}% in use. Healthy at idle: below 70%."),
                level == CriterionLevel.Good ? null : Loc.T("Закройте лишние программы перед игрой.", "Close unneeded apps before gaming.")));
        }
        else
        {
            missing.Add(Loc.T("Загрузка памяти", "Memory usage"));
        }

        if (facts.CpuLoadAverage is { } cpu)
        {
            var (pts, level) = cpu switch
            {
                < 15 => (15, CriterionLevel.Good),
                < 35 => (8, CriterionLevel.Warning),
                _ => (0, CriterionLevel.Bad),
            };
            criteria.Add(new HealthCriterion(
                "cpu",
                Loc.T("Фоновая нагрузка CPU", "Background CPU load"),
                pts,
                15,
                level,
                Loc.T($"В среднем {F(cpu)}% за последнюю минуту. Норма в простое — до 15%.", $"Average {F(cpu)}% over the last minute. Healthy at idle: below 15%."),
                level == CriterionLevel.Good ? null : Loc.T("Посмотрите процессы с высокой нагрузкой в разделе Optimize.", "Check high-load processes in Optimize.")));
        }
        else
        {
            missing.Add(Loc.T("Фоновая нагрузка CPU (накапливается в течение минуты после запуска)", "Background CPU load (collected during the first minute)"));
        }

        if (facts.EnabledStartupCount is { } startup)
        {
            var (pts, level) = startup switch
            {
                <= 8 => (15, CriterionLevel.Good),
                <= 15 => (8, CriterionLevel.Warning),
                _ => (3, CriterionLevel.Bad),
            };
            criteria.Add(new HealthCriterion(
                "startup",
                Loc.T("Программы в автозагрузке", "Startup programs"),
                pts,
                15,
                level,
                Loc.T($"Включено записей: {startup}. До 8 — хорошо.", $"Enabled entries: {startup}. Up to 8 is good."),
                level == CriterionLevel.Good ? null : Loc.T("Отключите ненужные записи в разделе Startup.", "Disable unneeded entries in Startup.")));
        }
        else
        {
            missing.Add(Loc.T("Автозагрузка", "Startup"));
        }

        if (facts.GameModeRecommended is { } gm)
        {
            criteria.Add(new HealthCriterion(
                "game-mode",
                Loc.T("Игровой режим", "Game Mode"),
                gm ? 5 : 0,
                5,
                gm ? CriterionLevel.Good : CriterionLevel.Warning,
                gm ? Loc.T("Включён.", "On.") : Loc.T("Выключен.", "Off."),
                gm ? null : Loc.T("Включите в Optimize → Игровой режим.", "Turn on in Optimize → Game Mode.")));
        }

        if (facts.GameDvrOff is { } dvrOff)
        {
            criteria.Add(new HealthCriterion(
                "game-dvr",
                Loc.T("Фоновая запись Game Bar", "Game Bar background recording"),
                dvrOff ? 5 : 2,
                5,
                dvrOff ? CriterionLevel.Good : CriterionLevel.Warning,
                dvrOff ? Loc.T("Выключена.", "Off.") : Loc.T("Включена или не настроена.", "On or not configured."),
                dvrOff ? null : Loc.T("Если не пользуетесь клипами Game Bar — выключите в Optimize.", "If you don't use Game Bar clips, turn it off in Optimize.")));
        }

        if (facts.PowerSaverActive is { } saver)
        {
            var pts = saver ? (facts.IsLaptop ? 5 : 0) : 10;
            criteria.Add(new HealthCriterion(
                "power",
                Loc.T("План электропитания", "Power plan"),
                pts,
                10,
                saver ? CriterionLevel.Warning : CriterionLevel.Good,
                saver ? Loc.T("Активна «Экономия энергии».", "\"Power saver\" is active.") : Loc.T("Не ограничивает производительность.", "Does not limit performance."),
                saver ? Loc.T("Переключите схему в Optimize → План электропитания.", "Switch the plan in Optimize → Power plan.") : null));
        }

        if (facts.PendingReboot is { } reboot)
        {
            criteria.Add(new HealthCriterion(
                "reboot",
                Loc.T("Ожидающая перезагрузка", "Pending restart"),
                reboot ? 0 : 5,
                5,
                reboot ? CriterionLevel.Warning : CriterionLevel.Good,
                reboot ? Loc.T("Windows ждёт перезагрузки для завершения обновлений или установки.", "Windows is waiting for a restart to finish updates or installs.") : Loc.T("Не требуется.", "Not required."),
                reboot ? Loc.T("Перезагрузите ПК перед игрой.", "Restart the PC before gaming.") : null));
        }

        if (facts.GpuDriverDate is { } driverDate)
        {
            var age = now - driverDate;
            var old = age.TotalDays > 365;
            criteria.Add(new HealthCriterion(
                "gpu-driver",
                Loc.T("Драйвер видеокарты", "Graphics driver"),
                old ? 2 : 5,
                5,
                old ? CriterionLevel.Warning : CriterionLevel.Good,
                Loc.T($"Дата драйвера: {driverDate:dd.MM.yyyy}.", $"Driver date: {driverDate:yyyy-MM-dd}."),
                old ? Loc.T("Драйверу больше года — обновите его с сайта производителя видеокарты.", "The driver is over a year old — update it from the GPU vendor's website.") : null));
        }

        var max = criteria.Sum(c => c.Max);
        var earned = criteria.Sum(c => c.Earned);
        var score = max == 0 ? 0 : (int)Math.Round(100.0 * earned / max);
        var (status, overall) = score switch
        {
            >= 85 => (Loc.T("Отличное состояние", "Excellent"), CriterionLevel.Good),
            >= 65 => (Loc.T("Хорошее состояние", "Good"), CriterionLevel.Good),
            >= 45 => (Loc.T("Требует внимания", "Needs attention"), CriterionLevel.Warning),
            _ => (Loc.T("Нужны улучшения", "Needs improvement"), CriterionLevel.Bad),
        };

        if (max == 0)
        {
            status = Loc.T("Недостаточно данных", "Not enough data");
            overall = CriterionLevel.Warning;
        }

        return new HealthResult(score, status, overall, criteria, missing);
    }

    private static string F(double v) => v.ToString("0", CultureInfo.CurrentCulture);
}
