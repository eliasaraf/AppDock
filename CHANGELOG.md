# Changelog

## 1.0.10 - 2026-11-02

- Updates within the same major version now download and install silently, when the PC is idle or at the next start.
  New major versions still show a notification. Versions before this one have to install it by hand.

## 1.0.6 - 2026-11-02

- Every build gets its own version number (`1.0.<build number>`).
- Builds are signed automatically with a self-signed development certificate.
- Installer no longer stalls on "Validating install" when upgrading.
- Resizing a docked app by its inner edge no longer undocks it when the first mouse movement is tiny.
- Showing or docking no longer makes the app slide out of its strip and jump back: Windows' push of the window
  into the work area is undone before it is drawn, and slide-in reserves the strip while the app is still off-screen.

## 1.0.1 - 2026-10-31

- Installer is now about 2 MB instead of 60 MB: AppDock uses the shared .NET 10 Desktop Runtime. Machines without
  it get Windows' prompt to download it from Microsoft on first launch.

## 1.0.0 - 2026-10-31

First release.

- Dock any app to the left, right, top or bottom edge of any monitor with reserved screen space (shell AppBar).
- Drag to an edge to dock (with snap preview), drag away to float, drag the inner edge to resize.
- Double-tap Ctrl (or Shift/Alt, or a key combination) to hide/show; hiding and minimizing free the space.
- Smooth vsync-paced slide and dock animations; respects the Windows "Animation effects" setting.
- Built-in Claude Desktop profile; "Choose app window" picker for any other app.
- Windows 11 Fluent UI (light/dark): tray menu, settings, first-run welcome.
- Recovers from app restarts, Explorer restarts, display/DPI changes, sleep, and AppDock crashes.
- Per-user MSI installer with clean upgrade and uninstall.
- Update notifications from GitHub Releases.
- MIT licensed.
