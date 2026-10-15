using System.Diagnostics;
using System.Management;
using System.Text.Json;
using NovaTune.Core.Common;
using NovaTune.Core.Elevation;
using NovaTune.Core.Localization;
using NovaTune.Core.Logging;

namespace NovaTune.Platform;

/// <summary>
/// Код повышенного процесса. Повторно проверяет запрос белым списком (запросу из файла не доверяем),
/// выполняет ровно одну операцию и записывает результат рядом с файлом запроса.
/// </summary>
public static class ElevatedTaskHost
{
    /// <summary>Точка входа при запуске EXE с параметром --elevated-task. Возвращает код выхода.</summary>
    public static int RunFromFile(string requestPath, ILog log)
    {
        ElevatedTaskResult result;
        try
        {
            var request = Json.Read<ElevatedTaskRequest>(requestPath) ?? throw new InvalidDataException("Пустой запрос.");
            result = Execute(request, log);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            log.Error("Повышенный процесс: не удалось прочитать запрос", ex);
            result = new ElevatedTaskResult(false, ex.Message);
        }

        try
        {
            Json.WriteAtomic(requestPath + ".result.json", result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("Повышенный процесс: не удалось записать результат", ex);
            return 2;
        }

        return result.Success ? 0 : 1;
    }

    public static ElevatedTaskResult Execute(ElevatedTaskRequest request, ILog log)
    {
        var (ok, reason) = ElevatedTaskValidator.Validate(request);
        if (!ok)
        {
            log.Warn("Повышенный процесс: запрос отклонён белым списком: " + reason);
            return new ElevatedTaskResult(false, reason);
        }

        try
        {
            log.Info($"Повышенный процесс: {request.Operation}");
            return request.Operation switch
            {
                ElevatedOperation.RegistrySet => RegistrySet(request),
                ElevatedOperation.RegistryDelete => RegistryDelete(request),
                ElevatedOperation.VolumeReTrim => Defrag(request.DriveLetter!.Value, "/L", expectSsd: true, log),
                ElevatedOperation.VolumeOptimizeHdd => Defrag(request.DriveLetter!.Value, "/O", expectSsd: false, log),
                ElevatedOperation.CreateRestorePoint => CreateRestorePoint(request.Description!, log),
                ElevatedOperation.ReadStorageReliability => new ElevatedTaskResult(true, null, Json.Serialize(new StorageService(log).ReadReliability())),
                _ => new ElevatedTaskResult(false, "Неизвестная операция."),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error("Повышенный процесс: ошибка операции " + request.Operation, ex);
            return new ElevatedTaskResult(false, ex.Message);
        }
    }

    private static ElevatedTaskResult RegistrySet(ElevatedTaskRequest r)
    {
        RegistrySettingsStore.WriteDirect(r.Hive!.Value, r.Key!, r.Name!, r.Value!);
        return new ElevatedTaskResult(true, null);
    }

    private static ElevatedTaskResult RegistryDelete(ElevatedTaskRequest r)
    {
        RegistrySettingsStore.DeleteDirect(r.Hive!.Value, r.Key!, r.Name!);
        return new ElevatedTaskResult(true, null);
    }

    /// <summary>
    /// Штатная утилита defrag.exe. Тип накопителя проверяется заново здесь же: дефрагментация SSD запрещена,
    /// TRIM на HDD бессмыслен — при несовпадении операция не выполняется.
    /// </summary>
    private static ElevatedTaskResult Defrag(char letter, string mode, bool expectSsd, ILog log)
    {
        var isSsd = new StorageService(log).IsSsd(letter);
        if (isSsd != expectSsd)
        {
            return new ElevatedTaskResult(false, isSsd is null
                ? Loc.T("Тип накопителя не определён однозначно — используйте «Оптимизация дисков» Windows.", "Drive type could not be determined — use Windows \"Optimize Drives\".")
                : expectSsd
                    ? Loc.T("Это HDD — TRIM не применяется.", "This is an HDD — TRIM does not apply.")
                    : Loc.T("Это SSD — дефрагментация SSD не выполняется.", "This is an SSD — SSDs are never defragmented."));
        }

        var defrag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "defrag.exe");
        var psi = new ProcessStartInfo(defrag)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add($"{char.ToUpperInvariant(letter)}:");
        psi.ArgumentList.Add(mode);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("defrag.exe не запустился.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? new ElevatedTaskResult(true, null, output)
            : new ElevatedTaskResult(false, $"defrag.exe: код {process.ExitCode}", output);
    }

    /// <summary>
    /// Точка восстановления через WMI SystemRestore.CreateRestorePoint.
    /// Windows по умолчанию создаёт не больше одной точки за 24 часа — проверяем по номеру последней точки.
    /// </summary>
    private static ElevatedTaskResult CreateRestorePoint(string description, ILog log)
    {
        const string scope = @"\\.\root\default";
        static uint LastSequence() => Wmi.Query(scope, "SELECT SequenceNumber FROM SystemRestore", Wmi.DefaultTimeout)
            .Select(r => (uint)(r.U64("SequenceNumber") ?? 0)).DefaultIfEmpty(0u).Max();

        var before = LastSequence();
        using var cls = new ManagementClass(new ManagementScope(scope), new ManagementPath("SystemRestore"), null);
        using var input = cls.GetMethodParameters("CreateRestorePoint");
        input["Description"] = description;
        input["RestorePointType"] = 12u; // MODIFY_SETTINGS
        input["EventType"] = 100u; // BEGIN_SYSTEM_CHANGE
        using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
        var code = Convert.ToUInt32(output["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
        if (code != 0)
        {
            log.Warn($"CreateRestorePoint вернул {code}");
            return new ElevatedTaskResult(false, code == 1058
                ? Loc.T("Защита системы выключена — точки восстановления не создаются.", "System Protection is turned off — restore points are not created.")
                : Loc.T($"Windows вернула код {code}. Проверьте, что защита системы включена для диска C:.", $"Windows returned code {code}. Make sure System Protection is enabled for drive C:."));
        }

        var after = LastSequence();
        return after > before
            ? new ElevatedTaskResult(true, null, Loc.T("Точка восстановления создана.", "Restore point created."))
            : new ElevatedTaskResult(true, null, Loc.T(
                "Windows приняла запрос, но новая точка не появилась: по умолчанию создаётся не больше одной точки за 24 часа. Последняя существующая точка сохранена.",
                "Windows accepted the request but no new point appeared: by default only one restore point is created per 24 hours. The latest existing point remains."));
    }
}
