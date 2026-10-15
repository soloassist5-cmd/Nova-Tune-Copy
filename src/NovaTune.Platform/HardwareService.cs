using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Logging;
using NovaTune.Platform.Native;

namespace NovaTune.Platform;

/// <summary>
/// Сбор характеристик ПК из WMI/CIM, реестра и Win32 API. Статичные данные кэшируются;
/// повторный сбор — по кнопке «Обновить».
/// </summary>
public sealed class HardwareService(StorageService storage, ILog log)
{
    private const string Cimv2 = @"\\.\root\cimv2";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HardwareReport? _cached;

    public HardwareReport? Cached => _cached;

    public async Task<HardwareReport> GetAsync(bool refresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null && !refresh)
            {
                return _cached;
            }

            _cached = await CollectAsync(ct).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HardwareReport> CollectAsync(CancellationToken ct)
    {
        string NoWmi() => Loc.T("WMI не вернул данные", "WMI returned no data");

        var cpuTask = Wmi.QueryAsync(Cimv2, "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor", log, ct);
        var gpuTask = Wmi.QueryAsync(Cimv2, "SELECT Name, DriverVersion, DriverDate, AdapterRAM, PNPDeviceID, VideoProcessor FROM Win32_VideoController", log, ct);
        var ramTask = Wmi.QueryAsync(Cimv2, "SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, SMBIOSMemoryType FROM Win32_PhysicalMemory", log, ct);
        var boardTask = Wmi.QueryAsync(Cimv2, "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", log, ct);
        var biosTask = Wmi.QueryAsync(Cimv2, "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", log, ct);
        var enclosureTask = Wmi.QueryAsync(Cimv2, "SELECT ChassisTypes FROM Win32_SystemEnclosure", log, ct);
        var batteryTask = Wmi.QueryAsync(Cimv2, "SELECT Name, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery", log, ct);
        var storageTask = storage.GetAsync(ct);
        await Task.WhenAll(cpuTask, gpuTask, ramTask, boardTask, biosTask, enclosureTask, batteryTask, storageTask).ConfigureAwait(false);

        var sections = new List<InfoSection>();
        var os = OsInfo.Current;

        // ── Процессор
        var cpus = cpuTask.Result;
        var cpu = cpus.FirstOrDefault();
        int? cores = cpus.Count == 0 ? null : cpus.Sum(c => c.I32("NumberOfCores") ?? 0);
        int? threads = cpus.Count == 0 ? null : cpus.Sum(c => c.I32("NumberOfLogicalProcessors") ?? 0);
        var cpuMhz = cpu?.I32("MaxClockSpeed");
        sections.Add(new InfoSection("cpu", Loc.T("Процессор", "Processor"),
        [
            InfoItem.Of(Loc.T("Модель", "Model"), cpu?.Str("Name"), NoWmi()),
            InfoItem.Of(Loc.T("Производитель", "Manufacturer"), cpu?.Str("Manufacturer"), NoWmi()),
            InfoItem.Of(Loc.T("Ядра / потоки", "Cores / threads"), cores is null ? null : $"{cores} / {threads}", NoWmi()),
            InfoItem.Of(Loc.T("Базовая частота", "Base clock"), cpuMhz is null ? null : $"{cpuMhz} MHz", NoWmi()),
            InfoItem.Of(Loc.T("Кэш L2 / L3", "L2 / L3 cache"), cpu is null ? null : $"{Units.Bytes((cpu.U64("L2CacheSize") ?? 0) * 1024.0)} / {Units.Bytes((cpu.U64("L3CacheSize") ?? 0) * 1024.0)}", NoWmi()),
            InfoItem.Of(Loc.T("Сокетов", "Sockets"), cpus.Count > 0 ? cpus.Count.ToString(CultureInfo.InvariantCulture) : null, NoWmi()),
        ]));

        // ── Видеокарты
        var vram = ReadVramFromRegistry();
        var gpus = new List<GpuSummary>();
        var gpuSections = new List<InfoSection>();
        foreach (var g in gpuTask.Result)
        {
            var name = g.Str("Name") ?? "GPU";
            var pnp = g.Str("PNPDeviceID") ?? "";
            ulong? mem = vram.FirstOrDefault(v => MatchesDevice(v, name, pnp)).Bytes;
            string? memText = mem is { } m ? Units.Bytes(m) : null;
            string memReason = Loc.T("драйвер не сообщил объём", "driver did not report the size");
            if (mem is null && g.U64("AdapterRAM") is { } adapterRam and > 0)
            {
                // AdapterRAM 32-битный: значение около 4 ГБ означает «4 ГБ или больше».
                if (adapterRam >= 0xFFF00000UL)
                {
                    memText = Loc.T("не менее 4 ГБ (точный объём WMI не сообщает)", "at least 4 GB (WMI cannot report the exact size)");
                }
                else
                {
                    mem = adapterRam;
                    memText = Units.Bytes(adapterRam);
                }
            }

            var integrated = IsIntegrated(name, mem);
            var driverDate = g.Date("DriverDate");
            gpus.Add(new GpuSummary(name, mem, integrated, g.Str("DriverVersion"), driverDate));
            gpuSections.Add(new InfoSection("gpu-" + gpus.Count, name,
            [
                InfoItem.Of(Loc.T("Тип", "Type"), integrated ? Loc.T("встроенная (определено по модели)", "integrated (by model name)") : Loc.T("дискретная", "discrete"), ""),
                InfoItem.Of(Loc.T("Видеопамять", "Video memory"), memText, memReason),
                InfoItem.Of(Loc.T("Версия драйвера", "Driver version"), g.Str("DriverVersion"), NoWmi()),
                InfoItem.Of(Loc.T("Дата драйвера", "Driver date"), driverDate?.ToString("d", CultureInfo.CurrentCulture), NoWmi()),
            ]));
        }

        sections.Add(new InfoSection("gpu", Loc.T("Видеокарты", "Graphics"),
            gpuSections.Count == 0 ? [new InfoItem(Loc.T("Видеокарты", "Graphics"), null, NoWmi())] : [], gpuSections));

        // ── Память
        var status = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        ulong? totalRam = NativeMethods.GlobalMemoryStatusEx(ref status) ? status.TotalPhys : null;
        var modules = ramTask.Result;
        var ramSpeed = modules.Select(m => m.I32("ConfiguredClockSpeed") ?? m.I32("Speed")).Where(s => s > 0).DefaultIfEmpty(null).Max();
        var moduleSections = modules.Select((m, i) => new InfoSection("ram-" + i, m.Str("DeviceLocator") ?? $"DIMM {i + 1}",
        [
            InfoItem.Of(Loc.T("Объём", "Capacity"), m.U64("Capacity") is { } c ? Units.Bytes(c) : null, NoWmi()),
            InfoItem.Of(Loc.T("Тип", "Type"), MemoryTypeName(m.I32("SMBIOSMemoryType")), Loc.T("не указан в SMBIOS", "not reported by SMBIOS")),
            InfoItem.Of(Loc.T("Частота (настроенная / номинальная)", "Speed (configured / rated)"), FormatSpeeds(m.I32("ConfiguredClockSpeed"), m.I32("Speed")), NoWmi()),
            InfoItem.Of(Loc.T("Производитель", "Manufacturer"), m.Str("Manufacturer"), NoWmi()),
            InfoItem.Of(Loc.T("Модель", "Part number"), m.Str("PartNumber"), NoWmi()),
        ])).ToList();
        sections.Add(new InfoSection("ram", Loc.T("Оперативная память", "Memory"),
        [
            InfoItem.Of(Loc.T("Всего доступно Windows", "Usable by Windows"), totalRam is { } t ? Units.Bytes(t) : null, "GlobalMemoryStatusEx"),
            InfoItem.Of(Loc.T("Модулей", "Modules"), modules.Count > 0 ? modules.Count.ToString(CultureInfo.InvariantCulture) : null, NoWmi()),
            InfoItem.Of(Loc.T("Частота", "Speed"), ramSpeed is { } rs ? $"{rs} MT/s" : null, NoWmi()),
        ], moduleSections));

        // ── Плата и BIOS
        var board = boardTask.Result.FirstOrDefault();
        var bios = biosTask.Result.FirstOrDefault();
        sections.Add(new InfoSection("board", Loc.T("Материнская плата и BIOS", "Motherboard & BIOS"),
        [
            InfoItem.Of(Loc.T("Производитель", "Manufacturer"), board?.Str("Manufacturer"), NoWmi()),
            InfoItem.Of(Loc.T("Модель", "Model"), board?.Str("Product"), NoWmi()),
            InfoItem.Of(Loc.T("Ревизия", "Revision"), board?.Str("Version"), NoWmi()),
            InfoItem.Of("BIOS", bios is null ? null : $"{bios.Str("Manufacturer")} {bios.Str("SMBIOSBIOSVersion")}".Trim(), NoWmi()),
            InfoItem.Of(Loc.T("Дата BIOS", "BIOS date"), bios?.Date("ReleaseDate")?.ToString("d", CultureInfo.CurrentCulture), NoWmi()),
        ]));

        // ── Накопители
        var (disks, volumes) = storageTask.Result;
        var diskSections = disks.Select((d, i) => new InfoSection("disk-" + i, d.Name,
        [
            InfoItem.Of(Loc.T("Тип", "Type"), d.MediaType, ""),
            InfoItem.Of(Loc.T("Интерфейс", "Bus"), d.BusType, ""),
            InfoItem.Of(Loc.T("Объём", "Size"), d.SizeBytes is { } s ? Units.Bytes(s) : null, NoWmi()),
            InfoItem.Of(Loc.T("Состояние", "Health"), d.Health, ""),
            InfoItem.Of(Loc.T("Тома", "Volumes"), d.DriveLetters.Count > 0 ? string.Join(", ", d.DriveLetters.Select(c => c + ":")) : null, Loc.T("нет томов с буквой", "no lettered volumes")),
            new(Loc.T("Температура, износ (SMART)", "Temperature, wear (SMART)"), null, Loc.T("нужны права администратора — кнопка в разделе Monitoring", "requires administrator rights — button in Monitoring")),
        ])).ToList();
        var volumeItems = volumes.Select(v => new InfoItem(
            $"{v.Letter}: {v.Label}".Trim(),
            Loc.T($"свободно {Units.Bytes(v.FreeBytes)} из {Units.Bytes(v.TotalBytes)} ({100 - v.UsedPercent:0}%), {v.FileSystem}", $"{Units.Bytes(v.FreeBytes)} free of {Units.Bytes(v.TotalBytes)} ({100 - v.UsedPercent:0}%), {v.FileSystem}"))).ToList();
        sections.Add(new InfoSection("storage", Loc.T("Накопители", "Storage"), volumeItems, diskSections));

        // ── Система
        var uptime = TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64());
        sections.Add(new InfoSection("system", Loc.T("Система", "System"),
        [
            InfoItem.Of(Loc.T("Имя устройства", "Device name"), Environment.MachineName, ""),
            InfoItem.Of(Loc.T("Windows", "Windows"), $"{os.Name} {os.DisplayVersion}".Trim(), ""),
            InfoItem.Of(Loc.T("Сборка", "Build"), os.FullBuild, ""),
            InfoItem.Of(Loc.T("Разрядность", "Architecture"), os.Architecture, ""),
            InfoItem.Of(Loc.T("Установлена", "Installed"), os.InstallDate?.ToString("d", CultureInfo.CurrentCulture), Loc.T("нет в реестре", "not in registry")),
            InfoItem.Of(Loc.T("Время работы", "Uptime"), Units.Duration(uptime), ""),
            InfoItem.Of(Loc.T("Поддержка", "Support"), os.SupportNote(), ""),
        ]));

        // ── Мониторы
        var monitors = ReadMonitors();
        sections.Add(new InfoSection("monitors", Loc.T("Мониторы", "Displays"),
            monitors.Count == 0
                ? [new InfoItem(Loc.T("Мониторы", "Displays"), null, Loc.T("EnumDisplaySettings не вернул активных дисплеев", "EnumDisplaySettings returned no active displays"))]
                : monitors.Select(m => new InfoItem(
                    m.Name + (m.IsPrimary ? Loc.T(" (основной)", " (primary)") : ""),
                    $"{m.Width}×{m.Height} @ {m.RefreshHz} Hz"
                    + (m.MaxRefreshHz is { } max ? Loc.T($" (максимум при этом разрешении — {max} Hz)", $" (maximum at this resolution: {max} Hz)") : "")
                    + (m.BelowMaxRefresh ? Loc.T(" — работает ниже максимума!", " — running below maximum!") : ""))).ToList()));

        // ── Сеть
        sections.Add(new InfoSection("network", Loc.T("Сеть", "Network"), ReadNetwork()));

        // ── Батарея
        NativeMethods.GetSystemPowerStatus(out var power);
        var hasBattery = power.BatteryFlag != 128 && power.BatteryFlag != 255;
        var battery = batteryTask.Result.FirstOrDefault();
        var chassis = enclosureTask.Result.FirstOrDefault();
        var portable = chassis is not null && chassis.TryGetValue("ChassisTypes", out var ctObj) && ctObj is ushort[] types
            && types.Any(x => x is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32);
        var isLaptop = portable || (hasBattery && battery is not null);
        var onBattery = hasBattery && power.ACLineStatus == 0;
        sections.Add(new InfoSection("battery", Loc.T("Батарея", "Battery"),
            battery is null && !hasBattery
                ? [new InfoItem(Loc.T("Батарея", "Battery"), Loc.T("Нет (настольный ПК)", "None (desktop)"))]
                :
                [
                    InfoItem.Of(Loc.T("Заряд", "Charge"), power.BatteryLifePercent <= 100 ? $"{power.BatteryLifePercent}%" : null, Loc.T("неизвестно", "unknown")),
                    InfoItem.Of(Loc.T("Питание", "Power source"), power.ACLineStatus switch { 1 => Loc.T("от сети", "AC"), 0 => Loc.T("от батареи", "battery"), _ => null }, Loc.T("неизвестно", "unknown")),
                    InfoItem.Of(Loc.T("Модель", "Model"), battery?.Str("Name"), NoWmi()),
                ]));

        var summary = new HardwareSummary
        {
            DeviceName = Environment.MachineName,
            CpuName = cpu?.Str("Name"),
            CpuCores = cores,
            CpuThreads = threads,
            CpuBaseMhz = cpuMhz,
            RamTotalBytes = totalRam,
            RamSpeedMhz = ramSpeed,
            Gpus = gpus,
            Disks = disks,
            Volumes = volumes,
            Monitors = monitors,
            IsLaptop = isLaptop,
            OnBattery = onBattery,
            OsBuild = os.Build,
            OsName = $"{os.Name} {os.DisplayVersion}".Trim(),
            SystemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0],
        };

        return new HardwareReport(summary, sections, DateTimeOffset.Now);
    }

    private static string? FormatSpeeds(int? configured, int? rated) =>
        configured is null && rated is null ? null : $"{configured?.ToString(CultureInfo.InvariantCulture) ?? "?"} / {rated?.ToString(CultureInfo.InvariantCulture) ?? "?"} MT/s";

    private static string? MemoryTypeName(int? smbios) => smbios switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    /// <summary>
    /// Эвристика по названию: Intel без Arc, AMD «Radeon(TM) Graphics»/Vega в APU, базовый адаптер Microsoft.
    /// Отмечаем в интерфейсе, что тип определён по названию модели.
    /// </summary>
    private static bool IsIntegrated(string name, ulong? vram)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("intel") && !n.Contains("arc"))
        {
            return true;
        }

        if (n.Contains("microsoft basic"))
        {
            return true;
        }

        if (n.Contains("amd") && (n.EndsWith("radeon(tm) graphics", StringComparison.Ordinal) || n.Contains("vega 8") || n.Contains("vega 11") || n.Contains("radeon graphics")))
        {
            return true;
        }

        return vram is < 512UL * 1024 * 1024;
    }

    private readonly record struct RegVram(string? Desc, string? MatchingId, ulong? Bytes);

    private List<RegVram> ReadVramFromRegistry()
    {
        var list = new List<RegVram>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(DisplayClassKey);
            if (root is null)
            {
                return list;
            }

            foreach (var sub in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)))
            {
                try
                {
                    using var k = root.OpenSubKey(sub);
                    if (k is null)
                    {
                        continue;
                    }

                    ulong? bytes = k.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l when l > 0 => (ulong)l,
                        byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                        _ => k.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int i when i != 0 => (uint)i,
                            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                            _ => null,
                        },
                    };
                    list.Add(new RegVram(k.GetValue("DriverDesc") as string, k.GetValue("MatchingDeviceId") as string, bytes));
                }
                catch (System.Security.SecurityException)
                {
                    // Отдельные подключи класса закрыты для чтения пользователю — пропускаем.
                    continue;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Warn("Не удалось прочитать объём видеопамяти из реестра", ex);
        }

        return list;
    }

    private static bool MatchesDevice(RegVram v, string name, string pnp) =>
        v.Bytes is not null && (
            (v.MatchingId is { Length: > 0 } id && pnp.StartsWith(id, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(v.Desc, name, StringComparison.OrdinalIgnoreCase));

    public static List<MonitorSummary> ReadMonitors()
    {
        var result = new List<MonitorSummary>();
        var adapter = new NativeMethods.DisplayDevice { Cb = Marshal.SizeOf<NativeMethods.DisplayDevice>() };
        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            if ((adapter.StateFlags & NativeMethods.DisplayDeviceAttachedToDesktop) != 0 && (adapter.StateFlags & NativeMethods.DisplayDeviceMirroringDriver) == 0)
            {
                var mode = new NativeMethods.DevMode { Size = (short)Marshal.SizeOf<NativeMethods.DevMode>(), DeviceName = "", FormName = "" };
                if (NativeMethods.EnumDisplaySettings(adapter.DeviceName, NativeMethods.EnumCurrentSettings, ref mode))
                {
                    var monitor = new NativeMethods.DisplayDevice { Cb = Marshal.SizeOf<NativeMethods.DisplayDevice>() };
                    var monitorName = NativeMethods.EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.DeviceString)
                        ? monitor.DeviceString
                        : adapter.DeviceName;
                    result.Add(new MonitorSummary(
                        $"{monitorName} — {adapter.DeviceString}",
                        mode.PelsWidth,
                        mode.PelsHeight,
                        mode.DisplayFrequency,
                        (adapter.StateFlags & NativeMethods.DisplayDevicePrimaryDevice) != 0,
                        MaxRefreshAt(adapter.DeviceName, mode.PelsWidth, mode.PelsHeight)));
                }
            }

            adapter = new NativeMethods.DisplayDevice { Cb = Marshal.SizeOf<NativeMethods.DisplayDevice>() };
        }

        return result;
    }

    /// <summary>Максимальная частота среди режимов, которые драйвер разрешает при данном разрешении.</summary>
    private static int? MaxRefreshAt(string device, int width, int height)
    {
        var max = 0;
        var mode = new NativeMethods.DevMode { Size = (short)Marshal.SizeOf<NativeMethods.DevMode>(), DeviceName = "", FormName = "" };
        for (var i = 0; i < 4096 && NativeMethods.EnumDisplaySettings(device, i, ref mode); i++)
        {
            if (mode.PelsWidth == width && mode.PelsHeight == height && mode.DisplayFrequency > max)
            {
                max = mode.DisplayFrequency;
            }
        }

        return max > 1 ? max : null;
    }

    private List<InfoItem> ReadNetwork()
    {
        var items = new List<InfoItem>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var ipv4 = nic.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .FirstOrDefault();
                var speed = nic.Speed > 0 ? (nic.Speed >= 1_000_000_000 ? $"{nic.Speed / 1_000_000_000.0:0.#} Gbit/s" : $"{nic.Speed / 1_000_000} Mbit/s") : null;
                var type = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                    _ => nic.NetworkInterfaceType.ToString(),
                };
                items.Add(new InfoItem(nic.Name, string.Join(", ", new[] { nic.Description, type, speed, ipv4 }.Where(s => !string.IsNullOrEmpty(s)))));
            }
        }
        catch (NetworkInformationException ex)
        {
            log.Warn("Не удалось получить сетевые адаптеры", ex);
        }

        return items.Count > 0 ? items : [new InfoItem(Loc.T("Сеть", "Network"), null, Loc.T("нет активных адаптеров", "no active adapters"))];
    }
}
