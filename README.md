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
- **Keep browser broker alive (experimental)** — see below. Off by default.
- **Run at login** — adds/removes a per-user startup entry so Offstage launches when you sign in.
- Auto-freeze on/off, the freeze delay, keep-broker-alive, and the hotkeys all persist to
  `%APPDATA%\Offstage\settings.json`. Hotkeys aren't rebindable in the UI yet — edit `FreezeHotkey` /
  `ThawHotkey` in that file (e.g. `"Ctrl+Shift+F9"`) and restart. At least one modifier plus a key
  (A–Z, 0–9, or F1–F24) is required.

**Keep browser broker alive (experimental):** Chromium/Electron apps (Edge, Chrome, Brave, Slack,
Discord, VS Code, …) run one main "broker" process that owns every window across all desktops, plus a
pile of renderer/GPU/utility children where nearly all the background CPU actually burns. With this
setting **on**, Offstage suspends only those children and leaves the broker running, so:

- You can still **open a new window** of a frozen browser on your current desktop — the broker is alive
  to service the request, and the new window's renderer is never frozen.
- The **background tabs stay frozen** even while that new window is open. Offstage remembers which
  windows the app owned at freeze time; it only thaws the frozen workers when *those* windows come back
  to the current desktop (i.e. you actually switch to their desktop), not when you open a fresh window.

Detection is automatic and needs **no per-app list**: an app is treated as a broker when one of its
child processes shares its executable name and carries a Chromium `--type=` switch (renderer,
gpu-process, utility, …). Non-Chromium apps are unaffected and still freeze whole-tree.

Caveat to watch: Chromium has a renderer-hang watchdog, so with renderers suspended but the broker
alive it *may* occasionally show "Page unresponsive" or reload a background tab. If an app misbehaves,
add it to **Never freeze…** and leave this mode for the rest.

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
- **Opening a new window of a frozen single-instance app** (default mode). Edge and Chrome run one
  "broker" (main) process that owns *every* window across all desktops; launching `msedge.exe`/
  `chrome.exe` again just sends that broker an IPC "open a window" message and exits. In the default
  whole-tree freeze the broker is suspended and can't receive the message, so no window appears — even
  on your current desktop. Turn on **Keep browser broker alive** (below) to fix this; it's the
  purpose-built mode for exactly this case.
- **Antivirus may flag the build.** `NtSuspendProcess` + `EmptyWorkingSet` + process enumeration is a
  malware-like fingerprint. For personal use, add this folder as a Defender exclusion.

## Roadmap (not built yet)

- A custom tray icon and an in-UI hotkey rebinding dialog.
- **Promote "Keep browser broker alive" from experimental to default** once it's had more real-world
  mileage — see the caveats in its section above.
- Optional close-and-restore adapters for specific apps (much more fragile — deferred on purpose).
