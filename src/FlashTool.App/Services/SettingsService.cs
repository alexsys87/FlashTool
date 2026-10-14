using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashTool.App.Services;

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON in %LOCALAPPDATA%\FlashTool.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsService()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashTool");
        _path = Path.Combine(folder, "settings.json");
        Settings = Load();
    }

    public AppSettings Settings { get; }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Settings, JsonOptions));
        }
        catch (IOException)
        {
            // Settings are a convenience; failing to persist them must not break the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) is { } loaded)
            {
                Migrate(loaded);
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new AppSettings { Version = AppSettings.CurrentVersion };
    }

    private static void Migrate(AppSettings settings)
    {
        if (settings.Version < 2)
        {
            // Version 2: expanding the last partition became opt-in.
            settings.ExpandLastPartition = false;
        }

        settings.Version = AppSettings.CurrentVersion;
    }
}
