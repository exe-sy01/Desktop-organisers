using System;
using System.Collections.Generic;
using System.Windows.Interop;
using DesktopFences.Interop;

namespace DesktopFences.Services;

/// <summary>System-wide hotkeys, received by a hidden message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    readonly HwndSource _src;
    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 1;

    public HotkeyService()
    {
        var p = new HwndSourceParameters("DesktopFencesHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _src = new HwndSource(p);
        _src.AddHook(WndProc);
    }

    /// <summary>Returns false if another app already owns this key combination.</summary>
    public bool Register(uint modifiers, uint vk, Action action)
    {
        int id = _nextId++;
        if (!Native.RegisterHotKey(_src.Handle, id, modifiers | Native.MOD_NOREPEAT, vk))
        {
            Log.Info($"Hotkey {modifiers:X}+{vk:X} is taken by another app");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_src.Handle, id);
        _actions.Clear();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { action(); } catch (Exception ex) { Log.Error("Hotkey", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _src.Dispose();
    }
}
