using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Offstage;

/// <summary>
/// The running application: a tray icon, two global hotkeys, and an optional auto-freeze watcher.
///   Ctrl+Alt+S  freeze — suspend every app whose windows are all on OTHER virtual desktops.
///   Ctrl+Alt+R  thaw   — resume everything currently frozen.
///   Auto-freeze — a stateless reconcile loop: any frozen app whose window is back on the current
///                 desktop is thawed at once; any eligible app that has been fully off the current
///                 desktop for the grace delay is frozen. It never tries to catch the switch moment,
///                 which makes it robust even though freezing an app makes its window unresponsive.
///   Never freeze — a user opt-out list (by process name and/or title substring) that sits on top of
///                 the built-in system safety list.
/// Frozen apps are never closed, so their window positions and orientation survive untouched.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int HotkeyFreezeId = 1;
    private const int HotkeyThawId = 2;

    private const int WatchIntervalMs = 500;
    private static readonly int[] GracePresetsSeconds = { 2, 5, 10, 30, 60 };

    // System / shell processes we must never suspend. ApplicationFrameHost in particular hosts the
    // window frames of ALL UWP apps across every desktop, so freezing it would freeze UWP windows
    // on the current desktop too.
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "applicationframehost", "sihost", "shellexperiencehost",
        "searchhost", "searchapp", "startmenuexperiencehost", "textinputhost",
        "systemsettings", "lockapp", "csrss", "winlogon", "ctfmon"
    };

    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly ToolStripMenuItem _autoMenuItem;
    private readonly ToolStripMenuItem _neverFreezeMenu;
    private readonly ToolStripMenuItem _graceMenu;
    private readonly ToolStripMenuItem _brokerModeItem;
    private readonly ToolStripMenuItem _runAtLoginItem;
    private readonly System.Windows.Forms.Timer _watchTimer;
    private readonly OptOutStore _optOut = new();
    private readonly SessionStateStore _sessionState = new();
    private readonly SettingsStore _settings = new();

    // Kept alive for the lifetime of the app so the native console handler isn't garbage-collected.
    private readonly NativeMethods.ConsoleCtrlHandler _consoleHandler;

    // Root PID -> exact list of PIDs suspended for it, so thaw resumes precisely what freeze froze.
    private readonly Dictionary<uint, IReadOnlyList<uint>> _frozen = new();

    // For broker apps frozen children-only (keep-broker-alive mode): root PID -> the window handles
    // the app owned at freeze time. Lets thaw distinguish "user switched to the frozen app's desktop"
    // (thaw) from "user opened a NEW window of the same app on this desktop" (keep background frozen).
    private readonly Dictionary<uint, HashSet<IntPtr>> _frozenBrokerWindows = new();

    // PID -> first time it was observed fully off the current desktop (drives the grace delay).
    private readonly Dictionary<uint, DateTime> _offDesktopSince = new();

    // PID -> process name (or null if unidentifiable), cached to avoid opening a handle every tick.
    private readonly Dictionary<uint, string?> _processNameCache = new();

    private bool _autoEnabled = true;
    private bool _leaveBrokerAlive;
    private bool _shuttingDown;
    private TimeSpan _graceDelay = TimeSpan.FromSeconds(5);

    public TrayApplicationContext()
    {
        _autoEnabled = _settings.Current.AutoEnabled;
        _leaveBrokerAlive = _settings.Current.LeaveBrokerAlive;
        _graceDelay = TimeSpan.FromSeconds(Math.Clamp(_settings.Current.GraceSeconds, 1, 3600));

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkey;

        _autoMenuItem = new ToolStripMenuItem("Auto-freeze on desktop switch", null, (_, _) => ToggleAuto())
        {
            Checked = _autoEnabled,
            CheckOnClick = false
        };

        _neverFreezeMenu = new ToolStripMenuItem("Never freeze…");
        _neverFreezeMenu.DropDownOpening += (_, _) => RebuildNeverFreezeMenu();

        _graceMenu = new ToolStripMenuItem("Freeze delay");
        _graceMenu.DropDownOpening += (_, _) => RebuildGraceMenu();

        _brokerModeItem = new ToolStripMenuItem(
            "Keep browser broker alive (experimental)", null, (_, _) => ToggleBrokerMode())
        {
            Checked = _leaveBrokerAlive,
            CheckOnClick = false
        };

        _runAtLoginItem = new ToolStripMenuItem("Run at login", null, (_, _) => ToggleRunAtLogin())
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = false
        };

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Offstage",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        RegisterHotkeys();

        // Resume anything a previous session left suspended before we start managing state ourselves,
        // so a prior crash / hard kill can't strand apps frozen.
        RecoverOrphansFromPreviousSession();

        // Best-effort thaw on exit paths that bypass the tray Exit item: normal process shutdown,
        // logoff/shutdown, and a Ctrl+C / console-close when run from a terminal. A true hard kill
        // (End Task) can't be caught in-process — that's what the session-state recovery above is for.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ThawAllSilently();
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => ThawAllSilently();
        _consoleHandler = OnConsoleCtrl;
        NativeMethods.SetConsoleCtrlHandler(_consoleHandler, true);

        _watchTimer = new System.Windows.Forms.Timer { Interval = WatchIntervalMs };
        _watchTimer.Tick += OnWatchTick;
        _watchTimer.Start();

        UpdateTrayText();
        ShowBalloon("Offstage is running",
            "Auto-freeze is ON. Ctrl+Alt+S freeze · Ctrl+Alt+R thaw · set exclusions in the tray menu.");
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_autoMenuItem);
        menu.Items.Add(_graceMenu);
        menu.Items.Add(_neverFreezeMenu);
        menu.Items.Add(_brokerModeItem);
        menu.Items.Add(_runAtLoginItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Freeze background desktops", null, (_, _) => ManualFreeze());
        menu.Items.Add("Thaw all", null, (_, _) => ThawAll(announce: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    private void RegisterHotkeys()
    {
        if (!Hotkey.TryParse(_settings.Current.FreezeHotkey, out Hotkey freeze))
            Hotkey.TryParse("Ctrl+Alt+S", out freeze);
        if (!Hotkey.TryParse(_settings.Current.ThawHotkey, out Hotkey thaw))
            Hotkey.TryParse("Ctrl+Alt+R", out thaw);

        // Non-short-circuiting so both hotkeys are attempted even if the first fails.
        bool ok = NativeMethods.RegisterHotKey(_hotkeyWindow.Handle, HotkeyFreezeId, freeze.Modifiers, freeze.VirtualKey)
                & NativeMethods.RegisterHotKey(_hotkeyWindow.Handle, HotkeyThawId, thaw.Modifiers, thaw.VirtualKey);

        if (!ok)
        {
            ShowBalloon("Hotkey registration failed",
                "A configured hotkey is invalid or already in use by another app. Use the tray menu instead.");
        }
    }

    private void OnHotkey(int id)
    {
        if (id == HotkeyFreezeId) ManualFreeze();
        else if (id == HotkeyThawId) ThawAll(announce: true);
    }

    // ---- "Never freeze" opt-out submenu ----

    private void RebuildNeverFreezeMenu()
    {
        ToolStripItemCollection items = _neverFreezeMenu.DropDownItems;
        items.Clear();

        // Running apps (minus system-protected ones) merged with any already-excluded names.
        IEnumerable<string> running = WindowManager.EnumerateAppWindows()
            .Select(w => GetProcessName(w.ProcessId))
            .Where(n => n is not null)
            .Select(n => n!)
            .Where(n => !ProtectedProcesses.Contains(n));

        List<string> names = running
            .Union(_optOut.ProcessNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        items.Add(new ToolStripMenuItem("Apps — check to never freeze") { Enabled = false });
        if (names.Count == 0)
            items.Add(new ToolStripMenuItem("(no eligible apps running)") { Enabled = false });

        foreach (string name in names)
        {
            string captured = name;
            var item = new ToolStripMenuItem(name)
            {
                Checked = _optOut.IsProcessExcluded(name),
                CheckOnClick = true
            };
            item.Click += (_, _) =>
            {
                _optOut.ToggleProcess(captured);
                ThawNewlyExcluded();
            };
            items.Add(item);
        }

        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Title rules — click to remove") { Enabled = false });

        foreach (string rule in _optOut.TitleSubstrings.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            string captured = rule;
            var item = new ToolStripMenuItem($"Title contains “{rule}”   ✕");
            item.Click += (_, _) => _optOut.RemoveTitleSubstring(captured);
            items.Add(item);
        }

        var addRule = new ToolStripMenuItem("Add title rule…");
        addRule.Click += (_, _) =>
        {
            string? input = InputDialog.Show("Never freeze windows whose title contains:", "Add title rule");
            if (!string.IsNullOrWhiteSpace(input))
            {
                _optOut.AddTitleSubstring(input);
                ThawNewlyExcluded();
            }
        };
        items.Add(addRule);
    }

    /// <summary>Immediately thaw anything currently frozen that the opt-out list now covers.</summary>
    private void ThawNewlyExcluded()
    {
        if (_frozen.Count == 0)
            return;

        Dictionary<uint, List<ManagedWindow>> windowsByPid = EnumerateWindowsByPid();
        bool changed = false;

        foreach (uint pid in _frozen.Keys.ToList())
        {
            string? name = GetProcessName(pid);
            bool titleHit = windowsByPid.TryGetValue(pid, out List<ManagedWindow>? ws)
                            && ws.Any(w => _optOut.IsTitleExcluded(w.Title));

            if ((name is not null && _optOut.IsProcessExcluded(name)) || titleHit)
            {
                try { ProcessSuspender.Resume(_frozen[pid]); } catch { /* keep going */ }
                _frozen.Remove(pid);
                _frozenBrokerWindows.Remove(pid);
                _offDesktopSince.Remove(pid);
                changed = true;
            }
        }

        if (changed)
        {
            UpdateTrayText();
            SaveSessionState();
        }
    }

    // ---- Auto-freeze watcher ----

    private void ToggleAuto()
    {
        _autoEnabled = !_autoEnabled;
        _autoMenuItem.Checked = _autoEnabled;
        _offDesktopSince.Clear();

        _settings.Current.AutoEnabled = _autoEnabled;
        _settings.Save();

        if (_autoEnabled)
        {
            ShowBalloon("Auto-freeze on", "Background desktops will be suspended automatically.");
        }
        else
        {
            ThawAll(announce: false);
            ShowBalloon("Auto-freeze off", "Everything thawed. Use the hotkeys for manual control.");
        }
    }

    private void RebuildGraceMenu()
    {
        ToolStripItemCollection items = _graceMenu.DropDownItems;
        items.Clear();

        int current = (int)_graceDelay.TotalSeconds;
        foreach (int seconds in GracePresetsSeconds)
        {
            int captured = seconds;
            var item = new ToolStripMenuItem(seconds < 60 ? $"{seconds} seconds" : "1 minute")
            {
                Checked = seconds == current
            };
            item.Click += (_, _) => SetGrace(captured);
            items.Add(item);
        }
    }

    private void SetGrace(int seconds)
    {
        _graceDelay = TimeSpan.FromSeconds(seconds);
        _settings.Current.GraceSeconds = seconds;
        _settings.Save();
        ShowBalloon("Freeze delay updated",
            $"Background apps freeze after {(seconds < 60 ? $"{seconds} seconds" : "1 minute")} off-screen.");
    }

    private void ToggleBrokerMode()
    {
        _leaveBrokerAlive = !_leaveBrokerAlive;
        _brokerModeItem.Checked = _leaveBrokerAlive;
        _settings.Current.LeaveBrokerAlive = _leaveBrokerAlive;
        _settings.Save();

        // The mode changes *how* an app is frozen, so re-baseline: thaw everything now and let the
        // next freeze (auto reconcile or a manual hotkey) apply the new policy cleanly. Otherwise a
        // browser already frozen whole-tree would stay unusable until the user thawed it by hand.
        ThawAll(announce: false);

        ShowBalloon(
            _leaveBrokerAlive ? "Keep-broker-alive on" : "Keep-broker-alive off",
            _leaveBrokerAlive
                ? "Chromium/Electron apps (Edge, Chrome, Slack…) will keep their main process running — only background workers are suspended. Everything was thawed to re-baseline."
                : "Chromium/Electron apps will be fully suspended again. Everything was thawed to re-baseline.");
    }

    private void ToggleRunAtLogin()
    {
        bool desired = !_runAtLoginItem.Checked;
        if (StartupManager.TrySetEnabled(desired))
        {
            _runAtLoginItem.Checked = desired;
            ShowBalloon(desired ? "Run at login on" : "Run at login off",
                desired
                    ? "Offstage will start automatically when you sign in."
                    : "Offstage will no longer start at login.");
        }
        else
        {
            ShowBalloon("Couldn't update startup", "Failed to write the startup registry entry.");
        }
    }

    /// <summary>
    /// Native console-control callback (Ctrl+C, console close) — resumes everything before the
    /// process is torn down. Best-effort: only fires when a console is attached (e.g. `dotnet run`).
    /// Returns false so the default handler still terminates the process.
    /// </summary>
    private bool OnConsoleCtrl(uint ctrlType)
    {
        ThawAllSilently();
        return false;
    }

    private void OnWatchTick(object? sender, EventArgs e)
    {
        if (_autoEnabled)
            Reconcile();
    }

    /// <summary>
    /// One idempotent pass: thaw anything that's back on the current desktop, and freeze anything
    /// that has stayed off the current desktop for the whole grace delay. Uses only the direct,
    /// per-window IsWindowOnCurrentVirtualDesktop query — no foreground or desktop-id guessing.
    /// </summary>
    private void Reconcile()
    {
        uint self = (uint)Environment.ProcessId;
        DateTime now = DateTime.UtcNow;

        Dictionary<uint, List<ManagedWindow>> windowsByPid = EnumerateWindowsByPid();
        var onCurrentByPid = windowsByPid.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Any(w => WindowManager.IsOnCurrentDesktop(w.Handle)));

        bool changed = false;

        // 1) Thaw: any frozen process now showing on the current desktop.
        foreach (uint pid in _frozen.Keys.ToList())
        {
            if (ShouldThawFrozen(pid, onCurrentByPid))
            {
                try { ProcessSuspender.Resume(_frozen[pid]); } catch { /* keep going */ }
                _frozen.Remove(pid);
                _frozenBrokerWindows.Remove(pid);
                _offDesktopSince.Remove(pid);
                changed = true;
            }
        }

        // 2) Track & freeze: processes whose windows are all off the current desktop.
        foreach ((uint pid, List<ManagedWindow> windows) in windowsByPid.Select(kv => (kv.Key, kv.Value)))
        {
            if (pid == self || _frozen.ContainsKey(pid) || onCurrentByPid[pid] || ShouldSkipFreeze(pid, windows))
            {
                _offDesktopSince.Remove(pid);
                continue;
            }

            if (!_offDesktopSince.TryGetValue(pid, out DateTime since))
            {
                _offDesktopSince[pid] = now; // first tick seen off-desktop; start the clock.
            }
            else if (now - since >= _graceDelay)
            {
                if (TryFreeze(pid, windows)) changed = true;
                _offDesktopSince.Remove(pid);
            }
        }

        // 3) Drop timers for processes that have since vanished.
        foreach (uint pid in _offDesktopSince.Keys.ToList())
            if (!windowsByPid.ContainsKey(pid))
                _offDesktopSince.Remove(pid);

        if (changed)
        {
            UpdateTrayText();
            SaveSessionState();
        }
    }

    // ---- Freeze / thaw core ----

    /// <summary>
    /// Freeze one app, recording the exact PIDs suspended and — for a broker app frozen
    /// children-only — the window handles it owned at freeze time. Returns false and changes nothing
    /// if the process resists suspension, so a single stubborn app never breaks the batch.
    /// </summary>
    private bool TryFreeze(uint pid, List<ManagedWindow> windows)
    {
        try
        {
            _frozen[pid] = ProcessSuspender.Suspend(pid, _leaveBrokerAlive, out bool brokerModeApplied);
            if (brokerModeApplied)
                _frozenBrokerWindows[pid] = windows.Select(w => w.Handle).ToHashSet();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a frozen app should thaw this tick. Ordinary app: any window on the current desktop.
    /// Broker app frozen children-only: only when one of the windows it owned AT FREEZE TIME is back
    /// on the current desktop (the user switched to that desktop). A brand-new window the broker
    /// opened on the current desktop isn't in that set, so opening a new window here leaves the
    /// background renderers frozen. If every remembered window is gone, thaw to release the orphans.
    /// </summary>
    private bool ShouldThawFrozen(uint pid, Dictionary<uint, bool> onCurrentByPid)
    {
        if (_frozenBrokerWindows.TryGetValue(pid, out HashSet<IntPtr>? frozenWindows))
        {
            bool anyLive = false;
            foreach (IntPtr handle in frozenWindows)
            {
                if (!WindowManager.IsWindow(handle))
                    continue;
                anyLive = true;
                if (WindowManager.IsOnCurrentDesktop(handle))
                    return true;
            }
            return !anyLive;
        }

        return onCurrentByPid.TryGetValue(pid, out bool onCurrent) && onCurrent;
    }

    private void ManualFreeze()
    {
        int count = FreezeBackground();
        ShowBalloon("Frozen",
            count > 0 ? $"Suspended {count} background app(s)." : "Nothing on other desktops to freeze.");
    }

    /// <summary>Freeze every eligible app whose windows are all on non-current desktops, right now.</summary>
    private int FreezeBackground()
    {
        uint self = (uint)Environment.ProcessId;
        Dictionary<uint, List<ManagedWindow>> windowsByPid = EnumerateWindowsByPid();

        int frozenCount = 0;
        foreach ((uint pid, List<ManagedWindow> windows) in windowsByPid.Select(kv => (kv.Key, kv.Value)))
        {
            if (pid == self || _frozen.ContainsKey(pid))
                continue;
            if (windows.Any(w => WindowManager.IsOnCurrentDesktop(w.Handle)))
                continue; // has a window in front of the user -> not a background app.
            if (ShouldSkipFreeze(pid, windows))
                continue;

            if (TryFreeze(pid, windows))
            {
                _offDesktopSince.Remove(pid);
                frozenCount++;
            }
        }

        UpdateTrayText();
        SaveSessionState();
        return frozenCount;
    }

    private void ThawAll(bool announce)
    {
        int count = _frozen.Count;
        foreach (IReadOnlyList<uint> pids in _frozen.Values)
        {
            try { ProcessSuspender.Resume(pids); } catch { /* keep resuming the rest */ }
        }

        _frozen.Clear();
        _frozenBrokerWindows.Clear();
        _offDesktopSince.Clear();
        UpdateTrayText();
        SaveSessionState();

        if (announce)
            ShowBalloon("Thawed", count > 0 ? $"Resumed {count} app(s)." : "Nothing was frozen.");
    }

    /// <summary>
    /// Resume every frozen process without touching the UI — safe to call from shutdown / console
    /// handler threads. Snapshots the values first so it can't trip over concurrent mutation.
    /// </summary>
    private void ThawAllSilently()
    {
        foreach (IReadOnlyList<uint> pids in _frozen.Values.ToList())
        {
            try { ProcessSuspender.Resume(pids); } catch { /* keep resuming the rest */ }
        }
        _sessionState.Clear();
    }

    // ---- Session-state persistence & recovery ----

    /// <summary>
    /// Resume anything a previous session left suspended (crash / Ctrl+C / closed terminal / End Task).
    /// Each record is re-verified against the live process by name and start time so a reused PID is
    /// never resumed by mistake.
    /// </summary>
    private void RecoverOrphansFromPreviousSession()
    {
        List<FrozenRecord> records = _sessionState.Load();
        if (records.Count == 0)
            return;

        int recovered = 0;
        foreach (FrozenRecord record in records)
        {
            if (!MatchesLiveProcess(record))
                continue;

            try
            {
                ProcessSuspender.Resume(ProcessSuspender.GetProcessTreePids(record.Pid));
                recovered++;
            }
            catch
            {
                // Process gone or inaccessible — nothing to recover.
            }
        }

        _sessionState.Clear();

        if (recovered > 0)
            ShowBalloon("Recovered", $"Resumed {recovered} app(s) left frozen by a previous session.");
    }

    private static bool MatchesLiveProcess(FrozenRecord record)
    {
        try
        {
            using Process process = Process.GetProcessById((int)record.Pid);

            if (record.ProcessName is not null &&
                !string.Equals(process.ProcessName, record.ProcessName, StringComparison.OrdinalIgnoreCase))
                return false; // PID has been reused by a different app.

            if (record.StartedUtc is not null &&
                DateTime.TryParse(record.StartedUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime started) &&
                Math.Abs((process.StartTime.ToUniversalTime() - started).TotalSeconds) > 2)
                return false; // Same PID, different process instance.

            return true;
        }
        catch
        {
            return false; // No such process anymore.
        }
    }

    private void SaveSessionState()
    {
        _sessionState.Save(_frozen.Keys.Select(BuildRecord).ToList());
    }

    private FrozenRecord BuildRecord(uint pid)
    {
        string? name = GetProcessName(pid);
        string? startedUtc = null;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            startedUtc = process.StartTime.ToUniversalTime().ToString("O");
            name ??= process.ProcessName;
        }
        catch
        {
            // Leave metadata null; recovery just performs whatever validation it still can.
        }

        return new FrozenRecord { Pid = pid, ProcessName = name, StartedUtc = startedUtc };
    }

    // ---- Eligibility & helpers ----

    /// <summary>
    /// True if a process must not be frozen: unidentifiable, system-protected, or on the user's
    /// opt-out list (by process name or by any of its window titles).
    /// </summary>
    private bool ShouldSkipFreeze(uint pid, List<ManagedWindow> windows)
    {
        string? name = GetProcessName(pid);
        if (name is null)
            return true; // Can't identify it -> leave it alone.
        if (ProtectedProcesses.Contains(name))
            return true;
        if (_optOut.IsProcessExcluded(name))
            return true;
        if (windows.Any(w => _optOut.IsTitleExcluded(w.Title)))
            return true;
        return false;
    }

    private static Dictionary<uint, List<ManagedWindow>> EnumerateWindowsByPid()
    {
        return WindowManager.EnumerateAppWindows()
            .GroupBy(w => w.ProcessId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private string? GetProcessName(uint pid)
    {
        if (_processNameCache.TryGetValue(pid, out string? cached))
            return cached;

        string? name;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch
        {
            name = null;
        }

        _processNameCache[pid] = name;
        return name;
    }

    private void UpdateTrayText()
    {
        if (_shuttingDown)
            return;

        string state = _autoEnabled ? "auto" : "manual";
        _tray.Text = _frozen.Count > 0
            ? $"Offstage — {_frozen.Count} frozen ({state})"
            : $"Offstage — idle ({state})";
    }

    private void ShowBalloon(string title, string text)
    {
        if (_shuttingDown)
            return;

        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(4000);
    }

    private void ExitApp()
    {
        _watchTimer.Stop();
        _watchTimer.Dispose();

        ThawAll(announce: false); // Safety: never leave the user's apps suspended after we exit.
        _shuttingDown = true;     // Silence UI callbacks from the ProcessExit handler after disposal.

        NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyFreezeId);
        NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyThawId);

        _tray.Visible = false;
        _tray.Dispose();
        _hotkeyWindow.DestroyHandle();
        ExitThread();
    }
}
