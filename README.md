# AppDock

Dock any Windows application to a screen edge like the taskbar. Built for keeping one important app (by default
**Claude Desktop**) always at hand:

- **Docks like the taskbar.** The app gets a reserved strip of the screen; other windows, even maximized ones,
  resize around it instead of covering it.
- **Drag it anywhere.** Drop it on any edge of any monitor to dock it there (an accent-coloured preview shows
  where), or in the middle to float it. Drag its inner edge to resize; the size is remembered per edge.
- **Double-tap Ctrl** to hide or show it. Hiding (or minimizing) frees the space; showing slides it back in.
- Smooth, display-synced animations, Windows 11 look (light/dark), multi-monitor and per-monitor DPI aware.

## Install

Run `AppDock-<version>-x64.msi`. It installs per user (no administrator rights) to
`%LOCALAPPDATA%\Programs\AppDock`, adds a Start menu shortcut and starts AppDock. The download is small
because AppDock uses the shared .NET 10 Desktop Runtime; if a machine does not have it, Windows offers
Microsoft's download the first time AppDock starts (one time, about 55 MB). Upgrades install over older versions and keep settings; uninstalling stops AppDock,
releases any reserved screen space and removes the autostart entry.

Requirements: Windows 10 (2004+) or Windows 11, x64, and the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).

## Use

AppDock lives in the notification area.

| Action | How |
| --- | --- |
| Show / hide the docked app | Double-tap **Ctrl** (configurable), or left-click the tray icon |
| Dock to another edge | Drag the app's title bar to that edge, or tray menu > Dock to |
| Float (undock) | Drag it away from the edge, or tray menu > Dock to > Floating |
| Resize | Drag the app's inner edge |
| Dock a different app | Tray menu > Choose app window..., then click the window (Esc cancels) |
| Settings | Tray menu > Settings, or start AppDock again while it runs |

Command line: `--reset` (restore default settings), `--verbose` (debug logging), `--shutdown` (ask the running
instance to exit), `--uninstall` (exit and remove autostart; used by the installer).

Files: settings `%APPDATA%\AppDock\settings.json`, logs `%LOCALAPPDATA%\AppDock\logs` (7 days kept).

## How it works

AppDock does not re-parent or inject into the target app (that breaks Electron and many others). It registers an
invisible shell **AppBar** (`SHAppBarMessage`), the same mechanism the taskbar uses, and positions the real app
window into the reserved strip, compensating for the invisible DWM borders. WinEvent hooks follow the window
(drag, resize, minimize, show/hide, close), so AppDock reacts to the user and to the app itself, re-acquires the
app when it restarts, and steps behind full-screen apps like the taskbar does.

While you drag the docked app, Windows Snap is paused for that drag only (it would otherwise snap the window to
half the screen at the edge). The change is never written to your profile and is restored right after the drop.

Robustness: space is released whenever the app is hidden, minimized, closed, or AppDock exits or crashes. If
AppDock is killed while the app is hidden, the next start shows it again. Explorer restarts, display/DPI changes,
monitor unplugging, sleep/resume and session unlock are handled. Apps running as administrator cannot be moved by
a normal process; AppDock says so instead of failing silently.

## Develop

```
dotnet build AppDock.slnx
dotnet test tests/AppDock.Core.Tests
```

| Project | Purpose |
| --- | --- |
| `src/AppDock.Core` | All logic, no UI: Win32 interop, AppBar, state machine, session, animator, hotkeys, settings |
| `src/AppDock` | WPF tray app (Fluent theme): tray menu, settings, onboarding, snap preview, crash handling |
| `tests/AppDock.Core.Tests` | Unit tests: geometry, state machine (incl. randomized model check), double-tap, settings |
| `tests/AppDock.IntegrationTests` | End-to-end tests against real windows and the real shell (opt-in, see below) |
| `tools/AppDock.TestTarget` | Stand-in target window for development and QA (`--frameless` mimics Electron) |
| `installer` | WiX v5 per-user MSI |

The core is a pure reducer (`DockReducer`: state + event -> state + ordered effects) executed by `DockSession`.
Effect order is deliberate: the window moves into place before space is reserved and leaves before it is released,
so other windows reflow once and nothing flickers.

**Integration tests** take over part of the screen and move the mouse for ~20 seconds:

```
$env:APPDOCK_INTEGRATION = '1'; dotnet test tests/AppDock.IntegrationTests
```

Debug builds include an "AppDock Test Target" profile; run `tools/AppDock.TestTarget` and pick it in Settings.

## Release

```
.\build\build.ps1 [-Version 1.1.0] [-Integration] [-CertificateThumbprint <sha1>]
```

Builds with warnings as errors, runs the tests, publishes the single-file exe, signs it (when a code-signing
certificate thumbprint is given) and builds `artifacts\AppDock-<version>-x64.msi`. Then publish it:

```
gh release create v<version> artifacts\AppDock-<version>-x64.msi --title "AppDock <version>" --notes-file CHANGELOG.md
```

Running AppDocks check `https://api.github.com/repos/eliasaraf/AppDock/releases/latest` (setting `updateFeedUrl`)
and show a notification that opens the release page when a newer version exists.

### Code signing with a self-made certificate

Unsigned builds work but Windows warns on first run. Without a purchased certificate you can use your own,
trusted only on machines where you install it:

```
.\build\new-signing-cert.ps1                    # once, on the build PC: creates the cert, prints its thumbprint
.\build\build.ps1 -CertificateThumbprint <thumbprint>
```

On each target machine, as administrator: `.\install-certificate.ps1 AppDock-signing.cer` (both files are in
`artifacts\signing`). The private key stays in your user certificate store on the build PC; never commit it.
SmartScreen may still warn for downloaded files until they gain reputation.

## License

MIT, see [LICENSE.txt](LICENSE.txt). Free for any use; provided as is, without warranty or liability.

## Known limitations

- Apps running as administrator need AppDock to run as administrator too (Windows UIPI).
- One docked app at a time (the architecture supports several sessions; the UI exposes one).
- Some apps enforce a minimum width; AppDock adopts it rather than fighting the app.
