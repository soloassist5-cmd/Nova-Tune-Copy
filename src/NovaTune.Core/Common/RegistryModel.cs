using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace NovaTune.Core.Common;

public enum RegHive
{
    CurrentUser,
    LocalMachine,
}

/// <summary>Тип значения реестра (собственное перечисление, чтобы ядро не зависело от Microsoft.Win32).</summary>
public enum RegKind
{
    String,
    ExpandString,
    Binary,
    DWord,
    MultiString,
    QWord,
}

/// <summary>
/// Значение реестра в сериализуемом виде. Для DWORD/QWORD используется <see cref="Number"/>,
/// для строк — <see cref="Text"/>, для REG_MULTI_SZ — <see cref="Lines"/>, для двоичных данных — <see cref="Bytes"/>.
/// </summary>
public sealed record RegValue
{
    public RegKind Kind { get; init; }

    public long? Number { get; init; }

    public string? Text { get; init; }

    public string[]? Lines { get; init; }

    public byte[]? Bytes { get; init; }

    public static RegValue DWord(uint value) => new() { Kind = RegKind.DWord, Number = value };

    public static RegValue QWord(ulong value) => new() { Kind = RegKind.QWord, Number = unchecked((long)value) };

    public static RegValue String(string value) => new() { Kind = RegKind.String, Text = value };

    public static RegValue ExpandString(string value) => new() { Kind = RegKind.ExpandString, Text = value };

    public static RegValue MultiString(IEnumerable<string> lines) => new() { Kind = RegKind.MultiString, Lines = lines.ToArray() };

    public static RegValue Binary(byte[] bytes) => new() { Kind = RegKind.Binary, Bytes = bytes.ToArray() };

    [JsonIgnore]
    public uint? AsDWord => Kind == RegKind.DWord && Number is { } n ? unchecked((uint)n) : null;

    /// <summary>Сравнение по содержимому (у record массивы сравниваются по ссылке, поэтому своя реализация).</summary>
    public bool ContentEquals(RegValue? other)
    {
        if (other is null || other.Kind != Kind)
        {
            return false;
        }

        return Kind switch
        {
            RegKind.DWord or RegKind.QWord => Number == other.Number,
            RegKind.String or RegKind.ExpandString => string.Equals(Text, other.Text, StringComparison.Ordinal),
            RegKind.MultiString => (Lines ?? []).SequenceEqual(other.Lines ?? [], StringComparer.Ordinal),
            RegKind.Binary => (Bytes ?? []).AsSpan().SequenceEqual(other.Bytes ?? []),
            _ => false,
        };
    }

    public string KindName => Kind switch
    {
        RegKind.String => "REG_SZ",
        RegKind.ExpandString => "REG_EXPAND_SZ",
        RegKind.Binary => "REG_BINARY",
        RegKind.DWord => "REG_DWORD",
        RegKind.MultiString => "REG_MULTI_SZ",
        RegKind.QWord => "REG_QWORD",
        _ => Kind.ToString(),
    };

    /// <summary>Короткое человекочитаемое представление для предпросмотра и журнала.</summary>
    public string Display()
    {
        switch (Kind)
        {
            case RegKind.DWord:
                return string.Create(CultureInfo.InvariantCulture, $"{AsDWord} (0x{AsDWord:X8})");
            case RegKind.QWord:
                return Number?.ToString(CultureInfo.InvariantCulture) ?? "";
            case RegKind.String:
            case RegKind.ExpandString:
                return "\"" + Text + "\"";
            case RegKind.MultiString:
                return string.Join(" | ", Lines ?? []);
            case RegKind.Binary:
                var bytes = Bytes ?? [];
                var sb = new StringBuilder();
                foreach (var b in bytes.Take(16))
                {
                    sb.Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                }

                if (bytes.Length > 16)
                {
                    sb.Append('…');
                }

                return sb.ToString().TrimEnd();
            default:
                return "";
        }
    }
}

public static class RegPath
{
    public static string HiveName(RegHive hive) => hive == RegHive.CurrentUser ? "HKCU" : "HKLM";

    public static string Format(RegHive hive, string key, string? name = null) =>
        name is null ? $@"{HiveName(hive)}\{key}" : $@"{HiveName(hive)}\{key}  →  {(name.Length == 0 ? "(по умолчанию)" : name)}";

    /// <summary>Нормализует путь ключа: обратные слэши, без хвостовых и двойных разделителей.</summary>
    public static string Normalize(string key) =>
        string.Join('\\', key.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries));
}
