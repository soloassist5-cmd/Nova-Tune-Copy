using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using NovaTune.Core.Localization;
using NovaTune.Core.Optimization;
using NovaTune.Platform.Native;

namespace NovaTune.Platform;

/// <summary>
/// Версия Windows. Номер сборки берётся из RtlGetVersion (не зависит от манифеста), UBR и DisplayVersion — из реестра.
/// В Windows 11 параметр ProductName по-прежнему содержит «Windows 10», поэтому имя исправляется по номеру сборки.
/// </summary>
public sealed record OsInfo(string Name, string Edition, string? DisplayVersion, int Build, int Ubr, string Architecture, DateTime? InstallDate)
{
    public const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public bool IsWindows11 => Build >= 22000;

    public string FullBuild => $"{Build}.{Ubr}";

    private static OsInfo? _cached;

    public static OsInfo Current => _cached ??= Read();

    private static OsInfo Read()
    {
        var build = 0;
        try
        {
            var info = new NativeMethods.OsVersionInfoEx { OSVersionInfoSize = Marshal.SizeOf<NativeMethods.OsVersionInfoEx>(), CSDVersion = "" };
            if (NativeMethods.RtlGetVersion(ref info) == 0)
            {
                build = info.BuildNumber;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Сборку возьмём из реестра ниже.
        }

        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(CurrentVersionKey);
        if (build == 0 && int.TryParse(key?.GetValue("CurrentBuildNumber") as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            build = b;
        }

        var ubr = key?.GetValue("UBR") is int u ? u : 0;
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        if (build >= 22000)
        {
            product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        var display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string;
        var edition = key?.GetValue("EditionID") as string ?? "";
        DateTime? installDate = key?.GetValue("InstallDate") is int seconds and > 0
            ? DateTimeOffset.FromUnixTimeSeconds((uint)seconds).LocalDateTime
            : null;

        return new OsInfo(product, edition, display, build, ubr, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), installDate);
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Текст о статусе поддержки — без запугивания, только факты.</summary>
    public string SupportNote()
    {
        if (IsWindows11)
        {
            return Loc.T("Windows 11 — поддерживается Microsoft.", "Windows 11 — supported by Microsoft.");
        }

        return Loc.T(
            "Основная поддержка Windows 10 завершилась 14 октября 2025 г. Обновления безопасности доступны по программе расширенной поддержки (ESU). Это информация, а не требование что-либо менять.",
            "Mainstream support for Windows 10 ended on 14 October 2025. Security updates are available through the Extended Security Updates (ESU) programme. This is information, not a requirement to change anything.");
    }
}

/// <summary>Чтение состояния HAGS из драйвера через D3DKMT (WDDM 2.7 caps).</summary>
public static class HagsProbe
{
    public static (TriState Supported, TriState Enabled) Query(Core.Logging.ILog log)
    {
        if (OsInfo.Current.Build < KnownKeys.HagsMinBuild)
        {
            return (TriState.No, TriState.No);
        }

        try
        {
            var en = new NativeMethods.D3dkmtEnumAdapters2();
            if (NativeMethods.D3DKMTEnumAdapters2(ref en) != 0 || en.NumAdapters == 0)
            {
                return (TriState.Unknown, TriState.Unknown);
            }

            var size = Marshal.SizeOf<NativeMethods.D3dkmtAdapterInfo>();
            var buffer = Marshal.AllocHGlobal(size * (int)en.NumAdapters);
            var caps = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                en.Adapters = buffer;
                if (NativeMethods.D3DKMTEnumAdapters2(ref en) != 0)
                {
                    return (TriState.Unknown, TriState.Unknown);
                }

                bool anyAnswered = false, supported = false, enabled = false;
                for (var i = 0; i < en.NumAdapters; i++)
                {
                    var adapter = Marshal.PtrToStructure<NativeMethods.D3dkmtAdapterInfo>(buffer + (i * size));
                    Marshal.WriteInt32(caps, 0);
                    var query = new NativeMethods.D3dkmtQueryAdapterInfo
                    {
                        Adapter = adapter.Adapter,
                        Type = NativeMethods.KmtQaiTypeWddm27Caps,
                        PrivateDriverData = caps,
                        PrivateDriverDataSize = sizeof(uint),
                    };
                    if (NativeMethods.D3DKMTQueryAdapterInfo(ref query) == 0)
                    {
                        anyAnswered = true;
                        var value = (uint)Marshal.ReadInt32(caps);
                        supported |= (value & 0x1) != 0;
                        enabled |= (value & 0x2) != 0;
                    }

                    var close = new NativeMethods.D3dkmtCloseAdapter { Adapter = adapter.Adapter };
                    NativeMethods.D3DKMTCloseAdapter(ref close);
                }

                if (!anyAnswered)
                {
                    return (TriState.Unknown, TriState.Unknown);
                }

                return (supported ? TriState.Yes : TriState.No, enabled ? TriState.Yes : TriState.No);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(caps);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            log.Warn("Не удалось определить состояние HAGS через D3DKMT", ex);
            return (TriState.Unknown, TriState.Unknown);
        }
    }
}
