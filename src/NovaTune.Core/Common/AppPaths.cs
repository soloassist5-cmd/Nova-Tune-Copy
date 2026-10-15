using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace NovaTune.Core.Common;

/// <summary>Расположение данных приложения: %LOCALAPPDATA%\NovaTune\...</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string Logs => Path.Combine(Root, "logs");

    public string DefaultBackups => Path.Combine(Root, "backups");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string JournalFile => Path.Combine(Root, "journal.jsonl");

    public string Temp => Path.Combine(Root, "temp");

    public static AppPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaTune"));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Temp);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        // Кириллица в JSON остаётся читаемой, без \uXXXX.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    /// <summary>Атомарная запись: сначала во временный файл, затем замена. Сбой посередине не оставит битый JSON.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public static T? Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static void WriteUtf8(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
}
