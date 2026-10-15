using System.Globalization;
using System.Text;
using NovaTune.Core.Common;

namespace NovaTune.Core.Backup;

/// <summary>
/// Формирует .reg-файл с исходными значениями — запасной способ ручного восстановления
/// (двойной клик по файлу в Проводнике), если приложение недоступно.
/// </summary>
public static class RegFileWriter
{
    public static string Build(IEnumerable<RegistryBackupItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("Windows Registry Editor Version 5.00\r\n");
        foreach (var group in items.GroupBy(i => (i.Hive, Key: RegPath.Normalize(i.Key))))
        {
            var hive = group.Key.Hive == RegHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
            sb.Append("\r\n[").Append(hive).Append('\\').Append(group.Key.Key).Append("]\r\n");
            foreach (var item in group)
            {
                sb.Append(FormatName(item.Name)).Append('=');
                sb.Append(item.Original is null ? "-" : FormatValue(item.Original));
                sb.Append("\r\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>Сохраняет в UTF-16 LE с BOM — кодировка, которую ожидает regedit.</summary>
    public static void Save(string path, IEnumerable<RegistryBackupItem> items) =>
        File.WriteAllText(path, Build(items), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

    private static string FormatName(string name) => name.Length == 0 ? "@" : Quote(name);

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string FormatValue(RegValue v) => v.Kind switch
    {
        RegKind.DWord => "dword:" + (v.AsDWord ?? 0).ToString("x8", CultureInfo.InvariantCulture),
        RegKind.String => Quote(v.Text ?? ""),
        RegKind.Binary => "hex:" + Hex(v.Bytes ?? []),
        RegKind.QWord => "hex(b):" + Hex(BitConverter.GetBytes(v.Number ?? 0)),
        RegKind.ExpandString => "hex(2):" + Hex(Encoding.Unicode.GetBytes((v.Text ?? "") + "\0")),
        RegKind.MultiString => "hex(7):" + Hex(Encoding.Unicode.GetBytes(string.Concat((v.Lines ?? []).Select(l => l + "\0")) + "\0")),
        _ => "-",
    };

    private static string Hex(byte[] bytes) =>
        string.Join(",", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
}
