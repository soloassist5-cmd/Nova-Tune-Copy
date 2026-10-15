namespace NovaTune.Core.Common;

/// <summary>Схема электропитания Windows.</summary>
public sealed record PowerSchemeInfo(Guid Id, string Name)
{
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// <summary>Шаблон «Максимальная производительность» (в Windows 10 скрыт, создаётся дублированием).</summary>
    public static readonly Guid UltimatePerformanceTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
}

/// <summary>
/// Абстракция над системными настройками: реестр и схемы электропитания.
/// В приложении — реальный реестр, в тестах — <see cref="InMemorySettingsStore"/> (песочница).
/// </summary>
public interface ISystemSettingsStore
{
    bool KeyExists(RegHive hive, string key);

    /// <summary>Читает значение. Возвращает null, если нет ключа или значения.</summary>
    RegValue? ReadValue(RegHive hive, string key, string name);

    IReadOnlyList<string> GetValueNames(RegHive hive, string key);

    /// <summary>Записывает значение, при необходимости создавая ключ.</summary>
    void WriteValue(RegHive hive, string key, string name, RegValue value);

    /// <summary>Удаляет значение; отсутствие значения ошибкой не считается.</summary>
    void DeleteValue(RegHive hive, string key, string name);

    /// <summary>Удаляет ключ, только если в нём нет ни значений, ни подключей.</summary>
    void DeleteKeyIfEmpty(RegHive hive, string key);

    Guid? GetActivePowerScheme();

    IReadOnlyList<PowerSchemeInfo> GetPowerSchemes();

    void SetActivePowerScheme(Guid scheme);

    /// <summary>Создаёт копию схемы (например, скрытой «Максимальной производительности») и возвращает её GUID.</summary>
    Guid DuplicatePowerScheme(Guid source);

    void DeletePowerScheme(Guid scheme);
}

/// <summary>Пользователь отклонил запрос UAC — штатная ситуация, а не сбой.</summary>
public sealed class ElevationCancelledException : Exception
{
    public ElevationCancelledException()
        : base("Запрос прав администратора отклонён пользователем.")
    {
    }
}
