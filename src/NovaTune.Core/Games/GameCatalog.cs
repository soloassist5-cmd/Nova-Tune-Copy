using NovaTune.Core.Localization;

namespace NovaTune.Core.Games;

/// <summary>Откуда считается путь к папке конфигов.</summary>
public enum ConfigRoot
{
    /// <summary>Папка установки игры.</summary>
    InstallDir,

    /// <summary>Steam\userdata\&lt;id аккаунта&gt;\… (по каждому аккаунту Steam).</summary>
    SteamUserdata,
    Documents,
    LocalAppData,
    RoamingAppData,
    SavedGames,
}

public sealed record ConfigLocation(ConfigRoot Root, string RelativePath, string Pattern = "*", bool Recursive = false);

/// <summary>
/// Профиль игры: где лежат её конфиги, как её узнать и что о ней полезно знать.
/// Пути сверены с типичной установкой; на конкретном ПК используются только реально существующие папки.
/// </summary>
public sealed record GameProfile(
    string Id,
    string Name,
    int? SteamAppId,
    string? ExeRelative,
    IReadOnlyList<ConfigLocation> Configs,
    IReadOnlyList<string> ProcessNames,
    IReadOnlyList<LocText> Tips);

/// <summary>Системные папки пользователя (в тестах подменяются временными).</summary>
public sealed record KnownFolders(string Documents, string LocalAppData, string RoamingAppData, string SavedGames)
{
    public static KnownFolders FromSystem() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"));
}

public static class GameCatalog
{
    public static readonly IReadOnlyList<GameProfile> Profiles =
    [
        new("cs2", "Counter-Strike 2", 730, Path.Combine("game", "bin", "win64", "cs2.exe"),
            [new(ConfigRoot.InstallDir, Path.Combine("game", "csgo", "cfg"), "*.cfg"), new(ConfigRoot.SteamUserdata, Path.Combine("730", "local", "cfg"))],
            ["cs2"],
            [
                new("Графические настройки CS2 хранит в cs2_video.txt и перезаписывает сама — меняйте их в игре, а файл держите в бэкапе.", "CS2 stores graphics settings in cs2_video.txt and rewrites it itself — change them in-game and keep the file backed up."),
                new("Свои .cfg подключаются параметром запуска +exec имя_файла.", "Your own .cfg files are loaded with the +exec filename launch option."),
            ]),
        new("csgo", "Counter-Strike: Global Offensive (legacy)", 730, "csgo.exe",
            [new(ConfigRoot.InstallDir, Path.Combine("csgo", "cfg"), "*.cfg"), new(ConfigRoot.SteamUserdata, Path.Combine("730", "local", "cfg"))],
            ["csgo"],
            [new("Legacy-версия ставится как бета-ветка CS2 (csgo_legacy) в ту же папку; конфиги CS:GO и CS2 — разные файлы.", "The legacy version installs as the csgo_legacy beta branch of CS2 into the same folder; CS:GO and CS2 configs are different files.")]),
        new("dota2", "Dota 2", 570, Path.Combine("game", "bin", "win64", "dota2.exe"),
            [new(ConfigRoot.InstallDir, Path.Combine("game", "dota", "cfg"), "*.cfg"), new(ConfigRoot.SteamUserdata, Path.Combine("570", "remote", "cfg"))],
            ["dota2"],
            [
                new("Рендер выбирается параметром запуска (-dx11 или -vulkan): на разных видеокартах быстрее разный — сравните FPS.", "The renderer is chosen with a launch option (-dx11 or -vulkan): different GPUs are faster with different ones — compare FPS."),
                new("Бинды хранятся в userdata\\…\\570\\remote\\cfg и синхронизируются через Steam Cloud.", "Key bindings live in userdata\\…\\570\\remote\\cfg and sync via Steam Cloud."),
            ]),
        new("gta5", "Grand Theft Auto V", 271590, "GTA5.exe",
            [new(ConfigRoot.Documents, Path.Combine("Rockstar Games", "GTA V"), "*.xml"), new(ConfigRoot.Documents, Path.Combine("Rockstar Games", "GTAV Enhanced"), "*.xml")],
            ["GTA5", "GTA5_Enhanced"],
            [new("Графика хранится в settings.xml в Документах. Если игра не запускается после смены настроек — восстановите файл из бэкапа.", "Graphics settings live in settings.xml in Documents. If the game fails to start after changing settings, restore the file from a backup.")]),
        new("apex", "Apex Legends", 1172470, "r5apex.exe",
            [new(ConfigRoot.SavedGames, Path.Combine("Respawn", "Apex", "local")), new(ConfigRoot.InstallDir, "cfg", "*.cfg")],
            ["r5apex", "r5apex_dx12"],
            [new("videoconfig.txt игра может перезаписать при запуске; держите рабочую версию в бэкапе.", "The game may rewrite videoconfig.txt on launch; keep a working copy backed up.")]),
        new("pubg", "PUBG: BATTLEGROUNDS", 578080, null,
            [new(ConfigRoot.LocalAppData, Path.Combine("TslGame", "Saved", "Config", "WindowsNoEditor"), "*.ini")],
            ["TslGame"],
            [new("Настройки графики и управления — в GameUserSettings.ini.", "Graphics and control settings are in GameUserSettings.ini.")]),
        new("rust", "Rust", 252490, "RustClient.exe",
            [new(ConfigRoot.InstallDir, "cfg", "*.cfg")],
            ["RustClient"],
            [new("client.cfg — настройки, keys.cfg — бинды.", "client.cfg holds settings, keys.cfg holds key bindings.")]),
        new("tf2", "Team Fortress 2", 440, null,
            [new(ConfigRoot.InstallDir, Path.Combine("tf", "cfg"), "*.cfg")],
            ["tf_win64", "tf"],
            [new("autoexec.cfg в tf\\cfg выполняется автоматически при запуске.", "autoexec.cfg in tf\\cfg runs automatically at launch.")]),
        new("deadlock", "Deadlock", 1422450, null,
            [new(ConfigRoot.InstallDir, Path.Combine("game", "citadel", "cfg"), "*.cfg")],
            [],
            [new("Игра в разработке — формат конфигов может меняться после обновлений.", "The game is in development — the config format may change with updates.")]),
        new("valorant", "VALORANT", null, null,
            [new(ConfigRoot.LocalAppData, Path.Combine("VALORANT", "Saved", "Config"), "*.ini", Recursive: true)],
            ["VALORANT-Win64-Shipping"],
            [new("NOVA TUNE только копирует файлы настроек и ничего не делает с игрой во время матча. Основные настройки VALORANT хранит на серверах Riot.", "NOVA TUNE only copies settings files and does nothing to the game during a match. VALORANT keeps most settings on Riot servers.")]),
        new("fortnite", "Fortnite", null, null,
            [new(ConfigRoot.LocalAppData, Path.Combine("FortniteGame", "Saved", "Config", "WindowsClient"), "*.ini")],
            ["FortniteClient-Win64-Shipping"],
            [new("Часть настроек синхронизируется с аккаунтом Epic и может перезаписать локальный файл.", "Some settings sync with the Epic account and may overwrite the local file.")]),
        new("minecraft", "Minecraft (Java Edition)", null, null,
            [new(ConfigRoot.RoamingAppData, ".minecraft", "options*.txt")],
            ["javaw"],
            [new("options.txt — настройки и бинды; при разных версиях игры файл общий.", "options.txt holds settings and bindings; it is shared between game versions.")]),
    ];

    /// <summary>Профиль для приложения Steam: учитывается и appid, и исполняемый файл (CS2 и CS:GO legacy делят appid 730).</summary>
    public static GameProfile? ForSteamApp(int appId, string installDir) =>
        Profiles.Where(p => p.SteamAppId == appId)
            .OrderByDescending(p => p.ExeRelative is not null && File.Exists(Path.Combine(installDir, p.ExeRelative)))
            .FirstOrDefault(p => p.ExeRelative is null || File.Exists(Path.Combine(installDir, p.ExeRelative)) || Profiles.Count(x => x.SteamAppId == appId) == 1);
}

/// <summary>Поиск реальных папок и файлов конфигов на этом ПК.</summary>
public static class ConfigLocator
{
    public const int MaxFiles = 400;
    public const long MaxBytes = 50L * 1024 * 1024;

    public static IReadOnlyList<(ConfigLocation Location, string Directory)> ResolveDirectories(
        GameProfile profile, string? installDir, string? steamPath, IEnumerable<string> steamAccountIds, KnownFolders folders)
    {
        var result = new List<(ConfigLocation, string)>();
        foreach (var location in profile.Configs)
        {
            IEnumerable<string> candidates = location.Root switch
            {
                ConfigRoot.InstallDir => installDir is null ? [] : [Path.Combine(installDir, location.RelativePath)],
                ConfigRoot.SteamUserdata => steamPath is null ? [] : steamAccountIds.Select(id => Path.Combine(steamPath, "userdata", id, location.RelativePath)),
                ConfigRoot.Documents => [Path.Combine(folders.Documents, location.RelativePath)],
                ConfigRoot.LocalAppData => [Path.Combine(folders.LocalAppData, location.RelativePath)],
                ConfigRoot.RoamingAppData => [Path.Combine(folders.RoamingAppData, location.RelativePath)],
                ConfigRoot.SavedGames => [Path.Combine(folders.SavedGames, location.RelativePath)],
                _ => [],
            };
            foreach (var dir in candidates)
            {
                if (Directory.Exists(dir))
                {
                    result.Add((location, dir));
                }
            }
        }

        return result;
    }

    /// <summary>Файлы для бэкапа. Ограничения по числу и объёму — чтобы случайно не скопировать гигабайты (например, демки).</summary>
    public static IReadOnlyList<string> CollectFiles(IEnumerable<(ConfigLocation Location, string Directory)> dirs)
    {
        var files = new List<string>();
        long total = 0;
        foreach (var (location, dir) in dirs)
        {
            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateFiles(dir, location.Pattern, location.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                foreach (var file in found)
                {
                    var info = new FileInfo(file);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 10 * 1024 * 1024)
                    {
                        continue;
                    }

                    if (files.Count >= MaxFiles || total + info.Length > MaxBytes)
                    {
                        return files;
                    }

                    files.Add(info.FullName);
                    total += info.Length;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Папка недоступна — пропускаем её, остальные конфиги всё равно сохраняем.
                continue;
            }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Метка бэкапов игры (для фильтрации списка «бэкапы этой игры»).</summary>
    public static string BackupTag(GameProfile profile) => "game:" + profile.Id;
}
