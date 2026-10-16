using Microsoft.Win32;

namespace NovaTune.App.Services;

/// <summary>
/// Автозапуск NOVA TUNE вместе с Windows: значение в HKCU\...\Run (только для текущего пользователя, без прав администратора).
/// Запуск идёт с ключом --autostart: окно открывается свёрнутым (если так выбрано в настройках).
/// </summary>
public static class AutostartService
{
    public const string Argument = "--autostart";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NOVA TUNE";

    private static string ExePath => Environment.ProcessPath ?? "";

    private static string Command => $"\"{ExePath}\" {Argument}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                AppServices.Current.Log.Warn("Автозапуск: не удалось прочитать реестр", ex);
                return false;
            }
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, Command, RegistryValueKind.String);
        AppServices.Current.Log.Info("Автозапуск включён: " + Command);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        AppServices.Current.Log.Info("Автозапуск выключен");
    }

    /// <summary>Если exe перенесли в другую папку, запись автозапуска указывает на старый путь — обновляем её.</summary>
    public static void RepairPathIfMoved()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase)
                && ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
                AppServices.Current.Log.Info("Автозапуск: путь обновлён на " + Command);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppServices.Current.Log.Warn("Автозапуск: не удалось обновить путь", ex);
        }
    }
}
