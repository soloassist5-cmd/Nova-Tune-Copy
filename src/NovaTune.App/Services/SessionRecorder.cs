using System.Globalization;
using System.Text;
using NovaTune.Core.Hardware;
using NovaTune.Core.Tools;

namespace NovaTune.App.Services;

/// <summary>
/// Запись показаний датчиков в CSV (например, во время игры) и итоговая статистика.
/// Пока идёт запись, мониторинг не приостанавливается при переходах между страницами.
/// </summary>
public sealed class SessionRecorder
{
    private StreamWriter? _writer;
    private readonly object _sync = new();

    public bool IsRecording => _writer is not null;

    public string? FilePath { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public int Rows { get; private set; }

    public Dictionary<string, ColumnStats> Stats { get; } = new();

    public static readonly string[] Columns = ["CPU load %", "CPU clock MHz", "CPU temp °C", "RAM %", "GPU load %", "GPU temp °C"];

    public void Start(string folder)
    {
        lock (_sync)
        {
            if (_writer is not null)
            {
                return;
            }

            Directory.CreateDirectory(folder);
            StartedAt = DateTimeOffset.Now;
            FilePath = Path.Combine(folder, $"session-{StartedAt:yyyyMMdd-HHmmss}.csv");
            _writer = new StreamWriter(FilePath, false, new UTF8Encoding(true));
            _writer.WriteLine("Time," + string.Join(",", Columns));
            Rows = 0;
            Stats.Clear();
            foreach (var c in Columns)
            {
                Stats[c] = new ColumnStats();
            }
        }

        AppServices.Current.Monitoring.Sampled += OnSampled;
        AppServices.Current.Monitoring.Start(AppServices.Current.Settings.Current.AdvancedSensors);
    }

    public void Stop()
    {
        AppServices.Current.Monitoring.Sampled -= OnSampled;
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void OnSampled(object? sender, MonitoringSnapshot s)
    {
        var gpu = s.Gpus.FirstOrDefault();
        double[] values =
        [
            s.CpuLoadPercent.Value ?? double.NaN,
            s.CpuClockMhz.Value ?? double.NaN,
            s.CpuTemperatureC.Value ?? double.NaN,
            s.RamUsedPercent.Value ?? double.NaN,
            gpu?.LoadPercent.Value ?? double.NaN,
            gpu?.TemperatureC.Value ?? double.NaN,
        ];
        lock (_sync)
        {
            if (_writer is null)
            {
                return;
            }

            // Пустая ячейка — датчик не дал значения (не 0 и не подстановка).
            _writer.WriteLine(s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "," +
                string.Join(",", values.Select(v => double.IsNaN(v) ? "" : v.ToString("0.0", CultureInfo.InvariantCulture))));
            for (var i = 0; i < Columns.Length; i++)
            {
                Stats[Columns[i]].Add(values[i]);
            }

            Rows++;
            if (Rows % 10 == 0)
            {
                _writer.Flush();
            }
        }
    }
}
