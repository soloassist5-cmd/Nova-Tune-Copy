using System.Globalization;
using System.Text;

namespace NovaTune.Core.Tools;

/// <summary>Игра-источник для перевода чувствительности. Yaw — градусов поворота на единицу движения мыши × чувствительность.</summary>
public sealed record SensGame(string Id, string Name, double Yaw);

/// <summary>
/// Калькулятор чувствительности. CS2 (как и CS:GO, Apex) использует m_yaw = 0.022°;
/// Valorant — 0.07°, Overwatch 2 — 0.0066°. Перевод — через равный угол поворота на дюйм движения мыши.
/// </summary>
public static class SensitivityCalculator
{
    public const double Cs2Yaw = 0.022;

    public static readonly IReadOnlyList<SensGame> Games =
    [
        new("cs2", "Counter-Strike 2 / CS:GO", Cs2Yaw),
        new("apex", "Apex Legends", 0.022),
        new("valorant", "Valorant", 0.07),
        new("overwatch", "Overwatch 2", 0.0066),
    ];

    public static double Edpi(double dpi, double sens) => dpi * sens;

    /// <summary>Сантиметров движения мыши на полный оборот 360°.</summary>
    public static double CmPer360(double dpi, double sens, double yaw = Cs2Yaw)
    {
        if (dpi <= 0 || sens <= 0 || yaw <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), "DPI, чувствительность и yaw должны быть больше нуля.");
        }

        return 360.0 / (sens * yaw * dpi) * 2.54;
    }

    /// <summary>Чувствительность в CS2 при том же DPI, дающая тот же угол поворота, что и в исходной игре.</summary>
    public static double ToCs2(double sourceSens, double sourceYaw) => sourceSens * sourceYaw / Cs2Yaw;
}

/// <summary>Настройки прицела CS2 (стандартные консольные переменные cl_crosshair*).</summary>
public sealed record CrosshairSettings
{
    /// <summary>cl_crosshairstyle: 4 — классический статичный (самый популярный), 2 — классический, 5 — классический с разбросом.</summary>
    public int Style { get; init; } = 4;

    public double Size { get; init; } = 2.5;

    public double Thickness { get; init; } = 0.5;

    public double Gap { get; init; } = -2;

    public bool Dot { get; init; }

    public bool Outline { get; init; } = true;

    public double OutlineThickness { get; init; } = 1;

    public bool TStyle { get; init; }

    public byte R { get; init; }

    public byte G { get; init; } = 255;

    public byte B { get; init; }

    public byte Alpha { get; init; } = 255;

    /// <summary>Команды для консоли CS2 (одна строка через «;») или для .cfg (по строке на команду).</summary>
    public string ToCommands(bool singleLine)
    {
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        string[] commands =
        [
            $"cl_crosshairstyle {Style}",
            $"cl_crosshairsize {F(Math.Clamp(Size, 0, 100))}",
            $"cl_crosshairthickness {F(Math.Clamp(Thickness, 0, 10))}",
            $"cl_crosshairgap {F(Math.Clamp(Gap, -10, 10))}",
            $"cl_crosshairdot {(Dot ? 1 : 0)}",
            $"cl_crosshair_t {(TStyle ? 1 : 0)}",
            $"cl_crosshair_drawoutline {(Outline ? 1 : 0)}",
            $"cl_crosshair_outlinethickness {F(Math.Clamp(OutlineThickness, 0, 3))}",
            "cl_crosshaircolor 5",
            $"cl_crosshaircolor_r {R}",
            $"cl_crosshaircolor_g {G}",
            $"cl_crosshaircolor_b {B}",
            "cl_crosshairusealpha 1",
            $"cl_crosshairalpha {Alpha}",
        ];
        return singleLine ? string.Join("; ", commands) : string.Join("\r\n", commands) + "\r\n";
    }
}

/// <summary>
/// Тренировочный конфиг для локального сервера (карта, запущенная самим игроком).
/// sv_cheats и серверные переменные на официальных серверах не работают и VAC не затрагивают.
/// </summary>
public static class PracticeConfig
{
    public static readonly IReadOnlyList<string> Maps =
        ["de_mirage", "de_inferno", "de_dust2", "de_nuke", "de_ancient", "de_anubis", "de_overpass", "de_train", "de_vertigo"];

    public static string Build(bool infiniteAmmo, bool grenadeTrajectory, bool showImpacts, bool noBots)
    {
        var sb = new StringBuilder();
        sb.Append("// NOVA TUNE — practice.cfg (только локальный сервер)\r\n");
        sb.Append("sv_cheats 1\r\n");
        sb.Append("mp_warmup_end\r\n");
        sb.Append("mp_freezetime 0\r\n");
        sb.Append("mp_roundtime_defuse 60\r\n");
        sb.Append("mp_roundtime 60\r\n");
        sb.Append("mp_buytime 9999\r\n");
        sb.Append("mp_buy_anywhere 1\r\n");
        sb.Append("mp_maxmoney 60000\r\n");
        sb.Append("mp_startmoney 60000\r\n");
        sb.Append("mp_limitteams 0\r\n");
        sb.Append("mp_autoteambalance 0\r\n");
        sb.Append("ammo_grenade_limit_total 5\r\n");
        if (infiniteAmmo)
        {
            sb.Append("sv_infinite_ammo 1\r\n");
        }

        if (grenadeTrajectory)
        {
            sb.Append("sv_grenade_trajectory_prac_pipreview 1\r\n");
        }

        if (showImpacts)
        {
            sb.Append("sv_showimpacts 1\r\n");
        }

        if (noBots)
        {
            sb.Append("bot_kick\r\n");
        }

        sb.Append("mp_restartgame 1\r\n");
        return sb.ToString();
    }

    /// <summary>Ссылка запуска через Steam: карта и выполнение practice.cfg. Steam покажет подтверждение аргументов.</summary>
    public static string LaunchUrl(string map)
    {
        if (!Maps.Contains(map))
        {
            throw new ArgumentException("Неизвестная карта: " + map, nameof(map));
        }

        return $"steam://run/730//+map%20{map}%20+exec%20practice";
    }
}

/// <summary>Статистика теста реакции: последние, лучшее, среднее; фальстарты не учитываются.</summary>
public sealed class ReactionStats
{
    private readonly List<double> _results = [];

    public IReadOnlyList<double> Results => _results;

    public void Add(double ms)
    {
        if (ms is > 0 and < 5000)
        {
            _results.Add(ms);
        }
    }

    public double? Best => _results.Count == 0 ? null : _results.Min();

    /// <summary>Среднее по последним 5 попыткам.</summary>
    public double? AverageLast5 => _results.Count == 0 ? null : _results.TakeLast(5).Average();

    public void Clear() => _results.Clear();
}
