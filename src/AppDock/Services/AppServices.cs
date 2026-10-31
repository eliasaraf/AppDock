using System;
using AppDock.Core.Docking;
using AppDock.Core.Settings;

namespace AppDock.Services;

/// <summary>What windows need from the app: the dock manager, settings, and app-level actions.</summary>
internal sealed record AppServices(
    DockManager Manager,
    SettingsStore Store,
    AppCommands Commands,
    Action ApplyHotkey,
    Action<bool> SuspendHotkey,
    Action ResetSettings);
