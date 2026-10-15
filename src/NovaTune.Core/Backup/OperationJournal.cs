using System.Text;
using System.Text.Json;
using NovaTune.Core.Common;
using NovaTune.Core.Logging;

namespace NovaTune.Core.Backup;

public sealed record JournalEntry(
    DateTimeOffset Time,
    string Category,
    string Title,
    bool Success,
    string? Details);

/// <summary>Журнал выполненных операций (JSON Lines): что сделано, успешно ли, подробности.</summary>
public sealed class OperationJournal
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly ILog _log;

    public OperationJournal(string path, ILog log)
    {
        _path = path;
        _log = log;
    }

    public event EventHandler<JournalEntry>? EntryAdded;

    public void Add(string category, string title, bool success, string? details = null)
    {
        var entry = new JournalEntry(DateTimeOffset.Now, category, title, success, details);
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json.Compact) + "\n", Encoding.UTF8);
            }
            catch (IOException ex)
            {
                _log.Error("Не удалось записать журнал операций", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Error("Нет доступа к журналу операций", ex);
            }
        }

        _log.Info($"[{category}] {(success ? "OK" : "FAIL")} {title}{(details is null ? "" : " — " + details)}");
        EntryAdded?.Invoke(this, entry);
    }

    /// <summary>Последние записи, новые сверху. Повреждённые строки пропускаются (с записью в лог).</summary>
    public IReadOnlyList<JournalEntry> ReadRecent(int max = 500)
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var result = new List<JournalEntry>();
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<JournalEntry>(line, Json.Compact) is { } e)
                    {
                        result.Add(e);
                    }
                }
                catch (JsonException ex)
                {
                    _log.Warn("Пропущена повреждённая строка журнала", ex);
                }
            }

            result.Reverse();
            return result.Take(max).ToList();
        }
    }
}
