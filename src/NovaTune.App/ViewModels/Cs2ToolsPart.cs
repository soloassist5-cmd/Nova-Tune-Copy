using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.App.Services;
using NovaTune.Core.Backup;
using NovaTune.Core.Localization;
using NovaTune.Core.Tools;

namespace NovaTune.App.ViewModels;

public sealed record ColorPreset(string Name, byte R, byte G, byte B);

/// <summary>Вкладки CS2: генератор прицела, калькулятор чувствительности, тренировочный конфиг.</summary>
public sealed partial class Cs2ViewModel
{
    [ObservableProperty] private string _tab = "overview";

    // ───────── Прицел ─────────

    public IReadOnlyList<ColorPreset> Colors { get; } =
    [
        new(Loc.T("Зелёный", "Green"), 0, 255, 0),
        new(Loc.T("Голубой", "Cyan"), 0, 255, 255),
        new(Loc.T("Жёлтый", "Yellow"), 255, 255, 0),
        new(Loc.T("Розовый", "Pink"), 255, 0, 255),
        new(Loc.T("Белый", "White"), 255, 255, 255),
        new(Loc.T("Красный", "Red"), 255, 0, 0),
    ];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Crosshair), nameof(CrosshairLine))] private double _crossSize = 2.5;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Crosshair), nameof(CrosshairLine))] private double _crossThickness = 0.5;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Crosshair), nameof(CrosshairLine))] private double _crossGap = -2;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Crosshair), nameof(CrosshairLine))] private bool _crossDot;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Crosshair), nameof(CrosshairLine))] private bool _crossOutline = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Crosshair), nameof(CrosshairLine))] private bool _crossT;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Crosshair), nameof(CrosshairLine))] private double _crossAlpha = 255;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Crosshair), nameof(CrosshairLine))] private ColorPreset? _crossColor;

    public CrosshairSettings Crosshair => new()
    {
        Size = Math.Round(CrossSize, 1),
        Thickness = Math.Round(CrossThickness, 1),
        Gap = Math.Round(CrossGap, 1),
        Dot = CrossDot,
        Outline = CrossOutline,
        TStyle = CrossT,
        Alpha = (byte)Math.Clamp(CrossAlpha, 0, 255),
        R = (CrossColor ?? Colors[0]).R,
        G = (CrossColor ?? Colors[0]).G,
        B = (CrossColor ?? Colors[0]).B,
    };

    public string CrosshairLine => Crosshair.ToCommands(singleLine: true);

    [RelayCommand]
    private void CopyCrosshair() => CopyText(CrosshairLine, Loc.T("Команды прицела скопированы — вставьте их в консоль CS2 (~).", "Crosshair commands copied — paste them into the CS2 console (~)."));

    /// <summary>Сохранить в game\csgo\cfg\crosshair.cfg (через бэкап); подключается командой «exec crosshair».</summary>
    [RelayCommand]
    private Task SaveCrosshair() => SaveCfgAsync("crosshair.cfg", "// NOVA TUNE — crosshair\r\n" + Crosshair.ToCommands(singleLine: false), Loc.T("Прицел CS2", "CS2 crosshair"));

    // ───────── Чувствительность ─────────

    public IReadOnlyList<SensGame> SensGames => SensitivityCalculator.Games;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SensResult))] private string _sensDpi = "800";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SensResult))] private string _sensValue = "1.0";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SensResult))] private SensGame? _sensSource;

    public string SensResult
    {
        get
        {
            if (!TryNum(SensDpi, out var dpi) || !TryNum(SensValue, out var sens) || dpi <= 0 || sens <= 0 || dpi > 50000 || sens > 1000)
            {
                return Loc.T("Введите DPI и чувствительность (числа больше нуля).", "Enter DPI and sensitivity (numbers above zero).");
            }

            var game = SensSource ?? SensGames[0];
            var cs2 = SensitivityCalculator.ToCs2(sens, game.Yaw);
            var sb = new StringBuilder();
            if (game.Id != "cs2" && game.Id != "apex")
            {
                sb.AppendLine(Loc.T($"Чувствительность для CS2: {cs2:0.###}", $"CS2 sensitivity: {cs2:0.###}"));
            }
            else if (game.Id == "apex")
            {
                sb.AppendLine(Loc.T($"Apex и CS2 используют одинаковый yaw — в CS2 то же значение: {cs2:0.###}", $"Apex and CS2 share the same yaw — same value in CS2: {cs2:0.###}"));
            }

            sb.AppendLine($"eDPI (CS2): {SensitivityCalculator.Edpi(dpi, cs2):0}");
            sb.AppendLine(Loc.T($"Оборот 360°: {SensitivityCalculator.CmPer360(dpi, cs2):0.0} см", $"360° turn: {SensitivityCalculator.CmPer360(dpi, cs2):0.0} cm ({SensitivityCalculator.CmPer360(dpi, cs2) / 2.54:0.0} in)"));
            sb.Append(Loc.T(
                "Для ориентира: у большинства профессиональных игроков CS eDPI 600–1200 (примерно 35–70 см на 360°).",
                "For reference: most professional CS players use 600–1200 eDPI (about 35–70 cm per 360°)."));
            return sb.ToString();
        }
    }

    [RelayCommand]
    private void CopySens()
    {
        if (TryNum(SensValue, out var sens))
        {
            var cs2 = SensitivityCalculator.ToCs2(sens, (SensSource ?? SensGames[0]).Yaw);
            CopyText("sensitivity " + cs2.ToString("0.###", CultureInfo.InvariantCulture), Loc.T("Команда sensitivity скопирована", "sensitivity command copied"));
        }
    }

    private static bool TryNum(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ───────── Тренировка ─────────

    public IReadOnlyList<string> Maps => PracticeConfig.Maps;

    [ObservableProperty] private string _practiceMap = "de_mirage";
    [ObservableProperty] private bool _practiceAmmo = true;
    [ObservableProperty] private bool _practiceTrajectory = true;
    [ObservableProperty] private bool _practiceImpacts = true;
    [ObservableProperty] private bool _practiceNoBots = true;

    [RelayCommand]
    private Task CreatePractice() => SaveCfgAsync(
        "practice.cfg",
        PracticeConfig.Build(PracticeAmmo, PracticeTrajectory, PracticeImpacts, PracticeNoBots),
        Loc.T("Тренировочный конфиг CS2", "CS2 practice config"));

    /// <summary>Запуск CS2 на выбранной карте с practice.cfg. Steam покажет окно подтверждения параметров.</summary>
    [RelayCommand]
    private async Task LaunchPractice()
    {
        if (_cs2 is null)
        {
            return;
        }

        if (!File.Exists(Path.Combine(_cs2.GameCfgDir, "practice.cfg")))
        {
            await CreatePractice();
            if (!File.Exists(Path.Combine(_cs2.GameCfgDir, "practice.cfg")))
            {
                return;
            }
        }

        ChangeRunner.OpenUrl(PracticeConfig.LaunchUrl(PracticeMap));
        S.Journal.Add("cs2", Loc.T("Запуск тренировки: ", "Practice launch: ") + PracticeMap, true);
    }

    // ───────── Общее ─────────

    private Task SaveCfgAsync(string fileName, string content, string title) => RunSafeAsync(async () =>
    {
        if (_cs2 is null)
        {
            S.Toasts.Show(Loc.T("Сначала найдите CS2 на вкладке «Обзор»", "Locate CS2 on the Overview tab first"), DialogKind.Warning);
            return;
        }

        var path = Path.Combine(_cs2.GameCfgDir, fileName);
        await ChangeRunner.ApplyAsync(title, [new FileWriteChange(path, Encoding.UTF8.GetBytes(content)) { Title = fileName }]);
    }, Loc.T("Не удалось сохранить конфиг", "Failed to save config"));

    private void CopyText(string text, string toast)
    {
        try
        {
            Clipboard.SetText(text);
            S.Toasts.Show(toast, DialogKind.Success);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            S.Log.Warn("Буфер обмена занят", ex);
            S.Toasts.Show(Loc.T("Буфер обмена занят — попробуйте ещё раз", "Clipboard is busy — try again"), DialogKind.Warning);
        }
    }
}
