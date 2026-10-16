using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NovaTune.App.Services;

/// <summary>
/// Обложки игр из локального кэша библиотеки Steam (appcache\librarycache). Ничего не скачивается из сети.
/// Поддерживаются оба формата кэша: старый (730_library_600x900.jpg) и новый (730\…\library_600x900.jpg).
/// </summary>
public static class SteamArt
{
    public static string? FindCover(string? steamPath, int appId) => Find(steamPath, appId, "library_600x900");

    public static string? FindHero(string? steamPath, int appId) => Find(steamPath, appId, "library_hero") ?? Find(steamPath, appId, "header");

    private static string? Find(string? steamPath, int appId, string kind)
    {
        if (steamPath is null)
        {
            return null;
        }

        var cache = Path.Combine(steamPath, "appcache", "librarycache");
        if (!Directory.Exists(cache))
        {
            return null;
        }

        var old = Path.Combine(cache, $"{appId}_{kind}.jpg");
        if (File.Exists(old))
        {
            return old;
        }

        var folder = Path.Combine(cache, appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(folder, kind + "*.jpg", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(folder, kind + "*.png", SearchOption.AllDirectories))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Загрузка с уменьшением и заморозкой (можно создавать в фоновом потоке).</summary>
    public static ImageSource? Load(string? path, int decodeWidth)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppServices.Current.Log.Warn("Не удалось загрузить обложку " + path, ex);
            return null;
        }
    }
}
