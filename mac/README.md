# Claude Code Indicator for macOS

A menu-bar version of the Windows tray app. It has the same menu, the same hooks and the same
ESP32 support. All the logic is shared with Windows in `../core`. This folder only holds the
macOS parts:

| File | What it does |
|---|---|
| `Program.cs`, `App.cs` | Avalonia startup without a Dock icon. Single instance. Adds Homebrew/`~/.local/bin` to PATH (apps started from Finder don't get your shell PATH). |
| `MenuBar.cs` | The menu-bar item and its menu, rendered from `IndicatorController`. |
| `DotIcons.cs` | Draws the colored dot, or uses `done.png` / `working.png` / `waiting.png` if you add them. |
| `UnixPtyTerminal.cs` | The pseudo terminal for the hidden `claude` session that reads `/usage` (`openpty` + `posix_spawn`). |
| `MacServices.cs` | Chimes via `afplay`, "Start at login" as a LaunchAgent, notifications via `osascript`. |
| `Dialogs.cs` | The ESP32 address prompt and error messages. |
| `build-app.sh`, `Info.plist` | Builds `Claude Code Indicator.app`. |

## Build and install

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) on the Mac.

```sh
cd mac
./build-app.sh                 # Apple Silicon; use osx-x64 for an Intel Mac
mv "bin/app/Claude Code Indicator.app" /Applications/
open "/Applications/Claude Code Indicator.app"
```

By default the app uses the installed .NET 10 runtime. Add `--self-contained` to bundle the
runtime instead (`./build-app.sh osx-arm64 --self-contained`).

Move the app to `/Applications` **before** you first open it. The first launch from the bundle
turns on "Start at login", which saves the app's path in
`~/Library/LaunchAgents/com.claudecodeindicator.agent.plist`. If you move the app later, turn
the menu item off and on again.

For development, `dotnet run --project mac` works too. It doesn't turn on "Start at login".

The app isn't signed. Because you built it yourself, macOS doesn't quarantine it, so it opens
without a Gatekeeper prompt. If you copy it to another Mac, run
`xattr -dr com.apple.quarantine "Claude Code Indicator.app"` there.

## Where things are

- Settings: `~/Library/Application Support/ClaudeCodeIndicator/settings.json`
- Hooks: the same `~/.claude/settings.json` entries as on Windows. Use the menu item
  "Auto-configure Claude Code hooks".
- The hidden `/usage` session runs in `~/Library/Application Support/ClaudeCodeIndicator/usage-probe`.

## Differences from Windows

- No "Listening on…" notification at startup. The hooks toggle shows a macOS notification
  instead of a balloon. The first time, that notification may be attributed to "Script Editor".
- macOS truncates the tooltip less, so you can see all three lines.
- Port errors don't suggest `netsh`. On macOS a busy port 8787 means another program is
  using it.
