using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NovaTune.App.ViewModels;
using NovaTune.Core.Logging;

namespace NovaTune.App.Services;

/// <summary>
/// Режим --screenshot: обходит все страницы и сохраняет PNG окна (RenderTargetBitmap) —
/// для визуальной проверки вёрстки без ручного кликанья. Системные настройки не меняются.
/// </summary>
public static class ScreenshotRunner
{
    public static async Task RunAsync(MainWindow window, MainViewModel vm, string directory, ILog log)
    {
        Directory.CreateDirectory(directory);
        await Task.Delay(1500);
        var index = 0;
        foreach (var item in vm.Items.ToList())
        {
            index++;
            vm.Navigate(item.Key);

            // Ждём загрузку страницы (не больше 15 секунд) и окончание анимаций.
            for (var waited = 0; waited < 15000 && vm.CurrentPage is { IsLoading: true }; waited += 250)
            {
                await Task.Delay(250);
            }

            await Task.Delay(2200);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            try
            {
                Save(window, Path.Combine(directory, $"{index:00}-{item.Key}.png"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Error("Скриншот страницы " + item.Key, ex);
            }
        }
    }

    public static void Save(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        // Фон окна рисуем явно: RenderTargetBitmap захватывает только содержимое.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
