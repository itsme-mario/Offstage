using System;
using System.IO;
using System.Text.Json;

namespace Offstage;

/// <summary>User-tunable settings, editable in the tray UI or by hand in settings.json.</summary>
internal sealed class AppSettings
{
    public int GraceSeconds { get; set; } = 5;
    public bool AutoEnabled { get; set; } = true;
    public string FreezeHotkey { get; set; } = "Ctrl+Alt+S";
    public string ThawHotkey { get; set; } = "Ctrl+Alt+R";

    /// <summary>
    /// Experimental: for Chromium/Electron apps (Edge, Chrome, Slack, VS Code, …) suspend only the
    /// renderer/GPU/utility children and leave the main "broker" process running, so you can still
    /// open a new window of a frozen browser on the current desktop. Off by default.
    /// </summary>
    public bool LeaveBrokerAlive { get; set; } = false;
}

/// <summary>Loads and persists <see cref="AppSettings"/> at %APPDATA%\Offstage\settings.json.</summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public AppSettings Current { get; private set; } = new();

    public SettingsStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Offstage");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Load();
    }

    public string ConfigPath => _path;

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions)); }
        catch { /* best-effort */ }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
                if (loaded is not null)
                {
                    Current = loaded;
                    return;
                }
            }

            Save(); // Materialise a default file the user can discover and edit.
        }
        catch
        {
            Current = new AppSettings();
        }
    }
}
