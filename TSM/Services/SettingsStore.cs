using System.Text.Json;
using TSM.Models;

namespace TSM.Services;

public sealed class SettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _saveLock = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(paths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile), JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(paths.DataDirectory);
            var temporaryFile = paths.SettingsFile + ".tmp";
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryFile, paths.SettingsFile, true);
        }
    }
}
