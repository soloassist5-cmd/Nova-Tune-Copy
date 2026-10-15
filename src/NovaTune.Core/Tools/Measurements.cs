namespace NovaTune.Core.Tools;

/// <summary>
/// Частота опроса мыши по отметкам времени событий Raw Input: число отчётов за последнюю секунду.
/// Учитываются только моменты, когда мышь реально двигалась, — в покое отчётов нет.
/// </summary>
public sealed class PollingRateMeter(long ticksPerSecond)
{
    private readonly Queue<long> _window = new();
    private long _lastTick = -1;

    public int CurrentHz { get; private set; }

    public int PeakHz { get; private set; }

    public int Samples { get; private set; }

    public void Add(long tick)
    {
        // Пауза дольше 100 мс — пользователь остановил мышь: окно начинаем заново, иначе частота занижается.
        if (_lastTick >= 0 && tick - _lastTick > ticksPerSecond / 10)
        {
            _window.Clear();
        }

        _lastTick = tick;
        _window.Enqueue(tick);
        while (_window.Count > 0 && tick - _window.Peek() > ticksPerSecond)
        {
            _window.Dequeue();
        }

        Samples++;

        // Пока окно не заполнено на секунду, экстраполируем по фактической длительности (не меньше 250 мс).
        var span = tick - _window.Peek();
        if (span >= ticksPerSecond / 4 && _window.Count > 1)
        {
            CurrentHz = (int)Math.Round((_window.Count - 1) * (double)ticksPerSecond / span);
            PeakHz = Math.Max(PeakHz, CurrentHz);
        }
    }

    public void Reset()
    {
        _window.Clear();
        _lastTick = -1;
        CurrentHz = 0;
        PeakHz = 0;
        Samples = 0;
    }

    /// <summary>Ближайшая стандартная частота мыши (125/250/500/1000/2000/4000/8000 Гц).</summary>
    public static int NearestStandard(int hz)
    {
        int[] standard = [125, 250, 500, 1000, 2000, 4000, 8000];
        return standard.MinBy(s => Math.Abs(s - hz));
    }
}

/// <summary>Среднее и максимум по колонке показаний; NaN (нет данных) не учитывается.</summary>
public sealed class ColumnStats
{
    private double _sum;

    public int Count { get; private set; }

    public double Max { get; private set; } = double.NaN;

    public double Average => Count == 0 ? double.NaN : _sum / Count;

    public void Add(double value)
    {
        if (double.IsNaN(value))
        {
            return;
        }

        _sum += value;
        Count++;
        Max = double.IsNaN(Max) ? value : Math.Max(Max, value);
    }
}
