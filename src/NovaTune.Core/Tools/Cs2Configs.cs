using NovaTune.Core.Localization;

namespace NovaTune.Core.Tools;

public sealed record NovaConfig(string Id, string FileName, LocText Name, LocText Description, string Content);

/// <summary>
/// Три конфига NOVA TUNE для CS2. Только стандартные консольные переменные игры — без мультибиндов
/// (Valve запретила бинды с несколькими действиями в 2024 году) и без серверных команд.
/// Если переменной нет в текущей версии игры, CS2 просто напишет «Unknown command» и продолжит.
/// </summary>
public static class Cs2NovaConfigs
{
    private const string Header = "// NOVA TUNE — {0}\r\n// Подключение: Steam → CS2 → Свойства → Параметры запуска: +exec {1}\r\n";

    public static readonly IReadOnlyList<NovaConfig> All =
    [
        new("competitive", "nova_competitive.cfg",
            new("Competitive", "Competitive"),
            new("Максимум информации и минимум отвлекающего: широкое оружие сбоку, подробный радар, без автосмены оружия и подсказок.", "Maximum information, minimum distraction: wide side-positioned viewmodel, detailed radar, no auto weapon switch or hints."),
            Build("Competitive", "nova_competitive",
            [
                "fps_max 0",
                "fps_max_ui 120",
                "cl_autowepswitch 0",
                "cl_use_opens_buy_menu 0",
                "cl_sniper_delay_unscope 0",
                "gameinstructor_enable 0",
                "cl_showloadout 1",
                "cl_radar_always_centered 0",
                "cl_radar_rotate 1",
                "cl_radar_scale 0.4",
                "cl_hud_radar_scale 1.15",
                "cl_radar_square_with_scoreboard 1",
                "viewmodel_fov 68",
                "viewmodel_offset_x 2.5",
                "viewmodel_offset_y 0",
                "viewmodel_offset_z -1.5",
                "snd_mute_losefocus 0",
                "mm_dedicated_search_maxping 80",
            ])),
        new("comfort", "nova_comfort.cfg",
            new("Comfort", "Comfort"),
            new("Для долгих сессий: ограничение FPS снижает нагрев и шум, оружие ближе к центру, радар крупнее, звук приглушается при сворачивании.", "For long sessions: an FPS cap reduces heat and noise, viewmodel closer to centre, larger radar, sound mutes when minimised."),
            Build("Comfort", "nova_comfort",
            [
                "fps_max 240",
                "fps_max_ui 60",
                "cl_autowepswitch 0",
                "gameinstructor_enable 0",
                "cl_radar_always_centered 1",
                "cl_radar_rotate 1",
                "cl_radar_scale 0.5",
                "cl_hud_radar_scale 1.2",
                "viewmodel_fov 62",
                "viewmodel_offset_x 1",
                "viewmodel_offset_y 1",
                "viewmodel_offset_z -1",
                "snd_mute_losefocus 1",
            ])),
        new("cleanhud", "nova_cleanhud.cfg",
            new("Clean HUD", "Clean HUD"),
            new("Минимальный интерфейс для стримов и записи: уменьшенный HUD, клавиша F6 скрывает всё, кроме килфида.", "Minimal UI for streaming and recording: smaller HUD, F6 hides everything except the kill feed."),
            Build("Clean HUD", "nova_cleanhud",
            [
                "hud_scaling 0.8",
                "cl_hud_radar_scale 0.9",
                "cl_showloadout 0",
                "gameinstructor_enable 0",
                "viewmodel_fov 68",
                "bind F6 \"toggle cl_draw_only_deathnotices\"",
            ])),
    ];

    private static string Build(string title, string execName, IEnumerable<string> lines) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Header, title, execName)
        + string.Join("\r\n", lines) + "\r\n"
        + $"echo \"NOVA TUNE: {title} loaded\"\r\n";
}
