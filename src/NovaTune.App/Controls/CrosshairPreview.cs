using System.Windows;
using System.Windows.Media;
using NovaTune.Core.Tools;

namespace NovaTune.App.Controls;

/// <summary>
/// Приблизительное превью прицела CS2 (масштаб для 1080p). Точная отрисовка в игре зависит
/// от разрешения и стиля — превью нужно, чтобы оценить форму и цвет.
/// </summary>
public sealed class CrosshairPreview : FrameworkElement
{
    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(
        nameof(Settings), typeof(CrosshairSettings), typeof(CrosshairPreview), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public CrosshairSettings? Settings
    {
        get => (CrosshairSettings?)GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Фон «как в игре»: светлое небо и тёмная стена, чтобы было видно контраст прицела.
        var w = ActualWidth;
        var h = ActualHeight;
        var bg = new LinearGradientBrush(Color.FromRgb(0x9B, 0xA7, 0xB4), Color.FromRgb(0x3A, 0x34, 0x2E), 90);
        bg.Freeze();
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, w, h), 12, 12);
        if (Settings is not { } s)
        {
            return;
        }

        const double scale = 2.0;
        var length = s.Size * scale;
        var thick = Math.Max(1, Math.Round(s.Thickness * scale));
        var gap = Math.Max(0, (s.Gap + 4) * scale / 2);
        var cx = Math.Round(w / 2);
        var cy = Math.Round(h / 2);
        var color = Color.FromArgb(s.Alpha, s.R, s.G, s.B);
        var fill = new SolidColorBrush(color);
        fill.Freeze();
        var outline = s.Outline ? new Pen(Brushes.Black, Math.Max(1, s.OutlineThickness)) : null;
        outline?.Freeze();

        void Bar(double x, double y, double bw, double bh)
        {
            var r = new Rect(x, y, Math.Max(1, bw), Math.Max(1, bh));
            dc.DrawRectangle(fill, outline, r);
        }

        var half = thick / 2;
        Bar(cx + gap, cy - half, length, thick);
        Bar(cx - gap - length, cy - half, length, thick);
        Bar(cx - half, cy + gap, thick, length);
        if (!s.TStyle)
        {
            Bar(cx - half, cy - gap - length, thick, length);
        }

        if (s.Dot)
        {
            Bar(cx - half, cy - half, thick, thick);
        }
    }
}
