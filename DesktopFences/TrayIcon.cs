using System;
using System.Linq;
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
        var peek = new WF.ToolStripMenuItem("Bring fences to front", null, (_, _) => _mgr.TogglePeek());
        menu.Items.Add(peek);
        var roll = new WF.ToolStripMenuItem("Roll up all fences", null, (_, _) => _mgr.RollUpAll(_mgr.AnyUnrolled));
        menu.Items.Add(roll);
        var lockAll = new WF.ToolStripMenuItem("Lock all fences", null, (_, _) => _mgr.LockAll(!_mgr.AllLocked));
        menu.Items.Add(lockAll);
        menu.Items.Add("Move all fences to centre of screen", null, (_, _) => _mgr.CentreAllFences());
        var layouts = new WF.ToolStripMenuItem("Layouts");
        layouts.DropDownItems.Add("(placeholder)"); // filled when the menu opens
        menu.Items.Add(layouts);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => _mgr.ShowSettings());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Put icons back on desktop && quit", null, (_, _) => _mgr.RestoreIconsAndQuit());
        menu.Items.Add("Quit", null, (_, _) => _mgr.Quit());
        menu.Opening += (_, _) =>
        {
            bool keys = _mgr.Config.Settings.HotkeysEnabled;
            toggle.Text = _mgr.FencesVisible ? "Hide fences" : "Show fences";
            toggle.ShortcutKeyDisplayString = keys ? "Ctrl+Alt+H" : "";
            peek.Text = _mgr.Peeking ? "Send fences back to the desktop" : "Bring fences to front";
            peek.ShortcutKeyDisplayString = keys ? "Ctrl+Alt+F" : "";
            roll.Text = _mgr.AnyUnrolled ? "Roll up all fences" : "Unroll all fences";
            lockAll.Checked = _mgr.AllLocked;
            BuildLayoutsMenu(layouts);
        };

        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) _mgr.ToggleVisibility();
        };
    }

    void BuildLayoutsMenu(WF.ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        parent.DropDownItems.Add("Save current layout…", null, (_, _) => _mgr.SaveLayoutInteractive());
        var saved = _mgr.Config.Layouts.Where(l => !l.Automatic).ToList();
        var auto = _mgr.Config.Layouts.Where(l => l.Automatic).OrderByDescending(l => l.Saved).ToList();
        WF.ToolStripMenuItem Restore(Models.LayoutSnapshot snap, string text) =>
            new(text, null, (_, _) => _mgr.RestoreLayout(snap)) { ToolTipText = $"Saved {snap.Saved:g} – {snap.Fences.Count} fence(s)" };

        if (saved.Count > 0)
        {
            parent.DropDownItems.Add(new WF.ToolStripSeparator());
            foreach (var snap in saved) parent.DropDownItems.Add(Restore(snap, $"Restore \"{snap.Name}\""));
        }
        if (auto.Count > 0)
        {
            var daily = new WF.ToolStripMenuItem("Daily backups");
            foreach (var snap in auto) daily.DropDownItems.Add(Restore(snap, $"Restore {snap.Saved:dddd d MMM}"));
            parent.DropDownItems.Add(new WF.ToolStripSeparator());
            parent.DropDownItems.Add(daily);
        }
        if (saved.Count > 0)
        {
            var delete = new WF.ToolStripMenuItem("Delete a layout");
            foreach (var snap in saved)
                delete.DropDownItems.Add(snap.Name, null, (_, _) => _mgr.DeleteLayout(snap));
            parent.DropDownItems.Add(delete);
        }
        parent.DropDownItems.Add(new WF.ToolStripSeparator());
        parent.DropDownItems.Add("Swap monitors", null, (_, _) => _mgr.SwapMonitors());
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
