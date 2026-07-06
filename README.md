# Offstage

A tiny Windows tray utility that frees CPU (and trims RAM) by **suspending apps that live on
virtual desktops you aren't currently looking at** — without closing them, so every window keeps
its exact position and orientation.

## Why this exists

Windows does **not** pause classic Win32 apps just because they're on another virtual desktop. A
browser with 40 tabs or an IDE compiling on "Desktop 2" keeps burning CPU/RAM while you work on
"Desktop 1". Offstage freezes that background work on demand and thaws it when you switch back.

## ⚠️ Data-loss warning — read this first

Offstage works by **suspending running processes mid-execution**. A frozen app is paused, not saved.
Keep these risks in mind:

- **Unsaved work is not flushed.** Freezing does not save anything. If a frozen app is later killed
  (Task Manager → End Task, a crash, a forced shutdown, or a Windows update reboot) *before it is
  thawed*, any in-memory unsaved changes are lost — exactly as if you'd killed a running app.
- **Never End-Task a frozen process.** Thaw first (`Ctrl+Alt+R` or Exit Offstage), then close the app
  normally. A suspended process can't respond to a save prompt.
- **In-flight operations can break.** Suspending an app in the middle of writing a file, running a DB
  transaction, uploading, or downloading can leave that operation half-finished or corrupt. Use the
  **Never freeze…** list (below) for anything doing background writes — backup tools, sync clients,
  download managers, encoders, VMs.
- **Network connections drop.** Frozen chat/browser/VPN apps disconnect and reconnect on thaw; some
  sessions (bank logins, one-time uploads) may not survive the gap.
- **Freeze conservatively.** If in doubt, don't freeze it. Everything here is reversible *only while
  Offstage is running and the process stays alive.*

## Usage

Run the app; it lives in the system tray.

**Auto-freeze (on by default):** when you switch virtual desktops, Offstage instantly thaws any
frozen app on the desktop you just entered, and — after a 5-second grace delay — suspends the apps
on the desktop you left. Toggle it from the tray menu; turning it off thaws everything.

**Manual control:**

| Hotkey        | Action                                                              |
| ------------- | ------------------------------------------------------------------- |
| `Ctrl+Alt+S`  | **Freeze** — suspend every app whose windows are all on *other* desktops |
| `Ctrl+Alt+R`  | **Thaw** — resume everything currently frozen                       |

The same actions are on the tray icon's right-click menu. Exiting the app thaws everything first,
so you can never get "stuck" with a frozen app.

**Never freeze (opt-out list):** the tray menu's **Never freeze…** submenu lists your running apps
with a checkbox each — tick one to keep it live even on a background desktop (e.g. a music player,
download manager, or backup tool). For finer control, **Add title rule…** excludes any window whose
title contains a given substring. Opting out an app that's already frozen thaws it immediately.
Choices are saved to `%APPDATA%\Offstage\optout.json` and persist across restarts. These sit on
top of a built-in list of system/shell processes that are never frozen regardless.

**Settings (tray menu):**

- **Freeze delay** — how long an app must sit off-screen before it's frozen (2 s – 1 min presets).
- **Run at login** — adds/removes a per-user startup entry so Offstage launches when you sign in.
- Auto-freeze on/off, the freeze delay, and the hotkeys all persist to `%APPDATA%\Offstage\settings.json`.
  Hotkeys aren't rebindable in the UI yet — edit `FreezeHotkey` / `ThawHotkey` in that file (e.g.
  `"Ctrl+Shift+F9"`) and restart. At least one modifier plus a key (A–Z, 0–9, or F1–F24) is required.

## Build & run

```powershell
dotnet build -c Release
dotnet run -c Release      # or launch bin\Release\net9.0-windows\Offstage.exe
```

Requires the .NET 9 SDK (Windows).

## How it works (v1)

- **Which windows are where** — the public, documented `IVirtualDesktopManager` COM interface
  (`IsWindowOnCurrentVirtualDesktop`). Deliberately avoids the undocumented
  `IVirtualDesktopManagerInternal`, which changes between Windows builds.
- **Auto-freeze** — a ~500 ms reconcile loop, not switch-detection. Each pass thaws any frozen app
  whose window is back on the current desktop, and freezes any eligible app that has stayed fully off
  the current desktop for the grace delay. It relies only on the direct `IsWindowOnCurrentVirtualDesktop`
  query, which works even for suspended windows — so it never depends on an app's window staying
  responsive.
- **Freezing** — walks each target app's *whole process tree* (via a Toolhelp snapshot) and calls
  `NtSuspendProcess` on every process, then `EmptyWorkingSet` to trim resident pages. Walking the
  tree matters: Chrome/Edge/Electron apps are a main process plus renderer/GPU/network children.
- **Safety** — a process is frozen only if it has app windows and *none* are on the current desktop;
  system/shell processes (explorer, dwm, ApplicationFrameHost, …) are never touched; every freeze is
  reversible and exit always thaws.
- **Crash recovery** — the set of frozen processes is persisted to `%APPDATA%\Offstage\session.json`.
  If Offstage is killed without a clean exit (Ctrl+C, closing the terminal, End Task) it leaves apps
  suspended; the next launch reads that file and resumes the orphans, re-verifying each by process
  name and start time so a reused PID is never resumed by mistake.

## Known limits / honest caveats

- **RAM is only partially reclaimed.** `EmptyWorkingSet` pushes pages to the standby list / pagefile;
  Windows does similar under pressure and pages them back on resume. The clean, reliable win here is
  **CPU dropping to ~0%** for frozen apps. Don't expect RAM to vanish.
- **Suspended network apps drop connections.** A frozen Slack/Discord/browser will reconnect on thaw.
- **You can't open a new window of a frozen single-instance app.** Edge and Chrome run one "broker"
  (main) process that owns *every* window across all desktops; launching `msedge.exe`/`chrome.exe`
  again just sends that broker an IPC "open a window" message and exits. If Offstage has frozen the
  browser, the broker is suspended and can't receive the message, so no window appears — even on your
  current desktop. The reconcile loop can't help, because it only thaws an app once one of its windows
  lands on the current desktop, and here the window never gets created (a chicken-and-egg deadlock).
  Workarounds today: thaw first (`Ctrl+Alt+R`), add the browser to **Never freeze…**, or run a second
  copy under a separate `--user-data-dir` (its own independent broker). See the roadmap below for a
  planned proper fix.
- **Antivirus may flag the build.** `NtSuspendProcess` + `EmptyWorkingSet` + process enumeration is a
  malware-like fingerprint. For personal use, add this folder as a Defender exclusion.

## Roadmap (not built yet)

- A custom tray icon and an in-UI hotkey rebinding dialog.
- **"Leave the broker alive" freeze mode for single-instance apps (Edge/Chrome/Electron).** Instead of
  suspending the *whole* tree, suspend only the **children** (renderer, GPU and utility processes —
  where nearly all the background CPU actually burns) and leave the root/broker process running. The
  broker stays responsive enough to accept a new-window request, which fixes the deadlock above, while
  you still get the bulk of the CPU savings. Since Offstage already resolves the full process tree and
  records exactly which PIDs it froze, this is mostly: mark known-broker apps by process name, skip the
  root PID when suspending them, and resume the same recorded child set on thaw. Caveats to validate
  first — Chromium's renderer-hang watchdog may flag suspended renderers as "unresponsive" and try to
  reload/kill them, so this needs testing before it's promoted from experimental.
- Optional close-and-restore adapters for specific apps (much more fragile — deferred on purpose).
