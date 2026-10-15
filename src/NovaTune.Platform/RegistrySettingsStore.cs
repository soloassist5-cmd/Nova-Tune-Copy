using System.ComponentModel;
using System.Text;
using Microsoft.Win32;
using NovaTune.Core.Common;
using NovaTune.Core.Elevation;
using NovaTune.Core.Localization;
using NovaTune.Platform.Native;

namespace NovaTune.Platform;

/// <summary>
/// Настоящие системные настройки. Чтение — напрямую. Запись в HKCU — напрямую (права пользователя).
/// Запись в HKLM без прав администратора выполняется через <see cref="ElevationService"/>
/// (отдельный процесс с UAC и белым списком операций).
/// </summary>
public sealed class RegistrySettingsStore(ElevationService elevation) : ISystemSettingsStore
{
    private static RegistryKey Base(RegHive hive) => RegistryKey.OpenBaseKey(
        hive == RegHive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
        RegistryView.Registry64);

    public bool KeyExists(RegHive hive, string key)
    {
        using var root = Base(hive);
        using var k = root.OpenSubKey(RegPath.Normalize(key));
        return k is not null;
    }

    public RegValue? ReadValue(RegHive hive, string key, string name)
    {
        using var root = Base(hive);
        using var k = root.OpenSubKey(RegPath.Normalize(key));
        if (k is null || !k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var kind = k.GetValueKind(name);
        var raw = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return Convert(kind, raw);
    }

    public IReadOnlyList<string> GetValueNames(RegHive hive, string key)
    {
        using var root = Base(hive);
        using var k = root.OpenSubKey(RegPath.Normalize(key));
        return k?.GetValueNames() ?? [];
    }

    public void WriteValue(RegHive hive, string key, string name, RegValue value)
    {
        if (NeedsElevation(hive))
        {
            elevation.RunOrThrow(new ElevatedTaskRequest { Operation = ElevatedOperation.RegistrySet, Hive = hive, Key = key, Name = name, Value = value });
            return;
        }

        WriteDirect(hive, key, name, value);
    }

    public void DeleteValue(RegHive hive, string key, string name)
    {
        if (ReadValue(hive, key, name) is null)
        {
            return;
        }

        if (NeedsElevation(hive))
        {
            elevation.RunOrThrow(new ElevatedTaskRequest { Operation = ElevatedOperation.RegistryDelete, Hive = hive, Key = key, Name = name });
            return;
        }

        DeleteDirect(hive, key, name);
    }

    public void DeleteKeyIfEmpty(RegHive hive, string key)
    {
        // Ключи HKLM приложение не создаёт (только меняет значения в существующих), поэтому удаляем лишь в HKCU.
        if (hive != RegHive.CurrentUser)
        {
            return;
        }

        var normalized = RegPath.Normalize(key);
        using var root = Base(hive);
        using (var k = root.OpenSubKey(normalized))
        {
            if (k is null || k.ValueCount > 0 || k.SubKeyCount > 0)
            {
                return;
            }
        }

        root.DeleteSubKey(normalized, throwOnMissingSubKey: false);
    }

    private static bool NeedsElevation(RegHive hive) => hive == RegHive.LocalMachine && !OsInfo.IsElevated();

    /// <summary>Прямая запись (вызывается и в повышенном процессе после проверки белым списком).</summary>
    internal static void WriteDirect(RegHive hive, string key, string name, RegValue value)
    {
        using var root = Base(hive);
        using var k = root.CreateSubKey(RegPath.Normalize(key), writable: true)
            ?? throw new UnauthorizedAccessException(RegPath.Format(hive, key));
        switch (value.Kind)
        {
            case RegKind.DWord:
                k.SetValue(name, unchecked((int)(value.AsDWord ?? 0)), RegistryValueKind.DWord);
                break;
            case RegKind.QWord:
                k.SetValue(name, value.Number ?? 0, RegistryValueKind.QWord);
                break;
            case RegKind.String:
                k.SetValue(name, value.Text ?? "", RegistryValueKind.String);
                break;
            case RegKind.ExpandString:
                k.SetValue(name, value.Text ?? "", RegistryValueKind.ExpandString);
                break;
            case RegKind.MultiString:
                k.SetValue(name, value.Lines ?? [], RegistryValueKind.MultiString);
                break;
            case RegKind.Binary:
                k.SetValue(name, value.Bytes ?? [], RegistryValueKind.Binary);
                break;
            default:
                throw new NotSupportedException(value.Kind.ToString());
        }
    }

    internal static void DeleteDirect(RegHive hive, string key, string name)
    {
        using var root = Base(hive);
        using var k = root.OpenSubKey(RegPath.Normalize(key), writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static RegValue? Convert(RegistryValueKind kind, object? raw) => kind switch
    {
        RegistryValueKind.DWord => RegValue.DWord(unchecked((uint)(int)raw!)),
        RegistryValueKind.QWord => RegValue.QWord(unchecked((ulong)(long)raw!)),
        RegistryValueKind.String => RegValue.String(raw as string ?? ""),
        RegistryValueKind.ExpandString => RegValue.ExpandString(raw as string ?? ""),
        RegistryValueKind.MultiString => RegValue.MultiString(raw as string[] ?? []),
        RegistryValueKind.Binary => RegValue.Binary(raw as byte[] ?? []),
        _ => RegValue.Binary(raw as byte[] ?? []),
    };

    // ───────── Схемы питания ─────────

    public Guid? GetActivePowerScheme()
    {
        if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != NativeMethods.ErrorSuccess)
        {
            return null;
        }

        try
        {
            return System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            NativeMethods.LocalFree(ptr);
        }
    }

    public IReadOnlyList<PowerSchemeInfo> GetPowerSchemes()
    {
        var result = new List<PowerSchemeInfo>();
        for (uint i = 0; ; i++)
        {
            var size = 16u;
            var buffer = new byte[16];
            var rc = NativeMethods.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.AccessScheme, i, buffer, ref size);
            if (rc == NativeMethods.ErrorNoMoreItems)
            {
                break;
            }

            if (rc != NativeMethods.ErrorSuccess)
            {
                throw new Win32Exception((int)rc);
            }

            var id = new Guid(buffer);
            result.Add(new PowerSchemeInfo(id, ReadSchemeName(id)));
        }

        return result;
    }

    private static string ReadSchemeName(Guid id)
    {
        uint size = 0;
        NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0)
        {
            return id.ToString();
        }

        var buffer = new byte[size];
        if (NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != NativeMethods.ErrorSuccess)
        {
            return id.ToString();
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public void SetActivePowerScheme(Guid scheme)
    {
        var rc = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (rc != NativeMethods.ErrorSuccess)
        {
            throw new Win32Exception((int)rc, Loc.T("Не удалось включить схему питания", "Failed to activate power plan") + $" (0x{rc:X})");
        }
    }

    public Guid DuplicatePowerScheme(Guid source)
    {
        var dest = IntPtr.Zero;
        var rc = NativeMethods.PowerDuplicateScheme(IntPtr.Zero, ref source, ref dest);
        if (rc != NativeMethods.ErrorSuccess || dest == IntPtr.Zero)
        {
            throw new Win32Exception((int)rc, Loc.T(
                "Не удалось создать схему питания. В некоторых редакциях Windows 10 шаблон «Максимальная производительность» недоступен.",
                "Failed to create the power plan. The \"Ultimate Performance\" template is unavailable in some Windows 10 editions.") + $" (0x{rc:X})");
        }

        try
        {
            return System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(dest);
        }
        finally
        {
            NativeMethods.LocalFree(dest);
        }
    }

    public void DeletePowerScheme(Guid scheme)
    {
        var rc = NativeMethods.PowerDeleteScheme(IntPtr.Zero, ref scheme);
        if (rc != NativeMethods.ErrorSuccess)
        {
            throw new Win32Exception((int)rc, Loc.T("Не удалось удалить схему питания", "Failed to delete power plan") + $" (0x{rc:X})");
        }
    }
}
