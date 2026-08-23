using System.Text;

namespace TSM.Services;

public sealed class AppLogService(AppPaths paths)
{
    private const long MaximumFileBytes = 5_000_000;
    private const int MaximumDisplayedCharacters = 1_500_000;
    private readonly object _writeLock = new();

    public void WriteSync(string level, string message)
    {
        try
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(paths.DataDirectory);
                Rotate(paths.SyncLogFile);
                File.AppendAllText(paths.SyncLogFile,
                    $"[{DateTimeOffset.Now:O}] {level.ToUpperInvariant()} {message.Trim()}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public string ReadCombined(bool truncateForDisplay = true)
    {
        var sync = Read(paths.SyncLogFile, truncateForDisplay);
        var errors = Read(paths.ErrorLogFile, truncateForDisplay);
        if (sync.Length == 0 && errors.Length == 0)
            return string.Empty;
        var output = new StringBuilder();
        if (sync.Length > 0)
            output.AppendLine("SYNC LOG").AppendLine(sync.TrimEnd()).AppendLine();
        if (errors.Length > 0)
            output.AppendLine("APPLICATION ERRORS").AppendLine(errors.TrimEnd()).AppendLine();
        return output.ToString();
    }

    public string ReadSyncLog(bool truncateForDisplay = false) =>
        Read(paths.SyncLogFile, truncateForDisplay);

    public string ReadForDisplay()
    {
        var source = ReadCombined();
        if (source.Length == 0)
            return string.Empty;
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0 || line[0] != '[')
                continue;
            var end = line.IndexOf(']');
            if (end <= 1 || !DateTimeOffset.TryParse(line[1..end], out var timestamp))
                continue;
            lines[index] = $"[{timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}]" + line[(end + 1)..];
        }
        return string.Join(Environment.NewLine, lines).TrimEnd();
    }

    private static string Read(string path, bool truncate)
    {
        try
        {
            if (!File.Exists(path))
                return string.Empty;
            var value = File.ReadAllText(path);
            return !truncate || value.Length <= MaximumDisplayedCharacters
                ? value
                : "… earlier entries omitted …" + Environment.NewLine + value[^MaximumDisplayedCharacters..];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Could not read {Path.GetFileName(path)}: {exception.Message}";
        }
    }

    private static void Rotate(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= MaximumFileBytes)
            return;
        File.Move(path, path + ".previous", true);
    }
}
