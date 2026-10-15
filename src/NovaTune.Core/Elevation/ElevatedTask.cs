using NovaTune.Core.Common;
using NovaTune.Core.Optimization;
using NovaTune.Core.Startup;

namespace NovaTune.Core.Elevation;

public enum ElevatedOperation
{
    RegistrySet,
    RegistryDelete,

    /// <summary>TRIM SSD штатной утилитой (defrag X: /L).</summary>
    VolumeReTrim,

    /// <summary>Оптимизация HDD штатной утилитой (defrag X: /O).</summary>
    VolumeOptimizeHdd,
    CreateRestorePoint,

    /// <summary>Чтение датчиков SMART/температур дисков, недоступных без прав администратора.</summary>
    ReadStorageReliability,
}

/// <summary>Описание операции для повышенного процесса. Передаётся через JSON-файл.</summary>
public sealed record ElevatedTaskRequest
{
    public ElevatedOperation Operation { get; init; }

    public RegHive? Hive { get; init; }

    public string? Key { get; init; }

    public string? Name { get; init; }

    public RegValue? Value { get; init; }

    public char? DriveLetter { get; init; }

    public string? Description { get; init; }
}

public sealed record ElevatedTaskResult(bool Success, string? Error, string? Output = null);

/// <summary>
/// Белый список повышенного процесса. Он не принимает произвольные команды: только перечисленные
/// ключи реестра с ограниченными значениями, операции над томами по букве диска и создание точки восстановления.
/// </summary>
public static class ElevatedTaskValidator
{
    private static readonly string[] StartupApprovedKeys =
    [
        RegPath.Normalize(StartupApproved.RunKey),
        RegPath.Normalize(StartupApproved.Run32Key),
        RegPath.Normalize(StartupApproved.StartupFolderKey),
    ];

    public static (bool Ok, string? Reason) Validate(ElevatedTaskRequest request)
    {
        switch (request.Operation)
        {
            case ElevatedOperation.RegistrySet:
            case ElevatedOperation.RegistryDelete:
                return ValidateRegistry(request);

            case ElevatedOperation.VolumeReTrim:
            case ElevatedOperation.VolumeOptimizeHdd:
                return request.DriveLetter is { } d && char.IsAsciiLetter(d)
                    ? (true, null)
                    : (false, "Некорректная буква диска.");

            case ElevatedOperation.CreateRestorePoint:
                return string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 200 || request.Description.Any(char.IsControl)
                    ? (false, "Некорректное описание точки восстановления.")
                    : (true, null);

            case ElevatedOperation.ReadStorageReliability:
                return (true, null);

            default:
                return (false, "Операция не входит в белый список.");
        }
    }

    private static (bool, string?) ValidateRegistry(ElevatedTaskRequest r)
    {
        if (r.Hive != RegHive.LocalMachine || r.Key is null || r.Name is null)
        {
            return (false, "Повышенный процесс меняет только перечисленные ключи HKLM.");
        }

        var key = RegPath.Normalize(r.Key);
        if (r.Name.Length > 260 || r.Name.Any(char.IsControl))
        {
            return (false, "Некорректное имя значения.");
        }

        // HAGS: HwSchMode = 1 или 2 (или удаление, если значения не было до изменения).
        if (key.Equals(RegPath.Normalize(KnownKeys.GraphicsDrivers), StringComparison.OrdinalIgnoreCase))
        {
            if (!r.Name.Equals(KnownKeys.HwSchMode, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Разрешено менять только HwSchMode.");
            }

            if (r.Operation == ElevatedOperation.RegistryDelete)
            {
                return (true, null);
            }

            return r.Value?.AsDWord is 1 or 2 ? (true, null) : (false, "HwSchMode может быть только 1 или 2.");
        }

        // Флаги автозагрузки диспетчера задач: двоичные 8–12 байт, первый байт 0x02/0x03/0x06/0x07.
        if (StartupApprovedKeys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            if (r.Operation == ElevatedOperation.RegistryDelete)
            {
                return (true, null);
            }

            return r.Value is { Kind: RegKind.Binary, Bytes: { Length: >= 8 and <= 12 } b } && b[0] is 0x02 or 0x03 or 0x06 or 0x07
                ? (true, null)
                : (false, "Некорректное значение флага автозагрузки.");
        }

        return (false, "Ключ не входит в белый список: " + key);
    }
}
