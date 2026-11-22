using System.Text.Json;
using Fluorite.Core.Json;
using Fluorite.Core.Settings;

namespace Fluorite.App.Services;

/// <summary>Loads and saves <see cref="LauncherSettings"/> at <c>&lt;root&gt;/settings.json</c>.</summary>
public sealed class SettingsService
{
    private readonly string _path = Path.Combine(AppPaths.DataRoot, "settings.json");

    public SettingsService()
    {
        Current = Load();
    }

    public LauncherSettings Current { get; }

    public event EventHandler? Saved;

    private LauncherSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path), FluoriteJsonContext.Default.LauncherSettings) ?? new LauncherSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            File.Copy(_path, _path + ".bad", overwrite: true);
        }
        return new LauncherSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, FluoriteJsonContext.Default.LauncherSettings));
        File.Move(tmp, _path, overwrite: true);
        Saved?.Invoke(this, EventArgs.Empty);
    }
}
