namespace TSM.Models;

public sealed class LuaTable
{
    private readonly Dictionary<object, object?> _values = [];

    public IEnumerable<KeyValuePair<object, object?>> Entries => _values;
    public IEnumerable<object?> Values => _values.Values;
    public int Count => _values.Count;

    public object? this[object key]
    {
        get => _values[key];
        set => _values[key] = value;
    }

    public bool TryGetValue(object key, out object? value) => _values.TryGetValue(NormalizeKey(key), out value);

    public bool TryGetTable(object key, out LuaTable table)
    {
        if (TryGetValue(key, out var value) && value is LuaTable found)
        {
            table = found;
            return true;
        }

        table = null!;
        return false;
    }

    public bool TryGetString(object key, out string value)
    {
        if (TryGetValue(key, out var found) && found is string text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    internal void Add(object key, object? value) => _values[NormalizeKey(key)] = value;

    private static object NormalizeKey(object key) => key switch
    {
        int number => (long)number,
        short number => (long)number,
        byte number => (long)number,
        double number when number == Math.Truncate(number) => (long)number,
        float number when number == MathF.Truncate(number) => (long)number,
        _ => key
    };
}

public sealed record LuaDocument(IReadOnlyDictionary<string, object?> Globals)
{
    public bool TryGetTable(string name, out LuaTable table)
    {
        if (Globals.TryGetValue(name, out var value) && value is LuaTable found)
        {
            table = found;
            return true;
        }

        table = null!;
        return false;
    }
}

public sealed class LuaParseException(string message, int line, int column)
    : FormatException($"{message} (line {line}, column {column})")
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}
