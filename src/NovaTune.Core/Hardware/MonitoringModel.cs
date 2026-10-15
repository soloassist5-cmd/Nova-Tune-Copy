namespace NovaTune.Core.Hardware;

/// <summary>Показание датчика: значение или причина, по которой его нет. Никаких подстановок «на глаз».</summary>
public readonly record struct Reading(double? Value, string? Reason)
{
    public bool HasValue => Value.HasValue;

    public static Reading Of(double value) => new(value, null);

    public static Reading Missing(string reason) => new(null, reason);
}

public sealed record GpuReading(string Name, Reading LoadPercent, Reading TemperatureC, Reading MemoryUsedBytes, Reading CoreClockMhz);

public sealed record DiskReading(string Name, Reading TemperatureC, Reading ActivePercent);

/// <summary>Снимок показаний за один такт мониторинга.</summary>
public sealed record MonitoringSnapshot
{
    public DateTimeOffset Time { get; init; }

    public Reading CpuLoadPercent { get; init; }

    /// <summary>Эффективная частота CPU (как в диспетчере задач: % Processor Performance × базовая частота).</summary>
    public Reading CpuClockMhz { get; init; }

    public Reading CpuTemperatureC { get; init; }

    public Reading RamUsedPercent { get; init; }

    public Reading RamUsedBytes { get; init; }

    public Reading RamTotalBytes { get; init; }

    public IReadOnlyList<GpuReading> Gpus { get; init; } = [];

    public IReadOnlyList<DiskReading> Disks { get; init; } = [];
}

/// <summary>Кольцевой буфер истории значений для графиков (фиксированный объём памяти).</summary>
public sealed class History
{
    private readonly double[] _values;
    private readonly object _sync = new();
    private int _start;
    private int _count;

    public History(int capacity)
    {
        _values = new double[capacity];
    }

    public int Capacity => _values.Length;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>NaN означает «нет данных» в этот момент — на графике будет разрыв.</summary>
    public void Add(double value)
    {
        lock (_sync)
        {
            var index = (_start + _count) % _values.Length;
            _values[index] = value;
            if (_count < _values.Length)
            {
                _count++;
            }
            else
            {
                _start = (_start + 1) % _values.Length;
            }
        }
    }

    public double[] ToArray()
    {
        lock (_sync)
        {
            var result = new double[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _values[(_start + i) % _values.Length];
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _start = 0;
            _count = 0;
        }
    }

    /// <summary>Среднее по последним N значениям без NaN; null, если данных нет.</summary>
    public double? AverageOfLast(int n)
    {
        var values = ToArray().Reverse().Take(n).Where(v => !double.IsNaN(v)).ToList();
        return values.Count == 0 ? null : values.Average();
    }
}
