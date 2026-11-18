using System;
using System.Windows;
using DesktopFences.Dialogs;
using DesktopFences.Services;
using WF = System.Windows.Forms;

namespace DesktopFences;

public sealed class TrayIcon : IDisposable
{
    readonly WF.NotifyIcon _icon;
    readonly FenceManager _mgr;

    public TrayIcon(FenceManager mgr)
    {
        _mgr = mgr;
        _icon = new WF.NotifyIcon
        {
            Text = "Desktop Fences",
            Icon = LoadIcon(),
            Visible = true,
        };

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("New fence", null, (_, _) => _mgr.CreateFence("New Fence", startRename: true));
        menu.Items.Add("New folder portal…", null, (_, _) => _mgr.CreatePortalInteractive());
        menu.Items.Add("Auto-organise desktop…", null, (_, _) => _mgr.AutoOrganize(confirm: true));
        menu.Items.Add(new WF.ToolStripSeparator());
        var toggle = new WF.ToolStripMenuItem("Hide fences", null, (_, _) => _mgr.ToggleVisibility());
        menu.Items.Add(toggle);
        menu.Items.Add("Settings…", null, (_, _) => new SettingsWindow(_mgr).ShowDialog());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Put icons back on desktop && quit", null, (_, _) => _mgr.RestoreIconsAndQuit());
        menu.Items.Add("Quit", null, (_, _) => _mgr.Quit());
        menu.Opening += (_, _) => toggle.Text = _mgr.FencesVisible ? "Hide fences" : "Show fences";

        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) _mgr.ToggleVisibility();
        };
    }

    static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res != null)
                using (var s = res.Stream)
                    return new System.Drawing.Icon(s, WF.SystemInformation.SmallIconSize);
        }
        catch (Exception ex) { Log.Error("Tray icon", ex); }
        return System.Drawing.SystemIcons.Application;
    }

    public void Balloon(string title, string text) =>
        _icon.ShowBalloonTip(6000, title, text, WF.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
