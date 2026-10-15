using System.Globalization;
using System.Net;
using System.Text;
using NovaTune.Core.Analysis;
using NovaTune.Core.Backup;
using NovaTune.Core.Frametime;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;

namespace NovaTune.Core.Reports;

public sealed record ReportData(
    HardwareReport? Hardware,
    HealthResult? Health,
    IReadOnlyList<InfoSection> Diagnostics,
    IReadOnlyList<JournalEntry> Journal,
    FrametimeComparison? Frametime,
    string AppVersion,
    DateTimeOffset CreatedAt);

/// <summary>Отчёт о ПК и выполненных операциях в TXT и HTML (HTML самодостаточный, без внешних ресурсов).</summary>
public static class ReportBuilder
{
    public static string BuildText(ReportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"NOVA TUNE {data.AppVersion} — {Loc.T("отчёт", "report")} {data.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        sb.AppendLine(new string('=', 60));

        if (data.Health is { } h)
        {
            sb.AppendLine();
            sb.AppendLine($"{Loc.T("Оценка состояния", "Health score")}: {h.Score}/100 — {h.StatusText}");
            foreach (var c in h.Criteria)
            {
                sb.AppendLine($"  [{c.Earned}/{c.Max}] {c.Title}: {c.Detail}{(c.Recommendation is null ? "" : " → " + c.Recommendation)}");
            }
        }

        if (data.Hardware is { } hw)
        {
            sb.AppendLine();
            sb.Append(hw.ToPlainText());
        }

        foreach (var section in data.Diagnostics)
        {
            sb.AppendLine();
            sb.AppendLine($"== {section.Title} ==");
            foreach (var item in section.Items)
            {
                sb.AppendLine($"{item.Label}: {item.DisplayValue}");
            }
        }

        if (data.Frametime is { } ft)
        {
            sb.AppendLine();
            sb.AppendLine($"== {Loc.T("Сравнение FPS", "FPS comparison")} ==");
            foreach (var (label, before, after) in FrametimeRows(ft))
            {
                sb.AppendLine($"{label}: {before} → {after}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"== {Loc.T("Журнал операций", "Operation log")} ==");
        if (data.Journal.Count == 0)
        {
            sb.AppendLine(Loc.T("Операций не было.", "No operations."));
        }

        foreach (var e in data.Journal)
        {
            sb.AppendLine($"{e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [{(e.Success ? "OK" : "FAIL")}] {e.Title}{(e.Details is null ? "" : " — " + e.Details)}");
        }

        return sb.ToString();
    }

    public static string BuildHtml(ReportData data)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(Loc.IsEnglish ? "en" : "ru").Append("\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>NOVA TUNE — ").Append(E(Loc.T("Отчёт", "Report"))).Append("</title><style>");
        sb.Append("""
            :root{--bg:#0f1115;--card:#171a21;--text:#e8ebf2;--muted:#9aa3b5;--line:#272c37;--accent:#5b8cff;--good:#3ecf8e;--warn:#f5b84b;--bad:#ff6b6b}
            @media (prefers-color-scheme: light){:root{--bg:#f4f6fa;--card:#fff;--text:#161a22;--muted:#5d6678;--line:#e1e5ee}}
            body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
            main{max-width:960px;margin:0 auto;padding:24px 16px}
            h1{font-size:22px;margin:0 0 4px} h2{font-size:16px;margin:0 0 12px} .muted{color:var(--muted)}
            section{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:16px;margin:16px 0}
            table{width:100%;border-collapse:collapse} td,th{padding:6px 8px;border-top:1px solid var(--line);text-align:left;vertical-align:top}
            td:first-child{color:var(--muted);width:38%} .ok{color:var(--good)} .fail{color:var(--bad)} .warn{color:var(--warn)}
            .score{font-size:40px;font-weight:600}
            """);
        sb.Append("</style></head><body><main>");
        sb.Append("<h1>NOVA TUNE</h1><div class=\"muted\">").Append(E($"{data.AppVersion} · {data.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}")).Append("</div>");

        if (data.Health is { } h)
        {
            sb.Append("<section><h2>").Append(E(Loc.T("Оценка состояния", "Health score"))).Append("</h2>");
            sb.Append("<div class=\"score\">").Append(h.Score).Append("<span class=\"muted\" style=\"font-size:16px\">/100 · ").Append(E(h.StatusText)).Append("</span></div><table>");
            foreach (var c in h.Criteria)
            {
                var cls = c.Level switch { CriterionLevel.Good => "ok", CriterionLevel.Warning => "warn", _ => "fail" };
                sb.Append("<tr><td>").Append(E(c.Title)).Append("</td><td><span class=\"").Append(cls).Append("\">").Append(c.Earned).Append('/').Append(c.Max).Append("</span> ")
                  .Append(E(c.Detail));
                if (c.Recommendation is not null)
                {
                    sb.Append("<br><span class=\"muted\">").Append(E(c.Recommendation)).Append("</span>");
                }

                sb.Append("</td></tr>");
            }

            sb.Append("</table></section>");
        }

        if (data.Hardware is { } hw)
        {
            foreach (var section in hw.Sections)
            {
                AppendSectionHtml(sb, section, E);
            }
        }

        foreach (var section in data.Diagnostics)
        {
            AppendSectionHtml(sb, section, E);
        }

        if (data.Frametime is { } ft)
        {
            sb.Append("<section><h2>").Append(E(Loc.T("Сравнение FPS", "FPS comparison"))).Append("</h2><table>");
            foreach (var (label, before, after) in FrametimeRows(ft))
            {
                sb.Append("<tr><td>").Append(E(label)).Append("</td><td>").Append(E(before)).Append(" → ").Append(E(after)).Append("</td></tr>");
            }

            sb.Append("</table></section>");
        }

        sb.Append("<section><h2>").Append(E(Loc.T("Журнал операций", "Operation log"))).Append("</h2><table>");
        if (data.Journal.Count == 0)
        {
            sb.Append("<tr><td colspan=\"2\">").Append(E(Loc.T("Операций не было.", "No operations."))).Append("</td></tr>");
        }

        foreach (var e in data.Journal)
        {
            sb.Append("<tr><td>").Append(E(e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append("</td><td><span class=\"")
              .Append(e.Success ? "ok\">OK" : "fail\">FAIL").Append("</span> ").Append(E(e.Title));
            if (e.Details is not null)
            {
                sb.Append("<br><span class=\"muted\">").Append(E(e.Details)).Append("</span>");
            }

            sb.Append("</td></tr>");
        }

        sb.Append("</table></section></main></body></html>");
        return sb.ToString();
    }

    private static void AppendSectionHtml(StringBuilder sb, InfoSection section, Func<string?, string> e)
    {
        sb.Append("<section><h2>").Append(e(section.Title)).Append("</h2><table>");
        AppendRows(sb, section, e);
        sb.Append("</table></section>");
    }

    private static void AppendRows(StringBuilder sb, InfoSection section, Func<string?, string> e)
    {
        foreach (var item in section.Items)
        {
            sb.Append("<tr><td>").Append(e(item.Label)).Append("</td><td")
              .Append(item.IsAvailable ? ">" : " class=\"muted\">").Append(e(item.DisplayValue)).Append("</td></tr>");
        }

        foreach (var child in section.Children ?? [])
        {
            sb.Append("<tr><th colspan=\"2\">").Append(e(child.Title)).Append("</th></tr>");
            AppendRows(sb, child, e);
        }
    }

    public static IReadOnlyList<(string Label, string Before, string After)> FrametimeRows(FrametimeComparison c)
    {
        static string Fps(double v) => v.ToString("0.0", CultureInfo.CurrentCulture);
        static string Ms(double v) => v.ToString("0.00", CultureInfo.CurrentCulture) + " ms";
        static string D(double before, double after) => $" ({FrametimeComparison.DeltaPercent(before, after):+0.0;-0.0;0}%)";
        return
        [
            (Loc.T("Средний FPS", "Average FPS"), Fps(c.Before.AverageFps), Fps(c.After.AverageFps) + D(c.Before.AverageFps, c.After.AverageFps)),
            ("1% low", Fps(c.Before.OnePercentLowFps), Fps(c.After.OnePercentLowFps) + D(c.Before.OnePercentLowFps, c.After.OnePercentLowFps)),
            ("0.1% low", Fps(c.Before.PointOnePercentLowFps), Fps(c.After.PointOnePercentLowFps) + D(c.Before.PointOnePercentLowFps, c.After.PointOnePercentLowFps)),
            (Loc.T("Frametime 99-й перцентиль", "Frametime 99th percentile"), Ms(c.Before.P99FrametimeMs), Ms(c.After.P99FrametimeMs)),
            (Loc.T("Разброс frametime (σ)", "Frametime spread (σ)"), Ms(c.Before.StdDevFrametimeMs), Ms(c.After.StdDevFrametimeMs)),
            (Loc.T("Кадров / длительность", "Frames / duration"), $"{c.Before.Frames} / {c.Before.DurationSeconds:0} s", $"{c.After.Frames} / {c.After.DurationSeconds:0} s"),
        ];
    }
}
