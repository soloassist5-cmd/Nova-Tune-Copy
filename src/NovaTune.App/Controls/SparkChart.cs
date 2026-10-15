using System.Windows;
using System.Windows.Media;

namespace NovaTune.App.Controls;

/// <summary>
/// Лёгкий график для мониторинга: одна линия с заливкой, рисуется в OnRender через StreamGeometry.
/// NaN в данных — разрыв линии (датчик в этот момент не отдавал значение). Никаких сглаживаний и интерполяций.
/// </summary>
public sealed class SparkChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(SparkChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SparkChart), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(SparkChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Подстраивать максимум под данные (для частоты и температур).</summary>
    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(SparkChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(SparkChart), new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(SparkChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Сколько точек помещается по ширине (окно истории).</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(SparkChart), new FrameworkPropertyMetadata(120, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public bool AutoScale
    {
        get => (bool)GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2)
        {
            return;
        }

        // Прозрачный фон — чтобы работали подсказки по наведению на всю область.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        if (GridBrush is { } grid)
        {
            var pen = new Pen(grid, 1);
            pen.Freeze();
            for (var i = 1; i < 4; i++)
            {
                var y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
        }

        var values = Values;
        if (values is null || values.Length < 2)
        {
            return;
        }

        var capacity = Math.Max(Capacity, 2);
        var data = values.Length > capacity ? values[^capacity..] : values;
        var min = Minimum;
        var max = Maximum;
        if (AutoScale)
        {
            var real = data.Where(v => !double.IsNaN(v)).ToArray();
            if (real.Length > 0)
            {
                var hi = real.Max();
                var lo = real.Min();
                var pad = Math.Max((hi - lo) * 0.2, Math.Max(1, hi * 0.05));
                max = hi + pad;
                min = Math.Max(0, lo - pad);
            }
        }

        if (max - min < 1e-6)
        {
            max = min + 1;
        }

        var step = w / (capacity - 1);
        var offset = (capacity - data.Length) * step;
        double X(int i) => offset + (i * step);
        double Y(double v) => h - 2 - ((Math.Clamp(v, min, max) - min) / (max - min) * (h - 4));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            var segmentStart = -1;
            for (var i = 0; i <= data.Length; i++)
            {
                var gap = i == data.Length || double.IsNaN(data[i]);
                if (!gap && segmentStart < 0)
                {
                    segmentStart = i;
                    lc.BeginFigure(new Point(X(i), Y(data[i])), false, false);
                    ac.BeginFigure(new Point(X(i), h), true, true);
                    ac.LineTo(new Point(X(i), Y(data[i])), false, false);
                }
                else if (!gap)
                {
                    lc.LineTo(new Point(X(i), Y(data[i])), true, true);
                    ac.LineTo(new Point(X(i), Y(data[i])), false, false);
                }
                else if (segmentStart >= 0)
                {
                    ac.LineTo(new Point(X(i - 1), h), false, false);
                    segmentStart = -1;
                }
            }
        }

        line.Freeze();
        area.Freeze();

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.CornflowerBlue;
        var fill = new LinearGradientBrush(Color.FromArgb(70, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);

        var stroke = new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        stroke.Freeze();
        dc.DrawGeometry(null, stroke, line);
    }
}
