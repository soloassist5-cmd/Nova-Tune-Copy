namespace NovaTune.Core.Common;

/// <summary>
/// Песочница: реестр и схемы питания в памяти. Используется в тестах, чтобы проверять
/// бэкап и откат, не трогая настоящую систему.
/// </summary>
public sealed class InMemorySettingsStore : ISystemSettingsStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, RegValue>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PowerSchemeInfo> _schemes =
    [
        new(PowerSchemeInfo.Balanced, "Сбалансированная"),
        new(PowerSchemeInfo.HighPerformance, "Высокая производительность"),
        new(PowerSchemeInfo.PowerSaver, "Экономия энергии"),
    ];

    private Guid? _active = PowerSchemeInfo.Balanced;

    /// <summary>Если задано, запись в указанный куст бросает это исключение (имитация сбоя/отказа UAC).</summary>
    public Func<RegHive, string, string, Exception?>? WriteFailure { get; set; }

    public int WriteCount { get; private set; }

    private static string K(RegHive hive, string key) => RegPath.HiveName(hive) + "\\" + RegPath.Normalize(key);

    public bool KeyExists(RegHive hive, string key)
    {
        lock (_sync)
        {
            var k = K(hive, key);
            return _keys.ContainsKey(k) || _keys.Keys.Any(x => x.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase));
        }
    }

    public RegValue? ReadValue(RegHive hive, string key, string name)
    {
        lock (_sync)
        {
            return _keys.TryGetValue(K(hive, key), out var values) && values.TryGetValue(name, out var v) ? v : null;
        }
    }

    public IReadOnlyList<string> GetValueNames(RegHive hive, string key)
    {
        lock (_sync)
        {
            return _keys.TryGetValue(K(hive, key), out var values) ? values.Keys.ToList() : [];
        }
    }

    public void WriteValue(RegHive hive, string key, string name, RegValue value)
    {
        if (WriteFailure?.Invoke(hive, key, name) is { } ex)
        {
            throw ex;
        }

        lock (_sync)
        {
            var k = K(hive, key);
            if (!_keys.TryGetValue(k, out var values))
            {
                values = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
                _keys[k] = values;
            }

            values[name] = value;
            WriteCount++;
        }
    }

    public void DeleteValue(RegHive hive, string key, string name)
    {
        if (WriteFailure?.Invoke(hive, key, name) is { } ex)
        {
            throw ex;
        }

        lock (_sync)
        {
            if (_keys.TryGetValue(K(hive, key), out var values))
            {
                values.Remove(name);
            }
        }
    }

    public void DeleteKeyIfEmpty(RegHive hive, string key)
    {
        lock (_sync)
        {
            var k = K(hive, key);
            var hasChildren = _keys.Keys.Any(x => x.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase));
            if (_keys.TryGetValue(k, out var values) && values.Count == 0 && !hasChildren)
            {
                _keys.Remove(k);
            }
        }
    }

    public Guid? GetActivePowerScheme()
    {
        lock (_sync)
        {
            return _active;
        }
    }

    public IReadOnlyList<PowerSchemeInfo> GetPowerSchemes()
    {
        lock (_sync)
        {
            return _schemes.ToList();
        }
    }

    public void SetActivePowerScheme(Guid scheme)
    {
        lock (_sync)
        {
            if (_schemes.All(s => s.Id != scheme))
            {
                throw new InvalidOperationException("Схема электропитания не найдена: " + scheme);
            }

            _active = scheme;
        }
    }

    public Guid DuplicatePowerScheme(Guid source)
    {
        lock (_sync)
        {
            var id = Guid.NewGuid();
            var name = source == PowerSchemeInfo.UltimatePerformanceTemplate ? "Максимальная производительность" : "Копия схемы";
            _schemes.Add(new PowerSchemeInfo(id, name));
            return id;
        }
    }

    public void DeletePowerScheme(Guid scheme)
    {
        lock (_sync)
        {
            if (_active == scheme)
            {
                throw new InvalidOperationException("Нельзя удалить активную схему.");
            }

            _schemes.RemoveAll(s => s.Id == scheme);
        }
    }
}
