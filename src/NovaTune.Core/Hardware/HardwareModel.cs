using System.Globalization;
using System.Text;
using NovaTune.Core.Localization;

namespace NovaTune.Core.Hardware;

/// <summary>Одна строка характеристик. Если значения нет — <see cref="Value"/> пуст, а <see cref="UnavailableReason"/> объясняет почему.</summary>
public sealed record InfoItem(string Label, string? Value, string? UnavailableReason = null)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Value);

    public string DisplayValue => IsAvailable ? Value! : Loc.T("Недоступно", "Unavailable") + (UnavailableReason is null ? "" : $" — {UnavailableReason}");

    public static InfoItem Of(string label, string? value, string reasonIfMissing) =>
        string.IsNullOrWhiteSpace(value) ? new InfoItem(label, null, reasonIfMissing) : new InfoItem(label, value.Trim());
}

/// <summary>Раздел характеристик (Процессор, Видеокарты, …). Подразделы — например, отдельные видеокарты или диски.</summary>
public sealed record InfoSection(string Id, string Title, IReadOnlyList<InfoItem> Items, IReadOnlyList<InfoSection>? Children = null);

public sealed record GpuSummary(string Name, ulong? VramBytes, bool IsIntegrated, string? DriverVersion, DateTime? DriverDate);

public sealed record DiskSummary(string Name, string MediaType, string BusType, ulong? SizeBytes, string Health, IReadOnlyList<char> DriveLetters, bool? IsSsd);

public sealed record VolumeSummary(char Letter, string? Label, ulong TotalBytes, ulong FreeBytes, bool? IsSsd, string? FileSystem)
{
    public double UsedPercent => TotalBytes == 0 ? 0 : 100.0 * (TotalBytes - FreeBytes) / TotalBytes;
}

public sealed record MonitorSummary(string Name, int Width, int Height, int RefreshHz, bool IsPrimary);

/// <summary>Сводные характеристики для дашборда, рекомендаций CS2 и анализа.</summary>
public sealed record HardwareSummary
{
    public string DeviceName { get; init; } = Environment.MachineName;

    public string? CpuName { get; init; }

    public int? CpuCores { get; init; }

    public int? CpuThreads { get; init; }

    public int? CpuBaseMhz { get; init; }

    public ulong? RamTotalBytes { get; init; }

    public int? RamSpeedMhz { get; init; }

    public IReadOnlyList<GpuSummary> Gpus { get; init; } = [];

    public IReadOnlyList<DiskSummary> Disks { get; init; } = [];

    public IReadOnlyList<VolumeSummary> Volumes { get; init; } = [];

    public IReadOnlyList<MonitorSummary> Monitors { get; init; } = [];

    public bool IsLaptop { get; init; }

    public bool OnBattery { get; init; }

    public int OsBuild { get; init; }

    public string? OsName { get; init; }

    public char SystemDrive { get; init; } = 'C';

    public GpuSummary? PrimaryGpu => Gpus.FirstOrDefault(g => !g.IsIntegrated) ?? Gpus.FirstOrDefault();

    public VolumeSummary? SystemVolume => Volumes.FirstOrDefault(v => char.ToUpperInvariant(v.Letter) == char.ToUpperInvariant(SystemDrive));

    public int? MaxRefreshHz => Monitors.Count == 0 ? null : Monitors.Max(m => m.RefreshHz);
}

public sealed record HardwareReport(HardwareSummary Summary, IReadOnlyList<InfoSection> Sections, DateTimeOffset CollectedAt)
{
    /// <summary>Текст для вставки на форум или в чат.</summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();
        sb.Append("NOVA TUNE — ").Append(Loc.T("сведения о ПК", "PC information")).Append(" (")
          .Append(CollectedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).AppendLine(")");
        foreach (var section in Sections)
        {
            AppendSection(sb, section, 0);
        }

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, InfoSection section, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine();
        sb.Append(indent).Append(depth == 0 ? "== " : "-- ").Append(section.Title).AppendLine(depth == 0 ? " ==" : "");
        foreach (var item in section.Items)
        {
            sb.Append(indent).Append(item.Label).Append(": ").AppendLine(item.DisplayValue);
        }

        foreach (var child in section.Children ?? [])
        {
            AppendSection(sb, child, depth + 1);
        }
    }
}

public static class Units
{
    public static string Bytes(double bytes)
    {
        string[] units = Loc.IsEnglish ? ["B", "KB", "MB", "GB", "TB"] : ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }

        return bytes.ToString(i == 0 ? "0" : bytes >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[i];
    }

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1
            ? Loc.T($"{(int)t.TotalDays} д {t.Hours} ч {t.Minutes} мин", $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m")
            : Loc.T($"{t.Hours} ч {t.Minutes} мин", $"{t.Hours}h {t.Minutes}m");
}
