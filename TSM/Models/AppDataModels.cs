namespace TSM.Models;

public sealed record AppDataEntry(string Type, string Realm, long UpdatedAtUnix, string RawPayload);

public sealed class AppDataCatalog(IReadOnlyList<AppDataEntry> entries)
{
    public IReadOnlyList<AppDataEntry> Entries { get; } = entries;

    public long LastUpdate(string type, string realm) => Entries
        .Where(entry => string.Equals(entry.Type, type, StringComparison.Ordinal) &&
                        string.Equals(entry.Realm, realm, StringComparison.Ordinal))
        .Select(entry => entry.UpdatedAtUnix)
        .DefaultIfEmpty(0)
        .Max();
}
