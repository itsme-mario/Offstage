using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Offstage;

/// <summary>
/// The user's personal "never freeze" list, layered on top of the built-in system safety list.
/// Two independent mechanisms: exact process names (durable, the everyday tool) and title
/// substrings (for finer control, e.g. only a specific window of an app). Persisted as JSON in
/// %APPDATA%\Offstage so choices survive restarts.
/// </summary>
internal sealed class OptOutStore
{
    private sealed class Settings
    {
        public List<string> ProcessNames { get; set; } = new();
        public List<string> TitleSubstrings { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private Settings _settings = new();
    private HashSet<string> _processSet = new(StringComparer.OrdinalIgnoreCase);

    public OptOutStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Offstage");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "optout.json");
        Load();
    }

    public string ConfigPath => _path;
    public IReadOnlyList<string> ProcessNames => _settings.ProcessNames;
    public IReadOnlyList<string> TitleSubstrings => _settings.TitleSubstrings;

    public bool IsProcessExcluded(string processName) => _processSet.Contains(processName);

    public bool IsTitleExcluded(string title)
    {
        if (string.IsNullOrEmpty(title))
            return false;

        foreach (string sub in _settings.TitleSubstrings)
            if (sub.Length > 0 && title.Contains(sub, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>Adds the process name to the list if absent, removes it if present.</summary>
    public void ToggleProcess(string processName)
    {
        if (_processSet.Add(processName))
            _settings.ProcessNames.Add(processName);
        else
        {
            _processSet.Remove(processName);
            _settings.ProcessNames.RemoveAll(n => string.Equals(n, processName, StringComparison.OrdinalIgnoreCase));
        }
        Save();
    }

    public void AddTitleSubstring(string substring)
    {
        substring = substring.Trim();
        if (substring.Length == 0 ||
            _settings.TitleSubstrings.Any(s => string.Equals(s, substring, StringComparison.OrdinalIgnoreCase)))
            return;

        _settings.TitleSubstrings.Add(substring);
        Save();
    }

    public void RemoveTitleSubstring(string substring)
    {
        _settings.TitleSubstrings.RemoveAll(s => string.Equals(s, substring, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                Settings? loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path));
                if (loaded is not null)
                    _settings = loaded;
            }
        }
        catch
        {
            _settings = new Settings(); // Corrupt file -> start clean rather than crash.
        }

        _processSet = new HashSet<string>(_settings.ProcessNames, StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_settings, JsonOptions));
        }
        catch
        {
            // Best-effort; a failed write must never take down the app.
        }
    }
}
