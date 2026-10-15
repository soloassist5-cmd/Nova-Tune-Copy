using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.ServiceProcess;
using Microsoft.Win32;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Logging;

namespace NovaTune.Platform;

/// <summary>Путь к Steam из реестра: HKCU\Software\Valve\Steam\SteamPath, запасной — HKLM (WOW6432Node) InstallPath.</summary>
public static class SteamLocator
{
    public static string? FindSteamPath()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
        {
            if (key?.GetValue("InstallPath") is string path && Directory.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }
}

/// <summary>Диагностика Windows: перезагрузка, обновления, журнал событий. Только чтение.</summary>
public sealed class DiagnosticsService(ILog log)
{
    public static bool? PendingReboot()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cbs = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using var wu = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return cbs is not null || wu is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<InfoSection>> CollectAsync(CancellationToken ct)
    {
        var updates = await CollectUpdatesAsync(ct).ConfigureAwait(false);
        var events = await Task.Run(CollectEvents, ct).ConfigureAwait(false);
        return [updates, events];
    }

    private async Task<InfoSection> CollectUpdatesAsync(CancellationToken ct)
    {
        var os = OsInfo.Current;
        var items = new List<InfoItem>
        {
            new(Loc.T("Версия", "Version"), $"{os.Name} {os.DisplayVersion} ({os.FullBuild})"),
            new(Loc.T("Поддержка", "Support"), os.SupportNote()),
        };

        var hotfixes = await Wmi.QueryAsync(@"\\.\root\cimv2", "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering", log, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var latest = hotfixes
            .Select(h => (Id: h.Str("HotFixID"), Date: ParseHotfixDate(h.Str("InstalledOn"))))
            .Where(h => h.Date is not null)
            .OrderByDescending(h => h.Date)
            .FirstOrDefault();
        items.Add(latest.Date is { } d
            ? new InfoItem(Loc.T("Последнее установленное обновление", "Latest installed update"), $"{latest.Id}, {d.ToString("d", CultureInfo.CurrentCulture)}")
            : new InfoItem(Loc.T("Последнее установленное обновление", "Latest installed update"), null, Loc.T("WMI не вернул даты обновлений", "WMI returned no update dates")));

        try
        {
            using var wu = new ServiceController("wuauserv");
            items.Add(new InfoItem(Loc.T("Служба Windows Update", "Windows Update service"), $"{wu.Status}, {wu.StartType}"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            items.Add(new InfoItem(Loc.T("Служба Windows Update", "Windows Update service"), null, ex.Message));
        }

        var reboot = PendingReboot();
        items.Add(new InfoItem(
            Loc.T("Ожидает перезагрузки", "Pending restart"),
            reboot switch { true => Loc.T("Да", "Yes"), false => Loc.T("Нет", "No"), null => null },
            Loc.T("нет доступа к ключам реестра", "no access to registry keys")));

        return new InfoSection("updates", Loc.T("Обновления Windows", "Windows Update"), items);
    }

    private static DateTime? ParseHotfixDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        string[] formats = ["M/d/yyyy", "MM/dd/yyyy", "d.M.yyyy", "dd.MM.yyyy", "yyyy-MM-dd"];
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        // Иногда WMI отдаёт дату как шестнадцатеричный FILETIME.
        if (long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ft) && ft > 0)
        {
            try
            {
                return DateTime.FromFileTime(ft);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private InfoSection CollectEvents()
    {
        var title = Loc.T("Журнал событий «Система» за 24 часа", "System event log, last 24 hours");
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, "*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 86400000]]]");
            var counts = new Dictionary<string, int>();
            var total = 0;
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null && total < 5000; record = reader.ReadEvent())
            {
                using (record)
                {
                    total++;
                    var provider = record.ProviderName ?? "?";
                    counts[provider] = counts.GetValueOrDefault(provider) + 1;
                }
            }

            var items = new List<InfoItem> { new(Loc.T("Критических ошибок и ошибок", "Critical events and errors"), total.ToString(CultureInfo.CurrentCulture)) };
            items.AddRange(counts.OrderByDescending(kv => kv.Value).Take(5).Select(kv => new InfoItem(kv.Key, kv.Value.ToString(CultureInfo.CurrentCulture))));
            if (total > 0)
            {
                items.Add(new InfoItem(Loc.T("Пояснение", "Note"), Loc.T(
                    "Единичные ошибки в журнале — норма для любой Windows. Повод разобраться — частые повторяющиеся ошибки одного источника (например, диска или драйвера видеокарты).",
                    "A few errors are normal on any Windows system. Investigate frequent repeated errors from one source (such as a disk or GPU driver).")));
            }

            return new InfoSection("events", title, items);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            log.Warn("Журнал событий недоступен", ex);
            return new InfoSection("events", title, [new InfoItem(title, null, Loc.T("нет доступа к журналу (могут понадобиться права администратора)", "no access to the log (administrator rights may be required)"))]);
        }
    }
}

public sealed record ProcessLoad(int Pid, string Name, double CpuPercent, ulong WorkingSetBytes, bool IsSystem);

/// <summary>Процессы с наибольшей нагрузкой (только показ). Процессы приложение не завершает.</summary>
public sealed class ProcessService(ILog log)
{
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm",
        "fontdrvhost", "Memory Compression", "MsMpEng", "SecurityHealthService", "audiodg", "spoolsv", "explorer",
    };

    public async Task<IReadOnlyList<ProcessLoad>> SampleAsync(TimeSpan window, int top, CancellationToken ct)
    {
        var first = Snapshot();
        var sw = Stopwatch.StartNew();
        await Task.Delay(window, ct).ConfigureAwait(false);
        var second = Snapshot();
        var elapsed = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;

        var result = new List<ProcessLoad>();
        foreach (var (pid, info) in second)
        {
            if (!first.TryGetValue(pid, out var before) || before.Name != info.Name)
            {
                continue;
            }

            var cpu = (info.Cpu - before.Cpu).TotalMilliseconds / elapsed * 100.0;
            result.Add(new ProcessLoad(pid, info.Name, Math.Max(0, cpu), info.WorkingSet, info.SessionId == 0 || SystemNames.Contains(info.Name)));
        }

        return result.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.WorkingSetBytes).Take(top).ToList();
    }

    private Dictionary<int, (string Name, TimeSpan Cpu, ulong WorkingSet, int SessionId)> Snapshot()
    {
        var map = new Dictionary<int, (string, TimeSpan, ulong, int)>();
        var denied = 0;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    map[p.Id] = (p.ProcessName, p.TotalProcessorTime, (ulong)p.WorkingSet64, p.SessionId);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Защищённые и завершившиеся процессы недоступны без прав администратора — это нормально.
                    denied++;
                }
            }
        }

        if (denied > 0)
        {
            log.Info($"Процессы: недоступно для чтения {denied}");
        }

        return map;
    }
}
