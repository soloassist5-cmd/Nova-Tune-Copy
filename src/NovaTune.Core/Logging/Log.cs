using System.Globalization;
using System.Text;

namespace NovaTune.Core.Logging;

public interface ILog
{
    void Info(string message);

    void Warn(string message, Exception? ex = null);

    void Error(string message, Exception? ex = null);
}

/// <summary>Пустой логгер для тестов.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? ex = null)
    {
    }

    public void Error(string message, Exception? ex = null)
    {
    }
}

/// <summary>
/// Файловый лог с ротацией: novatune.log → novatune.1.log → … → novatune.{N}.log.
/// Пишет синхронно под блокировкой: объём логов небольшой, зато запись не теряется при падении.
/// </summary>
public sealed class FileLog : ILog
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _maxFiles;

    public FileLog(string directory, long maxBytes = 1024 * 1024, int maxFiles = 5)
    {
        _directory = directory;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        Directory.CreateDirectory(directory);
    }

    public string CurrentFile => Path.Combine(_directory, "novatune.log");

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(message);
        if (ex is not null)
        {
            line.AppendLine().Append(ex);
        }

        line.AppendLine();

        lock (_sync)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(CurrentFile, line.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // Логгер не должен ронять приложение; писать о сбое логгера некуда, кроме отладочного вывода.
                System.Diagnostics.Debug.WriteLine(line.ToString());
            }
            catch (UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(line.ToString());
            }
        }
    }

    private void RotateIfNeeded()
    {
        var current = new FileInfo(CurrentFile);
        if (!current.Exists || current.Length < _maxBytes)
        {
            return;
        }

        var oldest = Path.Combine(_directory, $"novatune.{_maxFiles}.log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = _maxFiles - 1; i >= 1; i--)
        {
            var src = Path.Combine(_directory, $"novatune.{i}.log");
            if (File.Exists(src))
            {
                File.Move(src, Path.Combine(_directory, $"novatune.{i + 1}.log"));
            }
        }

        File.Move(CurrentFile, Path.Combine(_directory, "novatune.1.log"));
    }
}
