using NovaTune.Core.Logging;

namespace NovaTune.Core.Steam;

/// <summary>Конфиги одного аккаунта Steam для CS2.</summary>
public sealed record Cs2UserConfig(
    SteamUser User,
    string ConfigDir,
    bool ConfigDirExists,
    IReadOnlyList<string> Files,
    string? LaunchOptions);

public sealed record Cs2Installation(
    string GameDir,
    string? ExePath,
    string GameCfgDir,
    SteamApp? App,
    bool FoundManually,
    IReadOnlyList<Cs2UserConfig> Users)
{
    /// <summary>Файлы для бэкапа: *.cfg в папке игры и все файлы пользовательских папок cfg.</summary>
    public IReadOnlyList<string> ConfigFilesForBackup()
    {
        var files = new List<string>();
        if (Directory.Exists(GameCfgDir))
        {
            files.AddRange(Directory.EnumerateFiles(GameCfgDir, "*.cfg", SearchOption.TopDirectoryOnly));
        }

        foreach (var user in Users)
        {
            files.AddRange(user.Files);
        }

        return files;
    }
}

/// <summary>Ищет установленную CS2 (appid 730) по библиотекам Steam или в указанной пользователем папке.</summary>
public static class Cs2Locator
{
    public const int AppId = 730;

    /// <summary>Относительный путь к исполняемому файлу внутри папки игры.</summary>
    public static readonly string ExeRelative = Path.Combine("game", "bin", "win64", "cs2.exe");

    public static readonly string CfgRelative = Path.Combine("game", "csgo", "cfg");

    /// <summary>
    /// Поиск CS2. Ручной путь проверяется первым; если он неверен — возвращается null, а не «угадывание».
    /// </summary>
    public static Cs2Installation? Locate(string? steamPath, string? manualGameDir, ILog? log = null)
    {
        SteamScanner? scanner = steamPath is not null && Directory.Exists(steamPath) ? new SteamScanner(steamPath, log) : null;

        if (!string.IsNullOrWhiteSpace(manualGameDir))
        {
            var dir = NormalizeGameDir(manualGameDir);
            return dir is null ? null : Build(dir, null, foundManually: true, scanner);
        }

        var app = scanner?.FindApp(AppId);
        if (app is null || !Directory.Exists(app.InstallDir))
        {
            return null;
        }

        return Build(app.InstallDir, app, foundManually: false, scanner);
    }

    /// <summary>
    /// Принимает корневую папку игры («Counter-Strike Global Offensive») или любую вложенную
    /// (game, game\bin\win64) и возвращает корень, если в нём есть cs2.exe.
    /// </summary>
    public static string? NormalizeGameDir(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var current = new DirectoryInfo(full);
        for (var i = 0; i < 4 && current is not null; i++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, ExeRelative)))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private static Cs2Installation Build(string gameDir, SteamApp? app, bool foundManually, SteamScanner? scanner)
    {
        var exe = Path.Combine(gameDir, ExeRelative);
        var users = new List<Cs2UserConfig>();
        foreach (var user in scanner?.GetUsers() ?? [])
        {
            var cfg = Path.Combine(user.UserdataPath, AppId.ToString(System.Globalization.CultureInfo.InvariantCulture), "local", "cfg");
            var exists = Directory.Exists(cfg);
            var files = exists ? Directory.EnumerateFiles(cfg).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : [];

            // Показываем только аккаунты, где CS2 запускалась (есть папка 730) — остальные к игре отношения не имеют.
            if (Directory.Exists(Path.Combine(user.UserdataPath, "730")))
            {
                users.Add(new Cs2UserConfig(user, cfg, exists, files, scanner!.GetLaunchOptions(user, AppId)));
            }
        }

        return new Cs2Installation(
            gameDir,
            File.Exists(exe) ? exe : null,
            Path.Combine(gameDir, CfgRelative),
            app,
            foundManually,
            users);
    }
}
