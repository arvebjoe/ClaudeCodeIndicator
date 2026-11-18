# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

The project has been created from the build spec and **compiles** (Debug + Release both build
clean). It is split into a platform-neutral core and a Windows front end, so that a macOS
front end can be added later without duplicating the logic:

- `core/ClaudeCodeIndicator.Core.csproj` — `net9.0` class library, no UI and no OS-specific
  APIs. `IndicatorController` owns the state, timers, usage/Codex reads, ESP pushes and
  settings; `HookListener` (HTTP on :8787), `ClaudeHooks` (settings.json editing),
  `EspClient`, `UsageProbe`, `CodexUsageProbe`, `WorkDays`, `AppSettings`/`SettingsStore`.
  `Platform.cs` holds the interfaces a front end implements: `ISoundPlayer` and
  `IPseudoTerminal`.
- `windows/ClaudeCodeIndicator.csproj` — `net9.0-windows`, WinForms, references the core.
  `Program.cs` (`Program`, `TrayContext`: NotifyIcon, menu, icons, autostart registry key),
  `IpInputForm.cs`, `MciSoundPlayer.cs` (`ISoundPlayer`), `ConPtyTerminal.cs`
  (`IPseudoTerminal`).

No `.ico` files have been added yet, so the app falls back to runtime-drawn colored dots. ESP32
firmware exists in `firmware/` (the ESP integration is optional). Note: the color palette has
diverged from the spec by user request — the code is authoritative for colors (see table below).

`claude-code-indicator-build 1.md` remains the canonical spec — a complete, verbatim build handoff
with the full source plus build/run/hook-setup instructions. When the code and the spec disagree,
treat the spec as the source of intent unless the user says otherwise.

## What this is

A system-tray app that surfaces Claude Code's live state as a colored notification-area icon,
and optionally mirrors that color to an ESP32 RGB indicator over HTTP. The only front end today
is Windows (`net9.0-windows` + WinForms), which **cannot be built or run on Linux/macOS**; the
`core/` library builds anywhere.

## Build & run

Build/run commands target the `windows/` project (run from there, or pass the path). It pulls
in `core/` through a project reference, so there's nothing extra to build:

```powershell
dotnet build windows\ClaudeCodeIndicator.csproj -c Release
dotnet publish windows\ClaudeCodeIndicator.csproj -c Release -r win-x64 --self-contained false -o C:\Tools\ClaudeCodeIndicator
```

Publish to a **fixed** folder and don't move it afterward: on first run the app writes its own
exe path into `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` for autostart, so relocating
the exe breaks startup. There are no tests; verification is manual (see the spec's checklist).
`PowerShell(dotnet build *)` is already allow-listed in `.claude/settings.local.json`.

## Architecture

Data flow is a single hop with one optional fan-out:

```
Claude Code hooks  --HTTP POST-->  tray app (HttpListener on :8787)  --optional GET-->  ESP32
```

- **Hooks → state.** The app maps four Claude Code hook events to three states via local HTTP
  endpoints: `UserPromptSubmit`/`PreToolUse` → `/working` (red), `Notification` matching
  `permission_prompt|idle_prompt` → `/waiting` (yellow), `Stop` → `/done` (green). Routing is by
  **URL path only** — the POST body (which carries `session_id`) is read and discarded.
- **State is latest-event-wins, globally** (not per session). The single source of truth is
  `IndicatorController.State`; `ApplyState` updates it, chimes, pushes to the ESP and raises
  `Changed`, on which `TrayContext.Render` copies the state into the icon, tooltip and menu.
- **Self-managing hooks.** The app reads and edits `~/.claude/settings.json` itself
  (`%USERPROFILE%\.claude\settings.json`). Edits are **surgical and idempotent**: install removes
  its own hooks first, then re-adds them; a hook is recognized as "ours" purely by whether its
  handler URL points at `localhost:8787` / `127.0.0.1:8787` (`IsMyHandler`). A `settings.json.bak`
  is written before every change. If the file is JSONC/invalid, the app warns and changes nothing.
- **ESP32 push is fire-and-forget**, 2 s timeout: `GET http://<esp>/state?value=<state>&rgb=<hex>`.
  An absent/offline ESP is silently ignored. The ESP is entirely optional. Firmware lives in
  `firmware/` — `ClaudeCodeIndicatorMatrix/` (ESP32-S3-Matrix 8x8, renders the Claude mascot),
  `ClaudeCodeIndicator/` (single-LED variant) and `ClaudeCodeIndicatorRound/` (ESP32-2424S012,
  ESP32-C3 + 1.28" round GC9A01 via Arduino_GFX: Claude/Codex usage half rings and animated
  Clawd + Codex avatars). The round firmware's drawing lives in `render.h`, which is pure C++
  so `tools/preview.cpp` can render frames on a PC; see its README.
- **Chimes.** Entering Waiting plays `.sounds\pop.mp3` and entering Done plays
  `.sounds\done.mp3`, from next to the exe. The core decides *when* (only on the *transition*,
  so a repeated `Notification` hook doesn't double-pop; never when `SoundMuted`) and calls
  `ISoundPlayer.Play`. On Windows that's `MciSoundPlayer`, via `winmm`'s `mciSendString` —
  `SoundPlayer` is WAV-only and WPF's `MediaPlayer` is too heavy a dependency for one clip.
  Each MCI device is opened once, replayed with `play … from 0`, and closed in `Dispose`. A
  missing file or an MCI failure marks that clip unavailable and it's silent forever after.
  Toggle via the tray menu ("Play sounds (waiting / done)", persisted as `SoundMuted`). The
  csproj copies `.sounds\**` to the output directory.

- **Usage credits (`core/UsageProbe.cs`).** `claude -p "/usage"` omits the
  `$X / $Y spent · Resets …` row, so the app keeps one interactive `claude` running in a
  hidden pseudo terminal (`IPseudoTerminal`; on Windows `ConPtyTerminal`, a ConPTY
  pseudo-console) in `%APPDATA%\ClaudeCodeIndicator\usage-probe`. 10 s after
  start, every 10 min, and on each Stop (→ idle, at most once a minute) it sends Esc, types `/usage` + Enter, strips the ANSI output and
  regex-matches the row (last match wins, after the "Refreshing…" redraw settles). The
  session runs with `--setting-sources local --strict-mcp-config --permission-mode plan`
  so user hooks (including this app's) never fire for it. It answers the first-run
  folder-trust prompt (default is "No, exit", so it arrows down to "Yes" first). The
  result goes in the tooltip and the "Usage:" menu row; failures keep the last value
  marked "(stale)". Toggle: "Read usage every 10 min" (`UsageTrackingOff`). Fragile by
  nature: a change to the `/usage` layout in a Claude Code update can break the regex.

- **Codex usage (`core/CodexUsageProbe.cs`).** Structured, no scraping: every 10 min
  (first read 15 s after start, and on "Refresh usage now") it spawns `codex app-server`
  (newline-delimited JSON-RPC over stdio) in `%APPDATA%\ClaudeCodeIndicator\codex-probe`,
  does `initialize` → `initialized`, calls `account/rateLimits/read`, then kills the process
  tree. It reads the `codex` bucket (or legacy `rateLimits`): `individualLimit`
  (workspace monthly credit limit, `used`/`limit` as strings), `credits.balance`, and the
  `primary`/`secondary` windows (`usedPercent`, 300 min = 5h, 10080 = weekly). Needs a
  ChatGPT sign-in in Codex (`~/.codex/auth.json`). On Windows it finds `codex.exe` on PATH,
  else the npm shim `codex.cmd` via `cmd.exe`; elsewhere `codex` on PATH or in the usual
  install folders (`ExecutableSearch`). Shown in the "Codex:" menu row and tooltip, and sent to
  the ESP as `&cused=<percent used>` (`GaugeUsedPercent`: credit limit if any, else the
  fuller of the 5h/weekly windows).
- **Codex state.** `/codex/working|waiting|done` on the same listener set `CodexState`,
  which is only forwarded to the ESP (`&codex=<state>`); the tray icon stays Claude-only.
  The app does not install Codex hooks — they're set up by hand (see
  `firmware/ClaudeCodeIndicatorRound/README.md`). Toggle: "Read Codex usage every 10 min" (`CodexUsageOff`). The app-server
  protocol is marked unstable upstream, so every field is treated as optional.

## Key implementation constraints

These are easy to break and intentional:

- **Single instance** via named mutex `ClaudeCodeIndicator_SingleInstance` — prevents autostart +
  manual launch from double-binding port 8787. A second instance exits immediately.
- **UI-thread marshalling.** The HTTP listener and the usage timers run on background threads.
  `IndicatorController` posts every hook event and timer tick to the `SynchronizationContext`
  it was constructed with (on Windows a `WindowsFormsSynchronizationContext`, installed in
  `Main`), so all state changes, the async reads' continuations and `Changed` run on the UI
  thread and nothing needs locking. Don't touch WinForms objects from a listener or timer
  thread, and don't add core state that's mutated off the UI context.
- **Core stays platform-neutral.** No WinForms, `System.Drawing`, registry or P/Invoke in
  `core/`; put OS-specific pieces behind an interface in `core/Platform.cs` and implement
  them in the front end.
- **GDI handle ownership.** When `<state>.ico` files are absent, icons are drawn at runtime
  (`MakeCircleIcon`); those `HICON` handles are tracked in `_ownedHandles` and freed with
  `DestroyIcon` in `Dispose`. Any new generated icon must be tracked the same way or it leaks.
- **Listener port 8787** is `HookListener.Port`. It is hardcoded into the hook URLs the app
  writes, so changing it must stay consistent across the listener prefixes, `HookSpec`, and
  `IsMyHandler` (both in `ClaudeHooks`).
- Icons load from `<state>.ico` next to the exe if present, else fall back to a drawn colored dot —
  the icon is never blank.

## Color / state reference

| Hook event | Endpoint | State | Color | Matrix effect |
|---|---|---|---|---|
| `UserPromptSubmit`, `PreToolUse` | `/working` | Working | `#D37355` Claude orange | solid, eyes blink |
| `Notification` (`permission_prompt\|idle_prompt`) | `/waiting` | Waiting | `#E53935` red | whole mascot blinks |
| `Stop` | `/done` | Done | `#43A047` green | quota gauge (solid mascot if no quota known) |

**Quota gauge.** While the tray app has a usage reading, every ESP push appends
`&spent=<usd>&limit=<usd>&allowed=<usd>` (invariant format), and it re-pushes after each read, so going idle sends the last known budget at once and the fresh one a few seconds later. In the
done state the matrix lights one LED per `limit/64` of remaining budget. Spent LEDs go
dark in reading order (top-left → right, then down), and the current one fades linearly. A push
without `spent`/`limit` (tracking off, or no reading yet) shows the mascot again.

**Daily pace.** `allowed` = `limit × work days so far (including today) / work days in the month`
(`core/WorkDays.cs`: Mon–Fri minus Norwegian public holidays, Easter included, plus Dec 24
and Dec 31; the budget resets on the 1st). LEDs that cover `allowed` are green and the rest
are Claude orange. Leftover green carries over to the next day. When spending passes the pace,
the fading LED is orange and it pulses. If `allowed` is missing, every LED is green.

Initial state is Done/green. `PreToolUse` re-arms red after a permission prompt (yellow) is approved.
