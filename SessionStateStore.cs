using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Offstage;

/// <summary>Identity of a process Offstage suspended, durable enough to re-verify after a restart.</summary>
internal sealed class FrozenRecord
{
    public uint Pid { get; set; }
    public string? ProcessName { get; set; }

    /// <summary>Process start time (round-trip "O", UTC) — guards against PID reuse on recovery.</summary>
    public string? StartedUtc { get; set; }
}

/// <summary>
/// Persists the set of currently-frozen processes so a crash or hard kill (Ctrl+C, closing the
/// terminal, End Task) can't strand apps in a suspended state with no record of them. On the next
/// launch these records let Offstage find and resume the orphans it left behind.
/// </summary>
internal sealed class SessionStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public SessionStateStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Offstage");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "session.json");
    }

    public void Save(IReadOnlyCollection<FrozenRecord> records)
    {
        try
        {
            if (records.Count == 0)
            {
                Clear();
                return;
            }
            File.WriteAllText(_path, JsonSerializer.Serialize(records, JsonOptions));
        }
        catch
        {
            // Best-effort; a failed write must never take down the app.
        }
    }

    public List<FrozenRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                List<FrozenRecord>? records = JsonSerializer.Deserialize<List<FrozenRecord>>(File.ReadAllText(_path));
                if (records is not null)
                    return records;
            }
        }
        catch
        {
            // Corrupt file -> treat as no prior state.
        }
        return new List<FrozenRecord>();
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
            // Ignore; a stale file is harmless because recovery re-verifies every record.
        }
    }
}
