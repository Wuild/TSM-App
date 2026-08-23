using System.Globalization;

namespace TSM.Models;

public sealed record BackupEntry(
    string SystemId,
    string Account,
    long Timestamp,
    bool IsLocal,
    bool IsRemote,
    bool Keep,
    string LocalPath = "",
    string RemoteName = "")
{
    public DateTimeOffset Created => BackupTimestamp.ToDateTimeOffset(Timestamp);
    public string CreatedText => Created.ToString("g");
    public string Location => IsLocal && IsRemote ? "Local · Cloud" : IsRemote ? "Cloud" : "Local";
    public string SystemText => IsRemote ? SystemId : "This computer";
}

public static class BackupTimestamp
{
    private static readonly string[] LegacyFormats = ["yyyyMMddHHmmssfff", "yyyyMMddHHmmss"];

    public static DateTimeOffset ToDateTimeOffset(long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Length is 14 or 17 && DateTime.TryParseExact(text, LegacyFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var legacy))
            return new DateTimeOffset(legacy).ToLocalTime();
        if (value is >= -62135596800 and <= 253402300799)
            return DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime();
        if (value is >= -62135596800000 and <= 253402300799999)
            return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime();
        return DateTimeOffset.UnixEpoch.ToLocalTime();
    }
}
