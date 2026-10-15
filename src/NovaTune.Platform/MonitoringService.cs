using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Logging;
using NovaTune.Platform.Native;

namespace NovaTune.Platform;

/// <summary>
/// Мониторинг в реальном времени. Источники:
/// — загрузка CPU: счётчик «Processor Information\% Processor Utility» (совпадает с диспетчером задач);
/// — частота CPU: «% Processor Performance» × базовая частота (так считает диспетчер задач);
/// — память: GlobalMemoryStatusEx;
/// — GPU: LibreHardwareMonitor (NVAPI/ADL/D3D, без драйвера ядра); при недоступности — счётчики «GPU Engine»;
/// — температура CPU: только LibreHardwareMonitor в режиме расширенных датчиков (права администратора + драйвер PawnIO).
/// Один таймер на всё приложение; подписчики получают снимки через событие.
/// </summary>
public sealed class MonitoringService : IDisposable
{
    public const int HistoryLength = 600;

    private readonly ILog _log;
    private readonly object _sync = new();
    private readonly int _cpuBaseMhz;
    private Timer? _timer;
    private int _intervalMs = 1000;
    private int _sampling;
    private bool _disposed;

    private PerformanceCounter? _cpuUtility;
    private PerformanceCounter? _cpuPerformance;
    private string? _cpuUtilityError;
    private GpuEngineSampler? _gpuEngines;

    private Computer? _computer;
    private string? _lhmGpuError;
    private bool _advanced;

    public MonitoringService(ILog log, int cpuBaseMhz)
    {
        _log = log;
        _cpuBaseMhz = cpuBaseMhz;
    }

    public event EventHandler<MonitoringSnapshot>? Sampled;

    public MonitoringSnapshot? Last { get; private set; }

    public History CpuLoad { get; } = new(HistoryLength);

    public History CpuTemp { get; } = new(HistoryLength);

    public History CpuClock { get; } = new(HistoryLength);

    public History RamLoad { get; } = new(HistoryLength);

    public History GpuLoad { get; } = new(HistoryLength);

    public History GpuTemp { get; } = new(HistoryLength);

    public bool IsRunning => _timer is not null;

    public bool AdvancedSensorsActive => _advanced && _computer?.IsCpuEnabled == true;

    /// <summary>Почему расширенные датчики недоступны (null — доступны или не запрошены).</summary>
    public string? AdvancedSensorsReason { get; private set; }

    public int IntervalMs
    {
        get => _intervalMs;
        set
        {
            _intervalMs = Math.Clamp(value, 500, 5000);
            _timer?.Change(_intervalMs, _intervalMs);
        }
    }

    /// <summary>Установлен ли драйвер PawnIO (нужен LibreHardwareMonitor 0.9.x для датчиков CPU).</summary>
    public static bool IsPawnIoInstalled()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        return key is not null;
    }

    public void Start(bool advancedSensors)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null)
            {
                return;
            }

            InitCounters();
            if (_computer is null || _advanced != advancedSensors)
            {
                CloseLhm();
                InitLhm(advancedSensors);
            }

            _timer = new Timer(_ => Sample(), null, 0, _intervalMs);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void InitCounters()
    {
        if (_cpuUtility is not null || _cpuUtilityError is not null)
        {
            return;
        }

        try
        {
            _cpuUtility = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", readOnly: true);
            _cpuUtility.NextValue();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Warn("Счётчик % Processor Utility недоступен, используется % Processor Time", ex);
            try
            {
                _cpuUtility = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
                _cpuUtility.NextValue();
            }
            catch (Exception ex2) when (ex2 is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
            {
                _cpuUtility = null;
                _cpuUtilityError = Loc.T("счётчики производительности недоступны (возможно, повреждены — lodctr /R)", "performance counters unavailable (may be corrupted — lodctr /R)");
                _log.Error("Счётчики загрузки CPU недоступны", ex2);
            }
        }

        try
        {
            _cpuPerformance = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);
            _cpuPerformance.NextValue();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Warn("Счётчик % Processor Performance недоступен", ex);
            _cpuPerformance = null;
        }
    }

    private void InitLhm(bool advanced)
    {
        _advanced = advanced;
        AdvancedSensorsReason = null;
        var cpuEnabled = false;
        if (advanced)
        {
            if (!OsInfo.IsElevated())
            {
                AdvancedSensorsReason = Loc.T("Нужны права администратора: перезапустите NOVA TUNE от имени администратора.", "Administrator rights required: restart NOVA TUNE as administrator.");
            }
            else if (!IsPawnIoInstalled())
            {
                AdvancedSensorsReason = Loc.T("Не установлен драйвер PawnIO, который LibreHardwareMonitor использует для датчиков CPU.", "The PawnIO driver used by LibreHardwareMonitor for CPU sensors is not installed.");
            }
            else
            {
                cpuEnabled = true;
            }
        }

        try
        {
            _computer = new Computer
            {
                IsGpuEnabled = true,
                IsCpuEnabled = cpuEnabled,
                IsStorageEnabled = cpuEnabled,
            };
            _computer.Open();
            _lhmGpuError = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("LibreHardwareMonitor не инициализирован", ex);
            _computer = null;
            _lhmGpuError = Loc.T("библиотека датчиков не запустилась", "sensor library failed to start");
            if (cpuEnabled)
            {
                AdvancedSensorsReason = Loc.T("LibreHardwareMonitor не смог открыть датчики: ", "LibreHardwareMonitor could not open sensors: ") + ex.Message;
            }
        }
    }

    /// <summary>
    /// Приостановка при уходе со страниц мониторинга: останавливаем таймер и закрываем LibreHardwareMonitor
    /// (освобождаем драйвер и дескрипторы). История сохраняется, при следующем запуске в графике будет разрыв.
    /// </summary>
    public void Suspend()
    {
        Stop();
        lock (_sync)
        {
            CloseLhm();
        }
    }

    /// <summary>Перезапуск с другим режимом датчиков (например, после изменения настроек).</summary>
    public void Restart(bool advancedSensors)
    {
        Stop();
        lock (_sync)
        {
            CloseLhm();
        }

        Start(advancedSensors);
    }

    private void Sample()
    {
        // Защита от наложения тактов, если предыдущий опрос затянулся.
        if (Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }

        try
        {
            MonitoringSnapshot snapshot;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                snapshot = Collect();
            }

            Last = snapshot;
            CpuLoad.Add(snapshot.CpuLoadPercent.Value ?? double.NaN);
            CpuTemp.Add(snapshot.CpuTemperatureC.Value ?? double.NaN);
            CpuClock.Add(snapshot.CpuClockMhz.Value ?? double.NaN);
            RamLoad.Add(snapshot.RamUsedPercent.Value ?? double.NaN);
            var gpu = snapshot.Gpus.FirstOrDefault();
            GpuLoad.Add(gpu?.LoadPercent.Value ?? double.NaN);
            GpuTemp.Add(gpu?.TemperatureC.Value ?? double.NaN);
            Sampled?.Invoke(this, snapshot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("Ошибка опроса датчиков", ex);
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    private MonitoringSnapshot Collect()
    {
        Reading cpuLoad = _cpuUtility is null
            ? Reading.Missing(_cpuUtilityError ?? Loc.T("счётчик недоступен", "counter unavailable"))
            : Reading.Of(Math.Clamp(_cpuUtility.NextValue(), 0, 100));

        Reading cpuClock = _cpuPerformance is not null && _cpuBaseMhz > 0
            ? Reading.Of(_cpuPerformance.NextValue() / 100.0 * _cpuBaseMhz)
            : Reading.Missing(Loc.T("нет счётчика % Processor Performance", "% Processor Performance counter unavailable"));

        var mem = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        Reading ramPct, ramUsed, ramTotal;
        if (NativeMethods.GlobalMemoryStatusEx(ref mem))
        {
            ramTotal = Reading.Of(mem.TotalPhys);
            ramUsed = Reading.Of(mem.TotalPhys - mem.AvailPhys);
            ramPct = Reading.Of(100.0 * (mem.TotalPhys - mem.AvailPhys) / mem.TotalPhys);
        }
        else
        {
            ramPct = ramUsed = ramTotal = Reading.Missing("GlobalMemoryStatusEx");
        }

        Reading cpuTemp = Reading.Missing(_advanced
            ? AdvancedSensorsReason ?? Loc.T("датчик не найден", "sensor not found")
            : Loc.T("включите расширенные датчики в настройках (нужны права администратора)", "enable advanced sensors in Settings (administrator rights required)"));

        var gpus = new List<GpuReading>();
        var disks = new List<DiskReading>();
        if (_computer is not null)
        {
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        {
                            // Предпочитаем «Package»/«Tctl/Tdie»/«Core (Tctl/Tdie)», иначе максимум по ядрам.
                            var temps = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150).ToList();
                            var preferred = temps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase));
                            var value = preferred?.Value ?? temps.Select(s => s.Value).Max();
                            if (value is { } t)
                            {
                                cpuTemp = Reading.Of(t);
                            }
                            else
                            {
                                cpuTemp = Reading.Missing(Loc.T("процессор не отдаёт датчик температуры", "the CPU does not expose a temperature sensor"));
                            }

                            break;
                        }

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        gpus.Add(ReadGpu(hw));
                        break;

                    case HardwareType.Storage:
                        {
                            var temp = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value;
                            var load = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Total Activity", StringComparison.OrdinalIgnoreCase))?.Value;
                            disks.Add(new DiskReading(
                                hw.Name,
                                temp is { } dt ? Reading.Of(dt) : Reading.Missing(Loc.T("нет датчика", "no sensor")),
                                load is { } dl ? Reading.Of(dl) : Reading.Missing(Loc.T("нет данных", "no data"))));
                            break;
                        }
                }
            }
        }

        // Если LibreHardwareMonitor не нашёл видеокарт — загрузка по счётчикам «GPU Engine» (Windows 10 1709+).
        if (gpus.Count == 0)
        {
            _gpuEngines ??= GpuEngineSampler.TryCreate(_log);
            var load = _gpuEngines?.Sample();
            var reason = _lhmGpuError ?? Loc.T("видеокарта не поддерживается библиотекой датчиков", "GPU not supported by the sensor library");
            gpus.Add(new GpuReading(
                Loc.T("Видеокарта", "GPU"),
                load is { } l ? Reading.Of(l) : Reading.Missing(Loc.T("счётчики GPU Engine недоступны", "GPU Engine counters unavailable")),
                Reading.Missing(reason),
                Reading.Missing(reason),
                Reading.Missing(reason)));
        }

        return new MonitoringSnapshot
        {
            Time = DateTimeOffset.Now,
            CpuLoadPercent = cpuLoad,
            CpuClockMhz = cpuClock,
            CpuTemperatureC = cpuTemp,
            RamUsedPercent = ramPct,
            RamUsedBytes = ramUsed,
            RamTotalBytes = ramTotal,
            Gpus = gpus,
            Disks = disks,
        };
    }

    private static GpuReading ReadGpu(IHardware hw)
    {
        var sensors = hw.Sensors;
        var noSensor = Loc.T("драйвер не предоставляет значение", "the driver does not provide this value");
        var load = sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && (s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase)))?.Value;
        var temps = sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150).ToList();
        var temp = (temps.FirstOrDefault(s => s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
            ?? temps.FirstOrDefault(s => !s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)))?.Value;
        var memUsed = sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && (s.Name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase)))?.Value;
        var clock = sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))?.Value;
        return new GpuReading(
            hw.Name,
            load is { } l ? Reading.Of(l) : Reading.Missing(noSensor),
            temp is { } t ? Reading.Of(t) : Reading.Missing(noSensor),
            memUsed is { } m ? Reading.Of(m * 1024.0 * 1024.0) : Reading.Missing(noSensor),
            clock is { } c ? Reading.Of(c) : Reading.Missing(noSensor));
    }

    private void CloseLhm()
    {
        try
        {
            _computer?.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("Ошибка при закрытии LibreHardwareMonitor", ex);
        }

        _computer = null;
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseLhm();
            _cpuUtility?.Dispose();
            _cpuPerformance?.Dispose();
            _gpuEngines?.Dispose();
        }
    }
}

/// <summary>
/// Загрузка GPU по счётчикам «GPU Engine\Utilization Percentage», как в диспетчере задач:
/// сумма по процессам для каждого (адаптер, тип движка), затем максимум.
/// </summary>
internal sealed class GpuEngineSampler : IDisposable
{
    private readonly PerformanceCounterCategory _category;
    private Dictionary<string, CounterSample> _previous = new();

    private GpuEngineSampler(PerformanceCounterCategory category)
    {
        _category = category;
    }

    public static GpuEngineSampler? TryCreate(ILog log)
    {
        try
        {
            return PerformanceCounterCategory.Exists("GPU Engine") ? new GpuEngineSampler(new PerformanceCounterCategory("GPU Engine")) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            log.Warn("Категория GPU Engine недоступна", ex);
            return null;
        }
    }

    public double? Sample()
    {
        InstanceDataCollectionCollection data;
        try
        {
            data = _category.ReadCategory();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }

        var collection = data["Utilization Percentage"];
        if (collection is null)
        {
            return null;
        }

        var current = new Dictionary<string, CounterSample>();
        var sums = new Dictionary<string, double>();
        foreach (InstanceData instance in collection.Values)
        {
            var name = instance.InstanceName;
            var sample = instance.Sample;
            current[name] = sample;
            if (!_previous.TryGetValue(name, out var prev))
            {
                continue;
            }

            var value = CounterSample.Calculate(prev, sample);

            // Имя экземпляра: pid_1234_luid_0x..._0x..._phys_0_eng_0_engtype_3D
            var luidIndex = name.IndexOf("luid_", StringComparison.Ordinal);
            var typeIndex = name.IndexOf("engtype_", StringComparison.Ordinal);
            var physIndex = name.IndexOf("_phys", StringComparison.Ordinal);
            if (luidIndex < 0 || typeIndex < 0 || physIndex < luidIndex)
            {
                continue;
            }

            var key = name[luidIndex..physIndex] + "|" + name[typeIndex..];
            sums[key] = sums.GetValueOrDefault(key) + value;
        }

        _previous = current;
        return sums.Count == 0 ? null : Math.Clamp(sums.Values.Max(), 0, 100);
    }

    public void Dispose()
    {
        _previous.Clear();
    }
}
