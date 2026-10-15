using System.ComponentModel;
using System.Diagnostics;
using NovaTune.Core.Common;
using NovaTune.Core.Elevation;
using NovaTune.Core.Localization;
using NovaTune.Core.Logging;

namespace NovaTune.Platform;

/// <summary>
/// Запуск отдельных операций с правами администратора: тот же EXE запускается через «runas»
/// с параметром <c>--elevated-task &lt;путь к JSON&gt;</c>, ждём завершения и читаем результат.
/// Если приложение уже запущено с правами администратора — операция выполняется в процессе.
/// </summary>
public sealed class ElevationService(AppPaths paths, ILog log)
{
    public const string Argument = "--elevated-task";
    private const int ErrorCancelled = 1223;

    /// <summary>Выполняет операцию; отказ UAC — <see cref="ElevationCancelledException"/>, ошибка — <see cref="InvalidOperationException"/>.</summary>
    public void RunOrThrow(ElevatedTaskRequest request)
    {
        var result = Run(request);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? Loc.T("Операция с правами администратора не выполнена.", "The elevated operation failed."));
        }
    }

    public Task<ElevatedTaskResult> RunAsync(ElevatedTaskRequest request, CancellationToken ct = default) =>
        Task.Run(() => Run(request), ct);

    public ElevatedTaskResult Run(ElevatedTaskRequest request)
    {
        var (ok, reason) = ElevatedTaskValidator.Validate(request);
        if (!ok)
        {
            return new ElevatedTaskResult(false, reason);
        }

        if (OsInfo.IsElevated())
        {
            return ElevatedTaskHost.Execute(request, log);
        }

        paths.EnsureCreated();
        var requestPath = Path.Combine(paths.Temp, $"task-{Guid.NewGuid():N}.json");
        var resultPath = requestPath + ".result.json";
        Json.WriteAtomic(requestPath, request);
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к EXE.");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add(Argument);
            psi.ArgumentList.Add(requestPath);

            log.Info($"Запрос прав администратора: {request.Operation} {request.Key}\\{request.Name} {request.DriveLetter}");
            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                log.Info("Пользователь отклонил запрос UAC");
                throw new ElevationCancelledException();
            }

            if (process is null)
            {
                return new ElevatedTaskResult(false, Loc.T("Не удалось запустить процесс с правами администратора.", "Failed to start the elevated process."));
            }

            using (process)
            {
                // TRIM/оптимизация HDD может длиться долго — ждём до 30 минут.
                if (!process.WaitForExit(TimeSpan.FromMinutes(30)))
                {
                    return new ElevatedTaskResult(false, Loc.T("Операция не завершилась за 30 минут.", "The operation did not finish within 30 minutes."));
                }
            }

            if (!File.Exists(resultPath))
            {
                return new ElevatedTaskResult(false, Loc.T("Повышенный процесс не вернул результат.", "The elevated process returned no result."));
            }

            return Json.Read<ElevatedTaskResult>(resultPath) ?? new ElevatedTaskResult(false, "Пустой результат.");
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            log.Warn("Не удалось удалить временный файл " + path, ex);
        }
    }
}
