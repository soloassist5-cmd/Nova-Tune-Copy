using System.Globalization;
using System.Text;
using NovaTune.Core.Analysis;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;

namespace NovaTune.Core.Steam;

public enum Cs2Profile
{
    Competitive,
    Balanced,
    Quality,
    Laptop,
}

public enum GpuTier
{
    Unknown,
    Low,
    Mid,
    High,
}

public sealed record ReadinessCheck(string Title, CriterionLevel Level, string Detail);

public sealed record Cs2Recommendation(string Setting, string Value, string Why);

/// <summary>Входные данные для отчёта о готовности.</summary>
public sealed record Cs2ReadinessInput(
    HardwareSummary Hardware,
    VolumeSummary? GameVolume,
    bool? GameModeOn,
    bool? PowerSaverActive,
    bool? GameDvrOff,
    double? CpuLoadAverage,
    double? RamUsedPercent);

/// <summary>
/// Рекомендации для CS2 на основе реального железа. Это стартовая точка, а не гарантия:
/// эффект любых настроек нужно проверять замером FPS/frametime (раздел Reports).
/// </summary>
public static class Cs2Advisor
{
    /// <summary>Минимальные требования Valve: 4 аппаратных потока CPU, 8 ГБ ОЗУ, видеокарта с 1 ГБ и DirectX 11 / SM 5.0, 85 ГБ на диске.</summary>
    public const int MinThreads = 4;

    public const ulong MinRamBytes = 8UL * 1024 * 1024 * 1024;

    public const ulong MinVramBytes = 1UL * 1024 * 1024 * 1024;

    /// <summary>Запас свободного места для обновлений игры.</summary>
    public const ulong RecommendedFreeBytes = 15UL * 1024 * 1024 * 1024;

    public static GpuTier ClassifyGpu(GpuSummary? gpu)
    {
        if (gpu is null)
        {
            return GpuTier.Unknown;
        }

        if (gpu.IsIntegrated)
        {
            return GpuTier.Low;
        }

        return gpu.VramBytes switch
        {
            null => GpuTier.Unknown,
            < 4UL * 1024 * 1024 * 1024 => GpuTier.Low,
            < 8UL * 1024 * 1024 * 1024 => GpuTier.Mid,
            _ => GpuTier.High,
        };
    }

    public static Cs2Profile SuggestProfile(HardwareSummary hw) => hw.IsLaptop ? Cs2Profile.Laptop : Cs2Profile.Competitive;

    public static IReadOnlyList<ReadinessCheck> Readiness(Cs2ReadinessInput input)
    {
        var hw = input.Hardware;
        var checks = new List<ReadinessCheck>();

        if (input.GameVolume is { } vol)
        {
            var level = vol.FreeBytes >= RecommendedFreeBytes ? CriterionLevel.Good : vol.FreeBytes >= 5UL * 1024 * 1024 * 1024 ? CriterionLevel.Warning : CriterionLevel.Bad;
            checks.Add(new(
                Loc.T($"Свободно на диске игры ({vol.Letter}:)", $"Free space on game drive ({vol.Letter}:)"),
                level,
                Loc.T($"{Units.Bytes(vol.FreeBytes)}. Для обновлений CS2 желательно от 15 ГБ.", $"{Units.Bytes(vol.FreeBytes)}. 15 GB or more is advisable for CS2 updates.")));
        }

        if (hw.CpuThreads is { } threads)
        {
            checks.Add(new(
                Loc.T("Потоки процессора", "CPU threads"),
                threads >= MinThreads ? CriterionLevel.Good : CriterionLevel.Bad,
                Loc.T($"{threads} (минимум Valve — {MinThreads}).", $"{threads} (Valve minimum: {MinThreads}).")));
        }

        if (hw.RamTotalBytes is { } ram)
        {
            // Windows резервирует часть памяти, поэтому 8 ГБ модулей видны как ~7,9 ГБ — допускаем 5% погрешности.
            checks.Add(new(
                Loc.T("Оперативная память", "System memory"),
                ram >= MinRamBytes * 95 / 100 ? (ram >= 2 * MinRamBytes * 95 / 100 ? CriterionLevel.Good : CriterionLevel.Warning) : CriterionLevel.Bad,
                Loc.T($"{Units.Bytes(ram)} (минимум — 8 ГБ, комфортно — 16 ГБ).", $"{Units.Bytes(ram)} (minimum 8 GB, comfortable 16 GB).")));
        }

        if (hw.PrimaryGpu is { } gpu)
        {
            var tier = ClassifyGpu(gpu);
            var vram = gpu.VramBytes is { } v ? Units.Bytes(v) : Loc.T("объём неизвестен", "size unknown");
            checks.Add(new(
                Loc.T("Видеокарта", "Graphics card"),
                gpu.VramBytes is { } vb && vb < MinVramBytes ? CriterionLevel.Bad : tier == GpuTier.Low ? CriterionLevel.Warning : CriterionLevel.Good,
                $"{gpu.Name}, {vram}" + (gpu.IsIntegrated ? Loc.T(" (встроенная)", " (integrated)") : "")));
        }

        if (input.GameModeOn is { } gm)
        {
            checks.Add(new(Loc.T("Игровой режим Windows", "Windows Game Mode"), gm ? CriterionLevel.Good : CriterionLevel.Warning, gm ? Loc.T("Включён.", "On.") : Loc.T("Выключен.", "Off.")));
        }

        if (input.PowerSaverActive is { } saver)
        {
            checks.Add(new(
                Loc.T("План электропитания", "Power plan"),
                saver ? CriterionLevel.Bad : CriterionLevel.Good,
                saver ? Loc.T("«Экономия энергии» — ограничивает частоты.", "\"Power saver\" limits clocks.") : Loc.T("Не ограничивает производительность.", "Does not limit performance.")));
        }

        if (input.GameDvrOff is { } dvrOff)
        {
            checks.Add(new(
                Loc.T("Фоновая запись Game Bar", "Game Bar background recording"),
                dvrOff ? CriterionLevel.Good : CriterionLevel.Warning,
                dvrOff ? Loc.T("Выключена.", "Off.") : Loc.T("Включена или не настроена.", "On or not configured.")));
        }

        if (input.CpuLoadAverage is { } cpu)
        {
            checks.Add(new(
                Loc.T("Фоновая нагрузка CPU", "Background CPU load"),
                cpu < 15 ? CriterionLevel.Good : cpu < 35 ? CriterionLevel.Warning : CriterionLevel.Bad,
                Loc.T($"{cpu:0}% в среднем за минуту.", $"{cpu:0}% average over a minute.")));
        }

        if (hw.IsLaptop)
        {
            checks.Add(new(
                Loc.T("Питание ноутбука", "Laptop power"),
                hw.OnBattery ? CriterionLevel.Warning : CriterionLevel.Good,
                hw.OnBattery ? Loc.T("Работа от батареи — производительность обычно снижена. Подключите зарядку.", "Running on battery — performance is usually reduced. Plug in the charger.") : Loc.T("Подключено к сети.", "Plugged in.")));
        }

        return checks;
    }

    public static IReadOnlyList<Cs2Recommendation> Recommend(Cs2Profile profile, HardwareSummary hw)
    {
        var tier = ClassifyGpu(hw.PrimaryGpu);
        var isNvidia = hw.PrimaryGpu?.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true;
        var low = tier is GpuTier.Low or GpuTier.Unknown;
        var high = tier == GpuTier.High;
        var list = new List<Cs2Recommendation>();

        void Add(string ru, string en, string valueRu, string valueEn, string whyRu, string whyEn) =>
            list.Add(new Cs2Recommendation(Loc.T(ru, en), Loc.T(valueRu, valueEn), Loc.T(whyRu, whyEn)));

        switch (profile)
        {
            case Cs2Profile.Competitive:
                Add("Усиление контраста игроков (Boost Player Contrast)", "Boost Player Contrast", "Вкл.", "On", "Модели игроков лучше выделяются на фоне.", "Player models stand out better.");
                Add("Вертикальная синхронизация", "Wait for Vertical Sync", "Выкл.", "Off", "V-Sync добавляет задержку ввода.", "V-Sync adds input latency.");
                if (isNvidia)
                {
                    Add("NVIDIA Reflex", "NVIDIA Reflex Low Latency", "Вкл.", "On", "Снижает задержку системы при упоре в GPU.", "Reduces system latency when GPU-bound.");
                }

                Add("Сглаживание (MSAA)", "Multisampling Anti-Aliasing", high ? "4x" : low ? "Нет или CMAA2" : "2x", high ? "4x" : low ? "None or CMAA2" : "2x", "Баланс чёткости краёв и FPS для вашей видеокарты.", "Balance of edge clarity and FPS for your GPU.");
                Add("Качество теней", "Global Shadow Quality", high ? "Высокое" : low ? "Низкое" : "Среднее", high ? "High" : low ? "Low" : "Medium", "Тени выдают позицию игроков; на слабой видеокарте понижайте.", "Shadows reveal player positions; lower on weak GPUs.");
                Add("Детализация шейдеров и частиц", "Shader / Particle Detail", "Низкое", "Low", "Меньше визуального шума от эффектов.", "Less visual noise from effects.");
                Add("Фоновое затенение (AO)", "Ambient Occlusion", "Выкл.", "Disabled", "Заметно стоит FPS, на видимость противников почти не влияет.", "Costs FPS, barely affects enemy visibility.");
                Add("FidelityFX Super Resolution", "FidelityFX Super Resolution", low ? "Качество — только если FPS ниже частоты монитора" : "Выкл.", low ? "Quality — only if FPS is below refresh rate" : "Off", "Апскейл размывает картинку.", "Upscaling blurs the image.");
                Add("Ограничение FPS (fps_max)", "Frame cap (fps_max)", "0 (без ограничения)", "0 (uncapped)", "Минимальная задержка; если frametime скачет — попробуйте ограничение.", "Lowest latency; if frametime is uneven, try a cap.");
                break;

            case Cs2Profile.Balanced:
                Add("Усиление контраста игроков", "Boost Player Contrast", "Вкл.", "On", "Видимость без потерь FPS.", "Visibility at no FPS cost.");
                Add("Сглаживание (MSAA)", "Multisampling Anti-Aliasing", low ? "2x" : "4x", low ? "2x" : "4x", "Аккуратные края без сильной потери FPS.", "Clean edges without a big FPS hit.");
                Add("Качество теней", "Global Shadow Quality", low ? "Среднее" : "Высокое", low ? "Medium" : "High", "", "");
                Add("Текстуры", "Model / Texture Detail", high ? "Высокое" : "Среднее", high ? "High" : "Medium", "Зависит от видеопамяти.", "Depends on video memory.");
                Add("Фоновое затенение (AO)", "Ambient Occlusion", low ? "Выкл." : "Вкл.", low ? "Disabled" : "Enabled", "", "");
                Add("Ограничение FPS (fps_max)", "Frame cap (fps_max)", "0", "0", "", "");
                break;

            case Cs2Profile.Quality:
                Add("Сглаживание (MSAA)", "Multisampling Anti-Aliasing", high ? "8x" : "4x", high ? "8x" : "4x", "", "");
                Add("Качество теней", "Global Shadow Quality", "Очень высокое", "Very High", "", "");
                Add("Текстуры и шейдеры", "Texture / Shader Detail", high ? "Высокое" : "Среднее", high ? "High" : "Medium", "Высокие текстуры требуют 6–8 ГБ видеопамяти.", "High textures need 6–8 GB of VRAM.");
                Add("HDR", "High Dynamic Range", "Качество", "Quality", "", "");
                Add("Фоновое затенение (AO)", "Ambient Occlusion", "Вкл.", "Enabled", "", "");
                break;

            case Cs2Profile.Laptop:
                var cap = hw.MaxRefreshHz ?? 60;
                Add("Питание", "Power", "Подключить зарядку", "Plug in the charger", "От батареи CPU и GPU работают на сниженных частотах.", "On battery the CPU and GPU run at reduced clocks.");
                Add("Ограничение FPS (fps_max)", "Frame cap (fps_max)", cap.ToString(CultureInfo.InvariantCulture), cap.ToString(CultureInfo.InvariantCulture), "Ограничение по частоте экрана снижает нагрев и троттлинг.", "Capping at the refresh rate reduces heat and throttling.");
                Add("Сглаживание (MSAA)", "Multisampling Anti-Aliasing", low ? "Нет или CMAA2" : "2x", low ? "None or CMAA2" : "2x", "", "");
                Add("Качество теней", "Global Shadow Quality", "Низкое", "Low", "", "");
                Add("FidelityFX Super Resolution", "FidelityFX Super Resolution", low ? "Качество" : "Выкл.", low ? "Quality" : "Off", "Помогает слабой встроенной графике.", "Helps weak integrated graphics.");
                Add("Видеокарта для игры", "GPU for the game", "Дискретная", "Discrete", "Проверьте в Параметры → Дисплей → Графика, что cs2.exe использует дискретную видеокарту.", "Check Settings → Display → Graphics that cs2.exe uses the discrete GPU.");
                break;
        }

        return list.Where(r => r.Setting.Length > 0).ToList();
    }

    /// <summary>Рекомендуемое ограничение FPS для конфига (0 — без ограничения).</summary>
    public static int SuggestedFpsMax(Cs2Profile profile, HardwareSummary hw) =>
        profile == Cs2Profile.Laptop ? hw.MaxRefreshHz ?? 60 : 0;

    /// <summary>
    /// Содержимое novatune.cfg. Подключается параметром запуска «+exec novatune». Содержит только fps_max —
    /// остальные графические настройки CS2 хранит в своих файлах и перезаписывает их сама.
    /// </summary>
    public static string BuildConfig(Cs2Profile profile, int fpsMax, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("// NOVA TUNE — profile ").Append(profile).Append(", ").Append(now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("// Steam > CS2 > Properties > Launch options: +exec novatune\r\n");
        sb.Append("fps_max \"").Append(Math.Clamp(fpsMax, 0, 1000).ToString(CultureInfo.InvariantCulture)).Append("\"\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Пояснения к популярным параметрам запуска. Многие советы из интернета для CS2 бесполезны —
    /// говорим об этом прямо.
    /// </summary>
    public static IReadOnlyList<(string Option, string Explanation)> LaunchOptionNotes() =>
    [
        ("+exec novatune", Loc.T("Выполняет novatune.cfg, созданный NOVA TUNE.", "Runs the novatune.cfg created by NOVA TUNE.")),
        ("-novid", Loc.T("Пропуск вступительного видео; на FPS не влияет.", "Skips the intro video; does not affect FPS.")),
        ("-fullscreen", Loc.T("Запуск в полноэкранном режиме — обычно самая низкая задержка вывода.", "Starts in exclusive fullscreen — usually the lowest presentation latency.")),
        ("-high", Loc.T("Повышает приоритет процесса. Эффект спорный, иногда вызывает подвисания звука или системы — не рекомендуем.", "Raises process priority. Disputed effect, may cause audio/system hitches — not recommended.")),
        ("-threads N", Loc.T("Движок сам выбирает число потоков; ручное значение обычно не помогает.", "The engine picks thread count itself; a manual value usually does not help.")),
        ("-tickrate 128", Loc.T("В CS2 используется subtick — параметр не влияет на онлайн-игру.", "CS2 uses subtick — this has no effect in online play.")),
    ];
}
