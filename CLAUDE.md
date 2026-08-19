# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

The project has been created from the build spec and **compiles** (Debug + Release both build
clean). It lives in the `windows/` subfolder:

- `windows/ClaudeCodeIndicator.csproj` — `net9.0-windows`, WinForms, nullable enabled.
- `windows/Program.cs` — the full app (~570 lines): `Program`, `TrayContext`, `AppSettings`,
  `IpInputForm`.

No `.ico` files have been added yet, so the app falls back to runtime-drawn colored dots. ESP32
firmware exists in `firmware/` (the ESP integration is optional). Note: the color palette has
diverged from the spec by user request — the code is authoritative for colors (see table below).

`claude-code-indicator-build 1.md` remains the canonical spec — a complete, verbatim build handoff
with the full source plus build/run/hook-setup instructions. When the code and the spec disagree,
treat the spec as the source of intent unless the user says otherwise.

## What this is

A **Windows-only** system-tray app (`net9.0-windows` + WinForms) that surfaces Claude Code's live
state as a colored notification-area icon, and optionally mirrors that color to an ESP32 RGB
indicator over HTTP. **It cannot be built or run on Linux/macOS.**

## Build & run

Build/run commands target the `windows/` project (run from there, or pass the path):

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
  `TrayContext._state`; `ApplyState` updates the icon, tooltip, menu text, and pushes to the ESP.
- **Self-managing hooks.** The app reads and edits `~/.claude/settings.json` itself
  (`%USERPROFILE%\.claude\settings.json`). Edits are **surgical and idempotent**: install removes
  its own hooks first, then re-adds them; a hook is recognized as "ours" purely by whether its
  handler URL points at `localhost:8787` / `127.0.0.1:8787` (`IsMyHandler`). A `settings.json.bak`
  is written before every change. If the file is JSONC/invalid, the app warns and changes nothing.
- **ESP32 push is fire-and-forget**, 2 s timeout: `GET http://<esp>/state?value=<state>&rgb=<hex>`.
  An absent/offline ESP is silently ignored. The ESP is entirely optional. Firmware lives in
  `firmware/` — `ClaudeCodeIndicatorMatrix/` (ESP32-S3-Matrix 8x8, renders the Claude mascot)
  and `ClaudeCodeIndicator/` (single-LED variant).
- **Waiting chime.** Entering the Waiting (red) state plays `.sounds\pop.mp3` from next to the
  exe via `winmm`'s `mciSendString` — `SoundPlayer` is WAV-only and WPF's `MediaPlayer` is too
  heavy a dependency for one clip. The MCI device is opened once, replayed with `play … from 0`,
  and closed in `Dispose`. It fires only on the *transition* into Waiting, so a repeated
  `Notification` hook doesn't double-pop. A missing file or an MCI failure sets
  `_soundUnavailable` and is silent forever after. Toggle via the tray menu
  ("Play sound when waiting", persisted as `SoundMuted`). The csproj copies `.sounds\**` to
  the output directory.

## Key implementation constraints

These are easy to break and intentional:

- **Single instance** via named mutex `ClaudeCodeIndicator_SingleInstance` — prevents autostart +
  manual launch from double-binding port 8787. A second instance exits immediately.
- **UI-thread marshalling.** The HTTP listener runs on a background thread; all NotifyIcon updates
  must be marshalled through the hidden `_marshal` control (`RequestState` → `BeginInvoke` →
  `ApplyState`). Don't touch WinForms objects directly from the listener loop.
- **GDI handle ownership.** When `<state>.ico` files are absent, icons are drawn at runtime
  (`MakeCircleIcon`); those `HICON` handles are tracked in `_ownedHandles` and freed with
  `DestroyIcon` in `Dispose`. Any new generated icon must be tracked the same way or it leaks.
- **Listener port 8787** is a `const Port`. It is hardcoded into the hook URLs the app writes, so
  changing it must stay consistent across the listener prefixes, `HookSpec`, and `IsMyHandler`.
- Icons load from `<state>.ico` next to the exe if present, else fall back to a drawn colored dot —
  the icon is never blank.

## Color / state reference

| Hook event | Endpoint | State | Color | Matrix effect |
|---|---|---|---|---|
| `UserPromptSubmit`, `PreToolUse` | `/working` | Working | `#D37355` Claude orange | solid, eyes blink |
| `Notification` (`permission_prompt\|idle_prompt`) | `/waiting` | Waiting | `#E53935` red | whole mascot blinks |
| `Stop` | `/done` | Done | `#43A047` green | solid |

Initial state is Done/green. `PreToolUse` re-arms red after a permission prompt (yellow) is approved.
