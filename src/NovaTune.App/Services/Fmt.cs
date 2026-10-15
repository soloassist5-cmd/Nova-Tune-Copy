using System.Globalization;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;

namespace NovaTune.App.Services;

/// <summary>Форматирование показаний. Если значения нет — «—», а причина показывается отдельно.</summary>
public static class Fmt
{
    public const string Dash = "—";

    public static string Percent(Reading r) => r.Value is { } v ? v.ToString("0", CultureInfo.CurrentCulture) + "%" : Dash;

    public static string Temp(Reading r) => r.Value is { } v ? v.ToString("0", CultureInfo.CurrentCulture) + " °C" : Dash;

    public static string Mhz(Reading r) => r.Value is { } v ? (v >= 1000 ? (v / 1000).ToString("0.00", CultureInfo.CurrentCulture) + " GHz" : v.ToString("0", CultureInfo.CurrentCulture) + " MHz") : Dash;

    public static string Bytes(Reading r) => r.Value is { } v ? Units.Bytes(v) : Dash;

    public static string? Reason(Reading r) => r.HasValue ? null : r.Reason;

    public static string Ago(DateTimeOffset? time)
    {
        if (time is null)
        {
            return Loc.T("ещё не выполнялось", "never");
        }

        var span = DateTimeOffset.Now - time.Value;
        if (span.TotalMinutes < 1)
        {
            return Loc.T("только что", "just now");
        }

        if (span.TotalHours < 1)
        {
            return Loc.T($"{(int)span.TotalMinutes} мин назад", $"{(int)span.TotalMinutes} min ago");
        }

        return time.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    }
}
