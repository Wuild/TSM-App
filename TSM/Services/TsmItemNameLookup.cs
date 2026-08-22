using TSM.Models;

namespace TSM.Services;

internal static class TsmItemNameLookup
{
    public static IReadOnlyDictionary<string, string> Build(object? rawNames, object? rawItemStrings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rawNames is LuaTable nameTable && rawItemStrings is LuaTable itemTable)
        {
            foreach (var entry in nameTable.Entries)
            {
                if (entry.Value is string names && itemTable.TryGetString(entry.Key, out var itemStrings))
                    AddChunk(result, names, itemStrings);
            }
            return result;
        }
        if (rawNames is string namesText && rawItemStrings is string itemStringsText)
            AddChunk(result, namesText, itemStringsText);
        return result;
    }

    private static void AddChunk(IDictionary<string, string> result, string namesText, string itemStringsText)
    {
        var names = namesText.Split('\x02');
        var itemStrings = itemStringsText.Split('\x02');
        for (var index = 0; index < Math.Min(names.Length, itemStrings.Length); index++)
        {
            if (!string.IsNullOrWhiteSpace(itemStrings[index]) && !string.IsNullOrWhiteSpace(names[index]))
                result.TryAdd(itemStrings[index], names[index]);
        }
    }
}
