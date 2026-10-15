using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NovaTune.Core.Settings;

namespace NovaTune.App.Services;

/// <summary>Переключение темы: заменяет словарь цветов в ресурсах приложения (все кисти — DynamicResource).</summary>
public static class ThemeService
{
    private static ResourceDictionary? _current;

    public static AppTheme Theme { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        var dict = new ResourceDictionary
        {
            Source = new Uri(theme == AppTheme.Light ? "pack://application:,,,/NOVA-TUNE;component/Themes/Colors.Light.xaml" : "pack://application:,,,/NOVA-TUNE;component/Themes/Colors.Dark.xaml"),
        };

        if (_current is not null)
        {
            app.Resources.MergedDictionaries.Remove(_current);
        }

        // Словарь цветов должен идти первым: остальные словари ссылаются на его ключи.
        app.Resources.MergedDictionaries.Insert(0, dict);
        _current = dict;
        Theme = theme;
        app.Resources["Brush.Noise"] = CreateNoiseBrush(theme);
    }

    /// <summary>
    /// Мелкий шум поверх фона (против «пластиковой» гладкости градиента). Генерируется один раз — это декор,
    /// а не данные, поэтому случайные значения здесь уместны.
    /// </summary>
    private static ImageBrush CreateNoiseBrush(AppTheme theme)
    {
        const int size = 128;
        var pixels = new byte[size * size * 4];
        var random = new Random(7);
        for (var i = 0; i < size * size; i++)
        {
            var v = (byte)random.Next(0, 256);
            var light = theme == AppTheme.Dark;
            pixels[(i * 4) + 0] = light ? (byte)255 : (byte)0;
            pixels[(i * 4) + 1] = light ? (byte)255 : (byte)0;
            pixels[(i * 4) + 2] = light ? (byte)255 : (byte)0;
            pixels[(i * 4) + 3] = (byte)(v / (light ? 28 : 36));
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }
}
