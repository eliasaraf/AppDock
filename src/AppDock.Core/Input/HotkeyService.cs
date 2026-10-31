using AppDock.Core.Interop;
using AppDock.Core.Settings;
using AppDock.Core.Windows;
using Serilog;

namespace AppDock.Core.Input;

/// <summary>
/// The global show/hide shortcut: either a double tap of a modifier (low-level hook) or a classic key chord
/// (RegisterHotKey). <see cref="Triggered"/> is raised on the thread that owns <paramref name="ui"/>.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int ChordId = 0xAD01;
    private readonly MessageWindow _ui;
    private readonly ILogger _log;
    private readonly DoubleTapDetector _detector = new();
    private LowLevelInputHook? _hook;
    private bool _chordRegistered;
    private volatile TapKey _tapKey; // read on the hook thread

    public HotkeyService(MessageWindow ui, ILogger log)
    {
        _ui = ui;
        _log = log.ForContext<HotkeyService>();
        _ui.Message += OnMessage;
    }

    public event Action? Triggered;

    /// <summary>Raised when the configured chord could not be registered (already taken by another app).</summary>
    public event Action<string>? RegistrationFailed;

    public bool Paused { get; set; }

    public void Configure(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Mode == HotkeyMode.DoubleTap && _hook is not null)
        {
            // Hook already running: just retune it (e.g. while the user drags the speed slider).
            _tapKey = settings.TapKey;
            _detector.IntervalMs = settings.DoubleTapIntervalMs;
            _detector.Reset();
            return;
        }

        Teardown();
        switch (settings.Mode)
        {
            case HotkeyMode.DoubleTap:
                _tapKey = settings.TapKey;
                _detector.IntervalMs = settings.DoubleTapIntervalMs;
                _detector.Reset();
                _hook = new LowLevelInputHook(OnKey, OnMouse);
                if (!_hook.IsInstalled)
                {
                    _log.Error("Low-level input hook could not be installed");
                    RegistrationFailed?.Invoke("The double-tap shortcut could not be enabled.");
                }

                break;

            case HotkeyMode.Chord:
                _chordRegistered = Native.RegisterHotKey(_ui.Handle, ChordId,
                    settings.ChordModifiers | Native.MOD_NOREPEAT, (uint)settings.ChordKey);
                if (!_chordRegistered)
                {
                    _log.Warning("Hotkey {Mods}+{Key} is already in use", settings.ChordModifiers, settings.ChordKey);
                    RegistrationFailed?.Invoke(
                        $"The shortcut {HotkeyText.Describe(settings)} is already used by another app.");
                }

                break;
        }

        _log.Information("Shortcut configured: {Shortcut}", HotkeyText.Describe(settings));
    }

    public void Dispose()
    {
        Teardown();
        _ui.Message -= OnMessage;
    }

    private void Teardown()
    {
        _hook?.Dispose();
        _hook = null;
        if (_chordRegistered)
        {
            Native.UnregisterHotKey(_ui.Handle, ChordId);
            _chordRegistered = false;
        }
    }

    // Runs on the hook thread: keep it minimal.
    private bool OnKey(KeyEvent e)
    {
        var isTap = IsTapKey(e.VirtualKey, _tapKey);
        var now = Environment.TickCount64;
        if (e.IsDown)
        {
            _detector.KeyDown(isTap, now);
        }
        else if (_detector.KeyUp(isTap, now) && !Paused)
        {
            _ui.Post(() => Triggered?.Invoke());
        }

        return false;
    }

    private bool OnMouse(MouseEvent e)
    {
        if (e.Message is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN)
        {
            _detector.MouseButton();
        }

        return false;
    }

    private bool OnMessage(uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (msg == Native.WM_HOTKEY && (int)wParam == ChordId)
        {
            if (!Paused)
            {
                Triggered?.Invoke();
            }

            return true;
        }

        return false;
    }

    private static bool IsTapKey(int vk, TapKey key) => key switch
    {
        TapKey.Ctrl => vk is Native.VK_LCONTROL or Native.VK_RCONTROL or Native.VK_CONTROL,
        TapKey.Shift => vk is Native.VK_LSHIFT or Native.VK_RSHIFT or Native.VK_SHIFT,
        TapKey.Alt => vk is Native.VK_LMENU or Native.VK_RMENU or Native.VK_MENU,
        _ => false,
    };
}

public static class HotkeyText
{
    public static string Describe(HotkeySettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Mode switch
        {
            HotkeyMode.DoubleTap => $"Double-tap {s.TapKey}",
            HotkeyMode.Chord => Chord(s.ChordModifiers, s.ChordKey),
            _ => "None",
        };
    }

    public static string Chord(uint modifiers, int vk)
    {
        var parts = new List<string>();
        if ((modifiers & Native.MOD_CONTROL) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & Native.MOD_ALT) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & Native.MOD_SHIFT) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & Native.MOD_WIN) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }

    public static string KeyName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x20 => "Space",
        0xC0 => "`",
        0xDC => "\\",
        0xBA => ";",
        0xDE => "'",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xDB => "[",
        0xDD => "]",
        0xBD => "-",
        0xBB => "=",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x13 => "Pause",
        _ => $"Key {vk:X2}",
    };
}
