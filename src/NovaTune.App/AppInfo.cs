namespace NovaTune.App;

/// <summary>Отображаемая версия приложения (в интерфейсе и отчётах — коротко, «v2»).</summary>
public static class AppInfo
{
    public static string DisplayVersion => "v" + (typeof(AppInfo).Assembly.GetName().Version?.Major ?? 2);
}
