using System.Globalization;
using NovaTune.Core.Localization;

namespace NovaTune.Core.Frametime;

public sealed record FrametimeStats(
    string Source,
    int Frames,
    double DurationSeconds,
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double P99FrametimeMs,
    double AverageFrametimeMs,
    double StdDevFrametimeMs);

public sealed record FrametimeComparison(FrametimeStats Before, FrametimeStats After)
{
    public static double DeltaPercent(double before, double after) => before == 0 ? 0 : 100.0 * (after - before) / before;
}

/// <summary>
/// Разбор CSV-логов PresentMon и CapFrameX. Используется колонка MsBetweenPresents (PresentMon 1.x, CapFrameX)
/// или FrameTime (PresentMon 2.x). Если в логе несколько процессов — берётся самый частый (игра).
/// </summary>
public static class FrametimeLogParser
{
    private static readonly string[] FrametimeColumns = ["MsBetweenPresents", "FrameTime", "msBetweenPresents"];

    public static FrametimeStats ParseFile(string path) => Parse(File.ReadLines(path), Path.GetFileName(path));

    public static FrametimeStats Parse(IEnumerable<string> lines, string source)
    {
        string[]? header = null;
        var ftIndex = -1;
        var appIndex = -1;
        var values = new List<(string App, double Ms)>();
        var separator = ',';

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (header is null)
            {
                separator = line.Count(c => c == ';') > line.Count(c => c == ',') ? ';' : ',';
                var cols = SplitCsv(line, separator);
                var idx = FrametimeColumns.Select(c => Array.FindIndex(cols, h => h.Equals(c, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);
                if (idx < 0)
                {
                    continue; // в CapFrameX перед заголовком могут быть строки метаданных
                }

                header = cols;
                ftIndex = idx;
                appIndex = Array.FindIndex(cols, h => h.Equals("Application", StringComparison.OrdinalIgnoreCase) || h.Equals("ProcessName", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            var parts = SplitCsv(line, separator);
            if (parts.Length <= ftIndex)
            {
                continue;
            }

            if (double.TryParse(parts[ftIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0 && ms < 10_000)
            {
                values.Add((appIndex >= 0 && appIndex < parts.Length ? parts[appIndex] : "", ms));
            }
        }

        if (header is null)
        {
            throw new FormatException(Loc.T(
                "Не найдена колонка MsBetweenPresents/FrameTime. Поддерживаются CSV PresentMon и CapFrameX.",
                "MsBetweenPresents/FrameTime column not found. PresentMon and CapFrameX CSV files are supported."));
        }

        var mainApp = values.GroupBy(v => v.App).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var frametimes = values.Where(v => v.App == mainApp).Select(v => v.Ms).ToList();
        if (frametimes.Count < 100)
        {
            throw new FormatException(Loc.T($"Слишком мало кадров для статистики: {frametimes.Count} (нужно от 100).", $"Too few frames for statistics: {frametimes.Count} (100+ required)."));
        }

        return Compute(frametimes, source + (string.IsNullOrEmpty(mainApp) ? "" : $" ({mainApp})"));
    }

    /// <summary>
    /// 1% low / 0.1% low — средний FPS по худшим 1% / 0.1% кадров (по длительности кадров), как в CapFrameX.
    /// </summary>
    public static FrametimeStats Compute(IReadOnlyList<double> frametimesMs, string source)
    {
        var total = frametimesMs.Sum();
        var sorted = frametimesMs.OrderByDescending(x => x).ToList();
        double LowFps(double fraction)
        {
            var n = Math.Max(1, (int)Math.Ceiling(sorted.Count * fraction));
            return 1000.0 / sorted.Take(n).Average();
        }

        var ascending = frametimesMs.OrderBy(x => x).ToList();
        var p99Index = Math.Clamp((int)Math.Ceiling(0.99 * ascending.Count) - 1, 0, ascending.Count - 1);
        var mean = frametimesMs.Average();
        var std = Math.Sqrt(frametimesMs.Sum(x => (x - mean) * (x - mean)) / frametimesMs.Count);

        return new FrametimeStats(
            source,
            frametimesMs.Count,
            total / 1000.0,
            1000.0 * frametimesMs.Count / total,
            LowFps(0.01),
            LowFps(0.001),
            ascending[p99Index],
            mean,
            std);
    }

    private static string[] SplitCsv(string line, char separator)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == separator && !inQuotes)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString().Trim());
        return result.ToArray();
    }
}
