using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;
using DesktopFences.Interop;

namespace DesktopFences.Services;

/// <summary>Detects a double-click on an empty part of the desktop (not on an icon).</summary>
public sealed class DesktopDoubleClick : IDisposable
{
    readonly Native.LowLevelMouseProc _proc;
    readonly IntPtr _hook;
    readonly Dispatcher _ui;
    uint _lastTime;
    Native.POINT _lastPt;
    bool _armed;

    public event Action? DoubleClicked;

    public DesktopDoubleClick(Dispatcher ui)
    {
        _ui = ui;
        _proc = HookProc;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Info("Mouse hook could not be installed; desktop double-click disabled.");
    }

    IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam == Native.WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            uint dt = info.time - _lastTime;
            bool near = Math.Abs(info.pt.X - _lastPt.X) <= Native.GetSystemMetrics(Native.SM_CXDOUBLECLK) / 2 + 1
                     && Math.Abs(info.pt.Y - _lastPt.Y) <= Native.GetSystemMetrics(Native.SM_CYDOUBLECLK) / 2 + 1;
            if (_armed && near && dt <= Native.GetDoubleClickTime())
            {
                _armed = false;
                var pt = info.pt;
                _ui.InvokeAsync(() => Check(pt));
            }
            else
            {
                _armed = true;
                _lastTime = info.time;
                _lastPt = info.pt;
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    void Check(Native.POINT pt)
    {
        if (!Native.IsDesktopListView(Native.WindowFromPoint(pt))) return;
        Task.Run(() =>
        {
            bool onIcon = false;
            try
            {
                var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
                for (int i = 0; el != null && i < 4; i++)
                {
                    var type = el.Current.ControlType;
                    if (type == ControlType.ListItem) { onIcon = true; break; }
                    if (type == ControlType.List || type == ControlType.Pane) break;
                    el = TreeWalker.ControlViewWalker.GetParent(el);
                }
            }
            catch { /* UIA unavailable: assume empty space */ }
            if (!onIcon) _ui.InvokeAsync(() => DoubleClicked?.Invoke());
        });
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
    }
}
