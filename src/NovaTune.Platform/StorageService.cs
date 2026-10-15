using System.Globalization;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Logging;

namespace NovaTune.Platform;

public sealed record DiskReliability(string DiskName, double? TemperatureC, double? WearPercent, long? PowerOnHours, long? ReadErrors, long? WriteErrors);

/// <summary>Накопители: MSFT_PhysicalDisk (тип, шина, состояние), MSFT_Partition (буквы томов), DriveInfo (место).</summary>
public sealed class StorageService(ILog log)
{
    public const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

    public async Task<(IReadOnlyList<DiskSummary> Disks, IReadOnlyList<VolumeSummary> Volumes)> GetAsync(CancellationToken ct)
    {
        var disks = await Wmi.QueryAsync(StorageScope, "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk", log, ct).ConfigureAwait(false);
        var partitions = await Wmi.QueryAsync(StorageScope, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition", log, ct).ConfigureAwait(false);
        return Build(disks, partitions);
    }

    /// <summary>Синхронный вариант (для повышенного процесса).</summary>
    public (IReadOnlyList<DiskSummary> Disks, IReadOnlyList<VolumeSummary> Volumes) Get()
    {
        var disks = Wmi.Query(StorageScope, "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk", Wmi.DefaultTimeout);
        var partitions = Wmi.Query(StorageScope, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition", Wmi.DefaultTimeout);
        return Build(disks, partitions);
    }

    private (IReadOnlyList<DiskSummary>, IReadOnlyList<VolumeSummary>) Build(
        IReadOnlyList<Dictionary<string, object?>> disks,
        IReadOnlyList<Dictionary<string, object?>> partitions)
    {
        // Буквы томов по номеру диска. Том на нескольких дисках (Storage Spaces, RAID) получит null в IsSsd.
        var lettersByDisk = new Dictionary<string, List<char>>();
        var disksByLetter = new Dictionary<char, HashSet<string>>();
        foreach (var p in partitions)
        {
            var letter = LetterOf(p);
            var disk = p.Str("DiskNumber");
            if (letter is null || disk is null)
            {
                continue;
            }

            if (!lettersByDisk.TryGetValue(disk, out var list))
            {
                lettersByDisk[disk] = list = [];
            }

            list.Add(letter.Value);
            if (!disksByLetter.TryGetValue(letter.Value, out var set))
            {
                disksByLetter[letter.Value] = set = [];
            }

            set.Add(disk);
        }

        var diskList = new List<DiskSummary>();
        var ssdByDisk = new Dictionary<string, bool?>();
        foreach (var d in disks)
        {
            var id = d.Str("DeviceId") ?? "";
            var mediaCode = d.I32("MediaType") ?? 0;
            bool? isSsd = mediaCode switch { 4 => true, 3 => false, _ => null };
            ssdByDisk[id] = isSsd;
            diskList.Add(new DiskSummary(
                d.Str("FriendlyName") ?? Loc.T("Диск ", "Disk ") + id,
                MediaTypeName(mediaCode),
                BusTypeName(d.I32("BusType") ?? 0),
                d.U64("Size"),
                HealthName(d.I32("HealthStatus")),
                lettersByDisk.TryGetValue(id, out var letters) ? letters.OrderBy(c => c).ToList() : [],
                isSsd));
        }

        var volumes = new List<VolumeSummary>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                var letter = char.ToUpperInvariant(drive.Name[0]);
                bool? isSsd = disksByLetter.TryGetValue(letter, out var set) && set.Count == 1 && ssdByDisk.TryGetValue(set.First(), out var s) ? s : null;
                volumes.Add(new VolumeSummary(letter, string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel, (ulong)drive.TotalSize, (ulong)drive.TotalFreeSpace, isSsd, drive.DriveFormat));
            }
            catch (IOException ex)
            {
                log.Warn("Том недоступен: " + drive.Name, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                log.Warn("Нет доступа к тому: " + drive.Name, ex);
            }
        }

        return (diskList, volumes.OrderBy(v => v.Letter).ToList());
    }

    /// <summary>Тип накопителя для буквы тома: true — SSD, false — HDD, null — не удалось определить однозначно.</summary>
    public bool? IsSsd(char letter) => Get().Volumes.FirstOrDefault(v => v.Letter == char.ToUpperInvariant(letter))?.IsSsd;

    /// <summary>Счётчики надёжности (температура, износ). Требуют прав администратора.</summary>
    public IReadOnlyList<DiskReliability> ReadReliability()
    {
        var result = new List<DiskReliability>();
        foreach (var disk in Wmi.Query(StorageScope, "SELECT ObjectId, FriendlyName FROM MSFT_PhysicalDisk", Wmi.DefaultTimeout))
        {
            var objectId = disk.Str("ObjectId");
            if (objectId is null)
            {
                continue;
            }

            var escaped = objectId.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var counters = Wmi.Query(
                StorageScope,
                $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId=\"{escaped}\"}} WHERE AssocClass = MSFT_PhysicalDiskToStorageReliabilityCounter",
                Wmi.DefaultTimeout);
            var c = counters.FirstOrDefault();
            if (c is null)
            {
                continue;
            }

            double? temp = c.I32("Temperature") is { } t and > 0 ? t : null;
            double? wear = c.I32("Wear") is { } w ? w : null;
            result.Add(new DiskReliability(
                disk.Str("FriendlyName") ?? "?",
                temp,
                wear,
                c.U64("PowerOnHours") is { } h ? (long)h : null,
                c.U64("ReadErrorsTotal") is { } re ? (long)re : null,
                c.U64("WriteErrorsTotal") is { } we ? (long)we : null));
        }

        return result;
    }

    private static char? LetterOf(Dictionary<string, object?> row)
    {
        if (!row.TryGetValue("DriveLetter", out var v) || v is null)
        {
            return null;
        }

        var c = v switch
        {
            char ch => ch,
            ushort u => (char)u,
            string s when s.Length > 0 => s[0],
            _ => '\0',
        };
        return char.IsAsciiLetter(c) ? char.ToUpperInvariant(c) : null;
    }

    public static string MediaTypeName(int code) => code switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => Loc.T("не указан", "unspecified"),
    };

    public static string BusTypeName(int code) => code switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "File Backed Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => code.ToString(CultureInfo.InvariantCulture),
    };

    public static string HealthName(int? code) => code switch
    {
        0 => Loc.T("Исправен", "Healthy"),
        1 => Loc.T("Предупреждение", "Warning"),
        2 => Loc.T("Неисправен", "Unhealthy"),
        _ => Loc.T("Неизвестно", "Unknown"),
    };
}
