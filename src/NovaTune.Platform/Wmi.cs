using System.Management;
using NovaTune.Core.Logging;

namespace NovaTune.Platform;

/// <summary>
/// WMI-запросы с таймаутом. WMI иногда отвечает медленно или «зависает» — поэтому каждый запрос
/// выполняется в фоне с ограничением времени, а ошибка возвращается как пустой результат с записью в лог.
/// </summary>
public static class Wmi
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    public static async Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(
        string scope, string query, ILog log, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            return await Task.Run(() => Query(scope, query, timeout ?? DefaultTimeout), ct)
                .WaitAsync(timeout ?? DefaultTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log.Warn($"WMI: превышено время ожидания ({scope}: {query})");
            return [];
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            log.Warn($"WMI: ошибка запроса ({scope}: {query})", ex);
            return [];
        }
    }

    /// <summary>Синхронный запрос (для фонового кода и повышенного процесса).</summary>
    public static IReadOnlyList<Dictionary<string, object?>> Query(string scope, string query, TimeSpan timeout)
    {
        var options = new System.Management.EnumerationOptions { Timeout = timeout, ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
        using var results = searcher.Get();
        var list = new List<Dictionary<string, object?>>();
        foreach (var obj in results)
        {
            using (obj)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in obj.Properties)
                {
                    row[p.Name] = p.Value;
                }

                list.Add(row);
            }
        }

        return list;
    }

    public static string? Str(this Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null ? v.ToString()?.Trim() : null;

    public static ulong? U64(this Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null ? System.Convert.ToUInt64(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    public static int? I32(this Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null ? System.Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>Дата в формате CIM_DATETIME (yyyymmddHHMMSS.mmmmmmsUUU).</summary>
    public static DateTime? Date(this Dictionary<string, object?> row, string key)
    {
        var s = row.Str(key);
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(s);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
