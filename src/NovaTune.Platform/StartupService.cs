using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NovaTune.Core.Common;
using NovaTune.Core.Logging;
using NovaTune.Core.Startup;

namespace NovaTune.Platform;

/// <summary>
/// Перечисление автозагрузки: Run/RunOnce (HKCU, HKLM, WOW6432Node), папки автозагрузки и задачи
/// планировщика с триггером «при входе» (только чтение). Состояние вкл/выкл — из StartupApproved, как в диспетчере задач.
/// </summary>
public sealed class StartupService(ISystemSettingsStore store, ILog log)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnce32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce";

    public Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(CancellationToken ct) => Task.Run(() => GetEntries(ct), ct);

    public IReadOnlyList<StartupEntry> GetEntries(CancellationToken ct)
    {
        var list = new List<StartupEntry>();
        ReadRun(list, RegHive.CurrentUser, RunKey, StartupSource.HkcuRun);
        ReadRun(list, RegHive.CurrentUser, RunOnceKey, StartupSource.HkcuRunOnce);
        ReadRun(list, RegHive.LocalMachine, RunKey, StartupSource.HklmRun);
        ReadRun(list, RegHive.LocalMachine, RunOnceKey, StartupSource.HklmRunOnce);
        ReadRun(list, RegHive.LocalMachine, Run32Key, StartupSource.HklmRun32);
        ReadRun(list, RegHive.LocalMachine, RunOnce32Key, StartupSource.HklmRunOnce32);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserStartupFolder);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonStartupFolder);
        ct.ThrowIfCancellationRequested();
        ReadScheduledTasks(list);
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void ReadRun(List<StartupEntry> list, RegHive hive, string key, StartupSource source)
    {
        try
        {
            foreach (var name in store.GetValueNames(hive, key))
            {
                if (name.Length == 0)
                {
                    continue;
                }

                var value = store.ReadValue(hive, key, name);
                var command = value?.Text ?? value?.Display() ?? "";
                var approved = StartupApproved.LocationFor(source, name);
                list.Add(new StartupEntry
                {
                    Name = name,
                    Command = command,
                    Source = source,
                    Approved = approved,
                    Enabled = approved is null ? null : StartupApproved.IsEnabled(store.ReadValue(approved.Hive, approved.Key, approved.ValueName)),
                    Publisher = PublisherOf(command),
                    LooksLikeSystemComponent = StartupApproved.LooksLikeSystemComponent(name, command),
                });
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Warn($"Автозагрузка: нет доступа к {RegPath.Format(hive, key)}", ex);
        }
    }

    private void ReadFolder(List<StartupEntry> list, string folder, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var approved = StartupApproved.LocationFor(source, fileName);
                list.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = file,
                    Source = source,
                    Approved = approved,
                    Enabled = approved is null ? null : StartupApproved.IsEnabled(store.ReadValue(approved.Hive, approved.Key, approved.ValueName)),
                    LooksLikeSystemComponent = StartupApproved.LooksLikeSystemComponent(fileName, file),
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            log.Warn("Автозагрузка: нет доступа к папке " + folder, ex);
        }
    }

    /// <summary>
    /// Задачи планировщика с триггером входа (TASK_TRIGGER_LOGON = 9), кроме встроенных задач из папки \Microsoft.
    /// Только показ: отключение задач может сломать обновление драйверов и программ.
    /// </summary>
    private void ReadScheduledTasks(List<StartupEntry> list)
    {
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                return;
            }

            service = Activator.CreateInstance(type);
            dynamic svc = service!;
            svc.Connect();
            var pending = new Stack<dynamic>();
            pending.Push(svc.GetFolder("\\"));
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                string path = folder.Path;
                if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var task in folder.GetTasks(1))
                {
                    try
                    {
                        var hasLogon = false;
                        foreach (var trigger in task.Definition.Triggers)
                        {
                            if ((int)trigger.Type == 9)
                            {
                                hasLogon = true;
                            }
                        }

                        if (!hasLogon)
                        {
                            continue;
                        }

                        string command = "";
                        foreach (var action in task.Definition.Actions)
                        {
                            if ((int)action.Type == 0)
                            {
                                command = ((string)action.Path + " " + (string)(action.Arguments ?? "")).Trim();
                                break;
                            }
                        }

                        list.Add(new StartupEntry
                        {
                            Name = (string)task.Name,
                            Command = string.IsNullOrEmpty(command) ? (string)task.Path : command,
                            Source = StartupSource.ScheduledTaskLogon,
                            Enabled = (bool)task.Enabled,
                            Publisher = PublisherOf(command),
                            LooksLikeSystemComponent = StartupApproved.LooksLikeSystemComponent((string)task.Name, command),
                        });
                    }
                    catch (COMException ex)
                    {
                        log.Warn("Не удалось прочитать задачу планировщика", ex);
                    }
                    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
                    {
                        log.Warn("Неожиданный формат задачи планировщика", ex);
                    }
                }

                foreach (var sub in folder.GetFolders(0))
                {
                    pending.Push(sub);
                }
            }
        }
        catch (COMException ex)
        {
            log.Warn("Планировщик заданий недоступен", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Warn("Нет доступа к планировщику заданий", ex);
        }
        finally
        {
            if (service is not null && Marshal.IsComObject(service))
            {
                Marshal.FinalReleaseComObject(service);
            }
        }
    }

    /// <summary>Издатель EXE из команды запуска (CompanyName из версии файла).</summary>
    private static string? PublisherOf(string command)
    {
        var exe = ExtractExe(command);
        if (exe is null || !File.Exists(exe))
        {
            return null;
        }

        try
        {
            var company = FileVersionInfo.GetVersionInfo(exe).CompanyName;
            return string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    internal static string? ExtractExe(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.Length == 0)
        {
            return null;
        }

        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }

        var exeIndex = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex > 0 ? c[..(exeIndex + 4)] : c.Split(' ')[0];
    }
}
