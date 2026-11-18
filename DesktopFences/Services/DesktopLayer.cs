using System;
using DesktopFences.Interop;

namespace DesktopFences.Services;

/// <summary>
/// Tracks whether the desktop is "shown" (Win+D / Show desktop). Normally fences sit at the bottom
/// of the z-order just above the wallpaper; while the desktop is shown they're raised so they
/// don't vanish along with every other window.
/// </summary>
public sealed class DesktopLayer : IDisposable
{
    readonly Native.WinEventProc _proc;
    readonly IntPtr _hook;
    readonly uint _pid = (uint)Environment.ProcessId;

    public bool DesktopShown { get; private set; }
    public event Action? Changed;

    public DesktopLayer()
    {
        _proc = OnForegroundChanged;
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc,
            0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
    }

    void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (IsDesktop(hwnd))
            {
                // "Show desktop" raises the desktop window above everything else, including us.
                // A plain click on the desktop leaves it at the bottom, so nothing changes then.
                CheckRaised(hwnd);
                var recheck = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                recheck.Tick += (_, _) => { recheck.Stop(); if (Native.GetForegroundWindow() == hwnd) CheckRaised(hwnd); };
                recheck.Start();
                return;
            }
            else
            {
                // Taskbar, Start menu, tray flyouts etc. don't end "show desktop".
                var cls = Native.ClassOf(hwnd);
                long ex = Native.ExStyle(hwnd);
                if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
                    || (ex & (Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW)) != 0)
                    return;
                DesktopShown = false;
            }
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("Foreground hook", e);
        }
    }

    static bool IsDesktop(IntPtr h)
    {
        var cls = Native.ClassOf(h);
        return cls is "WorkerW" or "Progman";
    }

    void CheckRaised(IntPtr desktop)
    {
        if (!OurWindowBelow(desktop)) return;
        DesktopShown = true;
        Changed?.Invoke();
    }

    bool OurWindowBelow(IntPtr desktop)
    {
        for (var w = Native.GetWindow(desktop, Native.GW_HWNDNEXT); w != IntPtr.Zero; w = Native.GetWindow(w, Native.GW_HWNDNEXT))
        {
            if (!Native.IsWindowVisible(w)) continue;
            Native.GetWindowThreadProcessId(w, out var pid);
            if (pid == _pid) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
    }
}
