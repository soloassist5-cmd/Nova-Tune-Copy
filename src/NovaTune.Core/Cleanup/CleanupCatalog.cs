using NovaTune.Core.Localization;

namespace NovaTune.Core.Cleanup;

/// <summary>
/// Категории очистки — только данные, которые Windows и программы пересоздают сами.
/// Сознательно НЕ включены: кэш шейдеров DirectX (его удаление вызовет фризы в CS2 при первом запуске),
/// загрузки, документы, сохранения игр, профили браузеров.
/// </summary>
public static class CleanupCatalog
{
    public static IReadOnlyList<CleanupCategory> Create(string localAppData, string userTemp) =>
    [
        new(
            "user-temp",
            new("Временные файлы пользователя", "User temporary files"),
            new(
                "Папка %TEMP%. Удаляются только файлы старше суток — более свежие могут использоваться установщиками и запущенными программами.",
                "The %TEMP% folder. Only files older than one day are removed — newer ones may be used by installers and running apps."),
            [new CleanupRoot(userTemp)],
            TimeSpan.FromDays(1),
            SelectedByDefault: true),
        new(
            "thumbnails",
            new("Кэш миниатюр Проводника", "Explorer thumbnail cache"),
            new(
                "Файлы thumbcache_*.db. Проводник пересоздаст миниатюры при открытии папок. Файлы, занятые Проводником, будут пропущены.",
                "thumbcache_*.db files. Explorer rebuilds thumbnails when folders are opened. Files locked by Explorer are skipped."),
            [new CleanupRoot(Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", Recursive: false)],
            TimeSpan.Zero,
            SelectedByDefault: false),
        new(
            "wer",
            new("Отчёты об ошибках Windows (пользователь)", "Windows Error Reporting (user)"),
            new(
                "Архив и очередь отчётов об ошибках программ. Нужны только для отправки в Microsoft и диагностики сбоев.",
                "Archived and queued application crash reports. Only needed for sending to Microsoft and crash diagnostics."),
            [
                new CleanupRoot(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive")),
                new CleanupRoot(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue")),
            ],
            TimeSpan.FromHours(1),
            SelectedByDefault: true),
        new(
            "crash-dumps",
            new("Дампы памяти программ", "Application crash dumps"),
            new(
                "Файлы *.dmp из %LOCALAPPDATA%\\CrashDumps. Если вы отправляете отчёт разработчику игры — сначала сохраните нужный дамп.",
                "*.dmp files from %LOCALAPPDATA%\\CrashDumps. If you are sending a report to a game developer, keep the dump first."),
            [new CleanupRoot(Path.Combine(localAppData, "CrashDumps"), "*.dmp", Recursive: false)],
            TimeSpan.FromHours(1),
            SelectedByDefault: false),
    ];
}
