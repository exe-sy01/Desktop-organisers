using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopFences.Interop;
using DesktopFences.Models;
using DesktopFences.Services;
using Microsoft.Win32;

namespace DesktopFences;

/// <summary>Owns all fences and wires them to the desktop (hiding, watching, z-order, double-click).</summary>
public sealed class FenceManager
{
    readonly List<FenceWindow> _windows = new();
    readonly Dictionary<string, DispatcherTimer> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    DesktopWatcher? _watcher;
    DesktopDoubleClick? _doubleClick;
    HotkeyService? _hotkeys;
    DateTime _peekStarted;

    public ConfigStore Store { get; }
    public AppConfig Config => Store.Config;
    public DesktopHider Hider { get; }
    public DesktopLayer Layer { get; private set; } = null!;
    public TrayIcon? Tray { get; set; }
    public bool ShuttingDown { get; private set; }
    public bool RaisingFences { get; private set; }
    public bool FencesVisible { get; private set; } = true;
    /// <summary>Fences are temporarily in front of all windows (Ctrl+Alt+F).</summary>
    public bool Peeking { get; private set; }
    /// <summary>Hotkeys that couldn't be registered because another app owns them.</summary>
    public string HotkeyProblem { get; private set; } = "";
    public IReadOnlyList<FenceWindow> Windows => _windows;

    public FenceManager(ConfigStore store)
    {
        Store = store;
        Hider = new DesktopHider(store.Config, Dispatcher.CurrentDispatcher)
        {
            StillWanted = p => Config.Settings.HideIconsOnDesktop && FenceOf(p) != null && !_transferring.Contains(p) &&
                               (!DesktopHider.IsOnPublicDesktop(p) || Config.Settings.HidePublicDesktopIcons),
        };
        Hider.Changed += Store.SaveAtIdle;
        Hider.ElevationDeclined += () =>
            Tray?.Balloon("Shared desktop icons left visible",
                "Some shortcuts live on the shared Public Desktop and need admin permission to hide. " +
                "They're still in your fences. To hide them later, turn it on in Settings.");
        Hider.UnhideFailed += OnUnhideFailed;
    }

    /// <summary>Files being moved/copied into a portal right now: nothing may re-hide them mid-transfer.</summary>
    readonly HashSet<string> _transferring = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fence each item was last taken out of, so a failed un-hide can put it back.</summary>
    readonly Dictionary<string, string> _lastFence = new(StringComparer.OrdinalIgnoreCase);

    void OnUnhideFailed(List<string> paths)
    {
        // The item is still hidden on the desktop; never leave it invisible everywhere.
        foreach (var p in paths)
        {
            if (FenceOf(p) != null) continue;
            var target = _lastFence.TryGetValue(p, out var id) ? _windows.FirstOrDefault(w => w.Config.Id == id) : null;
            target ??= _windows.FirstOrDefault(w => w.Config.Kind == FenceKind.Standard)
                       ?? CreateFence("Restored icons");
            target.Config.Items.Add(p);
            target.Refresh();
        }
        Store.SaveSoon();
        Tray?.Balloon("Couldn't put some icons back",
            "Shortcuts on the shared Public Desktop need admin permission to un-hide, so they were kept in your fences. " +
            "Try again later, or use \"Put icons back on desktop & quit\".");
    }

    /// <summary>
    /// If the Desktop folder was moved (e.g. OneDrive backup), find our hidden files at the new
    /// location by name so they can still be managed and un-hidden.
    /// </summary>
    void RelinkMovedDesktop()
    {
        foreach (var old in Config.HiddenByUs.ToList())
        {
            if (ShellInfo.IsShellGuid(old) || ShellInfo.Exists(old)) continue;
            // Only when the item's folder is no longer a desktop folder (the user's Desktop really moved),
            // and only into the user's current Desktop – never across to the shared Public Desktop.
            var oldDir = Path.GetDirectoryName(old);
            if (oldDir == null || DesktopHider.DesktopFolders.Any(d => string.Equals(d.TrimEnd('\\'), oldDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!Path.GetFileName(oldDir).Equals("Desktop", StringComparison.OrdinalIgnoreCase)) continue;
            var moved = Path.Combine(DesktopHider.UserDesktop, Path.GetFileName(old));
            if (!ShellInfo.Exists(moved) || Config.HiddenByUs.Contains(moved, StringComparer.OrdinalIgnoreCase)) continue;
            Hider.Rename(old, moved);
            foreach (var w in _windows)
            {
                int i = IndexOf(w.Config.Items, old);
                if (i >= 0) w.Config.Items[i] = moved;
            }
            Log.Info($"Relinked {old} -> {moved}");
        }
    }

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        var ui = Dispatcher.CurrentDispatcher;
        ApplyIconSize();

        Layer = new DesktopLayer();
        Layer.Changed += () => { foreach (var w in _windows) PlaceInZOrder(w); };
        Layer.ForegroundChanged += () =>
        {
            // Switching to any other window ends a peek (e.g. after opening something from a fence).
            if (Peeking && DateTime.UtcNow - _peekStarted > TimeSpan.FromMilliseconds(600)) EndPeek();
        };

        NormalizeGroups();
        foreach (var fc in Config.Fences.ToList()) OpenWindow(fc);
        foreach (var w in _windows) w.RebuildTabs(); // every tab's window exists now

        RelinkMovedDesktop();
        foreach (var w in _windows) w.Refresh();
        ReconcileOrphans();
        if (Config.Settings.HideIconsOnDesktop) EnsureFencedItemsHidden();

        _watcher = new DesktopWatcher(ui);
        _watcher.Created += OnDesktopCreated;
        _watcher.Deleted += OnDesktopDeleted;
        _watcher.Renamed += OnDesktopRenamed;

        _doubleClick = new DesktopDoubleClick(ui);
        _doubleClick.DoubleClicked += () =>
        {
            if (Config.Settings.DoubleClickDesktopToToggle) ToggleVisibility();
        };

        _hotkeys = new HotkeyService();
        RegisterHotkeys();

        DailyBackup();
        var backupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) }; // catches midnight for always-on PCs
        backupTimer.Tick += (_, _) => DailyBackup();
        backupTimer.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        StartupHelper.RefreshPathIfEnabled();
        UpdateDesktopMenu();
    }

    /// <summary>
    /// Adds/removes "Desktop Fences settings" in the desktop right-click menu (also refreshes the exe path).
    /// Only the normal instance does this – not test copies started with --config.
    /// </summary>
    void UpdateDesktopMenu()
    {
        var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopFences");
        if (!string.Equals(Path.GetFullPath(Store.Dir).TrimEnd('\\'), defaultDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        DesktopMenuHelper.Set(Config.Settings.DesktopMenuEntry);
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            foreach (var w in _windows) w.UpdateTitlePosition(); // also pulls fences off a removed monitor
        }, DispatcherPriority.Background);

    /// <summary>Gathers every fence in the middle of the main screen, slightly staggered (rescues lost fences).</summary>
    public void CentreAllFences()
    {
        if (!FencesVisible) SetVisible(true);
        var primary = Native.WorkAreaFor(new Native.RECT { Left = 0, Top = 0, Right = 1, Bottom = 1 }); // main screen starts at 0,0
        var shown = _windows.Where(w => w.IsActiveTab).ToList();
        int n = shown.Count;
        for (int i = 0; i < n; i++)
            shown[i].MoveToCentre(primary, (i - (n - 1) / 2) * 32);
        Store.SaveSoon();
    }

    /// <summary>Moves every fence to the next monitor, keeping its relative position (two monitors: swaps them).</summary>
    public void SwapMonitors()
    {
        var screens = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
        if (screens.Count < 2)
        {
            MessageBox.Show("Only one monitor is connected.", "Swap monitors", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        static Native.RECT R(System.Drawing.Rectangle r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        foreach (var w in _windows.Where(w => w.IsActiveTab && w.Handle != IntPtr.Zero).ToList())
        {
            if (!Native.GetWindowRect(w.Handle, out var r)) continue;
            var src = System.Windows.Forms.Screen.FromRectangle(System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            int i = screens.FindIndex(s => s.DeviceName == src.DeviceName);
            var dst = screens[(i + 1) % screens.Count];
            w.MoveBetweenScreens(R(src.WorkingArea), R(dst.WorkingArea));
        }
        Store.SaveSoon();
    }

    void RegisterHotkeys()
    {
        if (_hotkeys == null) return;
        _hotkeys.UnregisterAll();
        HotkeyProblem = "";
        if (!Config.Settings.HotkeysEnabled) return;
        const uint ctrlAlt = Native.MOD_CONTROL | Native.MOD_ALT;
        var taken = new List<string>();
        if (!_hotkeys.Register(ctrlAlt, 0x48 /* H */, ToggleVisibility)) taken.Add("Ctrl+Alt+H");
        if (!_hotkeys.Register(ctrlAlt, 0x46 /* F */, TogglePeek)) taken.Add("Ctrl+Alt+F");
        if (taken.Count > 0) HotkeyProblem = string.Join(" and ", taken) + " is already used by another app.";
    }

    // ------------------------------------------------------------------ peek

    /// <summary>Brings every fence in front of all windows until you switch to another window.</summary>
    public void TogglePeek()
    {
        if (Peeking) { EndPeek(); return; }
        if (!FencesVisible) SetVisible(true);
        Peeking = true;
        _peekStarted = DateTime.UtcNow;
        foreach (var w in _windows)
        {
            PlaceInZOrder(w);
            w.UpdateIdleFade();
        }
    }

    public void EndPeek()
    {
        if (!Peeking) return;
        Peeking = false;
        foreach (var w in _windows)
        {
            if (w.Handle != IntPtr.Zero)
                Native.SetWindowPos(w.Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            PlaceInZOrder(w);
            w.UpdateIdleFade();
        }
    }

    public void FirstRun()
    {
        var r = MessageBox.Show(
            "Welcome to Desktop Fences!\n\n" +
            "Would you like to sort the icons on your desktop into fences automatically " +
            "(Shortcuts, Folders, Documents, Images, Installers…)?\n\n" +
            "Nothing is moved or deleted – icons are just tucked into fences, and you can put them back any time.",
            "Desktop Fences", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (r != MessageBoxResult.Yes || !AutoOrganize(confirm: false))
        {
            var wa = SystemParameters.WorkArea;
            CreateFence("My Fence", wa.Left + 120, wa.Top + 30);
        }

        Tray?.Balloon("Desktop Fences is running",
            "Drag icons into a fence. Right-click a fence or this tray icon for options. " +
            "Double-click empty desktop space to hide/show fences.");
    }

    public void Shutdown()
    {
        if (ShuttingDown) return;
        ShuttingDown = true;
        try { Hider.FlushElevatedNow(); } catch (Exception ex) { Log.Error("Flush on exit", ex); } // don't drop pending changes
        Store.SaveNow();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hotkeys?.Dispose();
        _doubleClick?.Dispose();
        _watcher?.Dispose();
        Layer?.Dispose();
        foreach (var w in _windows.ToList()) w.CloseForReal();
    }

    public void Quit() => Application.Current.Shutdown();

    Dialogs.SettingsWindow? _settingsWindow;

    /// <summary>Opens Settings (or brings it to the front if it's already open).</summary>
    public void ShowSettings()
    {
        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new Dialogs.SettingsWindow(this);
        _settingsWindow.Loaded += (_, _) => _settingsWindow?.Activate();
        try { _settingsWindow.ShowDialog(); }
        finally { _settingsWindow = null; }
    }

    public void RestoreIconsAndQuit()
    {
        var r = MessageBox.Show(
            "Put every fenced icon back on the desktop and quit?\n\n" +
            "Your fences are kept – icons will be tucked back in next time Desktop Fences starts.",
            "Desktop Fences", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        Hider.Restoring = true; // nothing may be re-hidden from here on
        DesktopMenuHelper.Set(false); // this is the "before uninstalling" step: leave no menu entry behind
        Hider.UnhideAll();
        Hider.FlushElevatedNow();
        Store.SaveNow();
        int stuck = Config.HiddenByUs.Count(p => !ShellInfo.IsShellGuid(p) && ShellInfo.Exists(p));
        if (stuck > 0)
            MessageBox.Show(
                $"{stuck} shortcut(s) on the shared Public Desktop couldn't be put back because admin permission wasn't given. " +
                "They're still hidden, and they're still in your fences.\n\n" +
                "Start Desktop Fences again and repeat \"Put icons back on desktop & quit\" before uninstalling.",
                "Desktop Fences", MessageBoxButton.OK, MessageBoxImage.Warning);
        Quit();
    }

    // ------------------------------------------------------------------ windows

    FenceWindow OpenWindow(FenceConfig fc)
    {
        var w = new FenceWindow(this, fc);
        _windows.Add(w);
        if (FencesVisible && w.IsActiveTab) w.Show();
        return w;
    }

    // ------------------------------------------------------------------ tab groups

    /// <summary>The fences shown as tabs together with <paramref name="w"/>, in tab order (just w if it's not grouped).</summary>
    public List<FenceWindow> GroupMembers(FenceWindow w)
    {
        if (w.Config.GroupId == null) return new List<FenceWindow> { w };
        var ids = Config.Fences.Where(f => f.GroupId == w.Config.GroupId).Select(f => f.Id).ToList();
        return ids.Select(id => _windows.FirstOrDefault(x => x.Config.Id == id)).OfType<FenceWindow>().ToList();
    }

    public void RefreshTabs(FenceWindow w)
    {
        foreach (var m in GroupMembers(w)) m.RebuildTabs();
    }

    /// <summary>Fixes groups left inconsistent (e.g. by editing the config by hand): one active tab, no single-tab groups.</summary>
    void NormalizeGroups()
    {
        foreach (var g in Config.Fences.Where(f => f.GroupId != null).GroupBy(f => f.GroupId).ToList())
        {
            var list = g.ToList();
            if (list.Count < 2)
            {
                foreach (var f in list) { f.GroupId = null; f.ActiveTab = false; }
                continue;
            }
            var active = list.FirstOrDefault(f => f.ActiveTab) ?? list[0];
            foreach (var f in list) f.ActiveTab = f == active;
            foreach (var f in list.Skip(1)) CopyLook(list[0], f); // tabs share the first tab's look
        }
    }

    /// <summary>Colour, opacity and text colour – what makes tabs of one group look like one fence.</summary>
    static void CopyLook(FenceConfig from, FenceConfig to)
    {
        to.TintColor = from.TintColor;
        to.Opacity = from.Opacity;
        to.TextColor = from.TextColor;
    }

    /// <summary>After one tab's look changed (Customize…, colour sliders), give every tab in its group the same look.</summary>
    public void SyncGroupLook(FenceWindow source)
    {
        foreach (var m in GroupMembers(source).Where(m => m != source))
        {
            CopyLook(source.Config, m.Config);
            m.ApplyAppearance();
            if (Config.Settings.TintIcons) m.Refresh(); // icon tint follows the fence colour
        }
        Store.SaveSoon();
    }

    public void SwitchTab(FenceWindow from, FenceWindow to)
    {
        if (from == to || from.Config.GroupId == null || from.Config.GroupId != to.Config.GroupId) return;
        from.Config.ActiveTab = false;
        to.Config.ActiveTab = true;
        to.ShowAsTab(from); // shown on top of the old tab first, so nothing flickers
        from.Hide();
        RefreshTabs(to);
        Store.SaveSoon();
    }

    /// <summary>Called when a fence is dropped after moving it: merges it into the fence whose title bar it was dropped on.</summary>
    public bool TryMergeOnDrop(FenceWindow dragged)
    {
        if (!Config.Settings.EnableTabs) return false;
        Native.GetCursorPos(out var p);
        var target = _windows.FirstOrDefault(w =>
            w.IsVisible && w.Handle != IntPtr.Zero && w != dragged &&
            (w.Config.GroupId == null || w.Config.GroupId != dragged.Config.GroupId) &&
            w.TitleBarContains(p));
        if (target == null) return false;
        if (Config.Settings.ConfirmNewTabs && MessageBox.Show(
                $"Combine \"{dragged.Config.Title}\" with \"{target.Config.Title}\" as tabs of one fence?\n\n" +
                "Click a tab to switch. To split them again, right-click → Tab → Detach (or Shift+click a tab).\n" +
                "This question can be turned off in Settings → Tabs.",
                "Create tabs", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return false;

        var gid = target.Config.GroupId ?? Guid.NewGuid().ToString("N");
        var targetMembers = GroupMembers(target);
        var draggedMembers = GroupMembers(dragged);
        foreach (var m in targetMembers) { m.Config.GroupId = gid; m.Config.ActiveTab = false; }
        // Put the new tabs right after the target group's last tab.
        int insertAt = Config.Fences.IndexOf(targetMembers[^1].Config) + 1;
        var firstTab = targetMembers[0].Config;
        foreach (var m in draggedMembers)
        {
            m.Config.GroupId = gid;
            m.Config.ActiveTab = false;
            CopyLook(firstTab, m.Config); // new tabs take on the group's look
            int i = Config.Fences.IndexOf(m.Config);
            Config.Fences.RemoveAt(i);
            if (i < insertAt) insertAt--;
            Config.Fences.Insert(insertAt++, m.Config);
        }
        // The dropped fence becomes the visible tab, in the target's place.
        dragged.Config.ActiveTab = true;
        dragged.ShowAsTab(target);
        target.Hide();
        foreach (var m in draggedMembers.Where(m => m != dragged)) m.Hide();
        RefreshTabs(dragged);
        Store.SaveSoon();
        return true;
    }

    /// <summary>Takes a tab out of its group and shows it as a fence of its own, just below and to the right.</summary>
    public void DetachTab(FenceWindow w)
    {
        var members = GroupMembers(w);
        if (members.Count < 2) return;
        var others = members.Where(m => m != w).ToList();
        if (w.Config.ActiveTab)
        {
            others[0].Config.ActiveTab = true;
            others[0].ShowAsTab(w);
        }
        var shown = others.First(m => m.Config.ActiveTab);
        w.Config.GroupId = null;
        w.Config.ActiveTab = false;
        if (others.Count == 1) { others[0].Config.GroupId = null; others[0].Config.ActiveTab = false; }

        // Same size as the group, offset so both are visible.
        w.ShowAsTab(shown);
        w.Config.Left += 36;
        w.Config.Top += 36;
        w.ApplyLayout(w.CaptureLayout());
        foreach (var m in others) m.RebuildTabs();
        w.RebuildTabs();
        foreach (var m in others) m.ApplyAppearance(); // a group of one goes back to a plain title bar
        Store.SaveSoon();
    }

    public void MoveTab(FenceWindow w, int delta)
    {
        var members = GroupMembers(w);
        int i = members.IndexOf(w), j = i + delta;
        if (i < 0 || j < 0 || j >= members.Count) return;
        int a = Config.Fences.IndexOf(w.Config), b = Config.Fences.IndexOf(members[j].Config);
        (Config.Fences[a], Config.Fences[b]) = (Config.Fences[b], Config.Fences[a]);
        RefreshTabs(w);
        Store.SaveSoon();
    }

    public FenceWindow CreateFence(string title, double? left = null, double? top = null,
        FenceKind kind = FenceKind.Standard, string? portalPath = null, bool startRename = false)
    {
        if (!FencesVisible) SetVisible(true);
        var wa = SystemParameters.WorkArea;
        int n = Config.Fences.Count % 8;
        var fc = new FenceConfig
        {
            Title = title,
            Kind = kind,
            PortalPath = portalPath,
            Width = 380,
            Height = 260,
            Left = left ?? wa.Left + (wa.Width - 380) / 2 + n * 24,
            Top = top ?? wa.Top + (wa.Height - 260) / 2 + n * 24,
            Sort = kind == FenceKind.Portal ? SortMode.Name : SortMode.Manual,
        };
        Config.Fences.Add(fc);
        var w = OpenWindow(fc);
        Store.SaveSoon();
        if (startRename) w.Dispatcher.InvokeAsync(w.BeginRename, DispatcherPriority.Background);
        return w;
    }

    public void CreatePortalInteractive(double? left = null, double? top = null)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to show in the portal fence" };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.FolderName)) return;
        var name = new DirectoryInfo(dlg.FolderName).Name;
        CreateFence(string.IsNullOrEmpty(name) ? dlg.FolderName : name, left, top, FenceKind.Portal, dlg.FolderName);
    }

    public void DeleteFence(FenceWindow w, bool confirm)
    {
        if (confirm)
        {
            var msg = w.Config.Kind == FenceKind.Portal
                ? $"Delete the portal fence \"{w.Config.Title}\"?\n\nThe folder and its files are not touched."
                : $"Delete the fence \"{w.Config.Title}\"?\n\nIts {w.Config.Items.Count} icon(s) go back onto the desktop – nothing is deleted.";
            if (MessageBox.Show(msg, "Delete fence", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
        }
        var paths = w.Config.Items.ToList();
        if (w.Config.GroupId != null)
        {
            // Deleting one tab: show a neighbour in its place and keep the rest of the group.
            var others = GroupMembers(w).Where(m => m != w).ToList();
            if (w.Config.ActiveTab && others.Count > 0) SwitchTab(w, others[0]);
            if (others.Count == 1) { others[0].Config.GroupId = null; others[0].Config.ActiveTab = false; }
            w.Config.GroupId = null;
            foreach (var m in others) { m.RebuildTabs(); m.ApplyAppearance(); }
        }
        Config.Fences.Remove(w.Config);
        _windows.Remove(w);
        w.CloseForReal();
        foreach (var p in paths)
            if (FenceOf(p) == null) Hider.Unhide(p); // a failed un-hide lands in another fence (OnUnhideFailed)
        Store.SaveSoon();
    }

    public void ToggleVisibility() => SetVisible(!FencesVisible);

    public void SetVisible(bool visible)
    {
        if (!visible) EndPeek();
        FencesVisible = visible;
        foreach (var w in _windows) w.FadeTo(visible);
    }

    public void PlaceInZOrder(FenceWindow w)
    {
        if (w.Handle == IntPtr.Zero || !w.IsVisible) return;
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
        if (Peeking)
        {
            Native.SetWindowPos(w.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
        }
        else if (Layer.DesktopShown)
        {
            RaisingFences = true;
            Native.SetWindowPos(w.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
            Native.SetWindowPos(w.Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            RaisingFences = false;
        }
        else
        {
            Native.SetWindowPos(w.Handle, Native.HWND_BOTTOM, 0, 0, 0, 0, flags);
        }
    }

    // ------------------------------------------------------------------ items

    public FenceWindow? FenceOf(string path) =>
        _windows.FirstOrDefault(w => w.Config.Kind == FenceKind.Standard && IndexOf(w.Config.Items, path) >= 0);

    static int IndexOf(List<string> list, string p) =>
        list.FindIndex(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));

    static int RemoveAll(List<string> list, IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return list.RemoveAll(set.Contains);
    }

    public void AddItems(FenceWindow target, IEnumerable<string> paths, int index, bool isReorder)
    {
        if (target.Config.Kind != FenceKind.Standard) return;
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;

        var cfg = target.Config;
        if (isReorder && cfg.Sort != SortMode.Manual)
        {
            cfg.Items = target.DisplayedPaths(); // freeze what the user sees, then switch to manual
            cfg.Sort = SortMode.Manual;
        }
        else if (cfg.Sort != SortMode.Manual)
        {
            index = -1;
        }

        foreach (var w in _windows)
            if (w != target && w.Config.Kind == FenceKind.Standard && RemoveAll(w.Config.Items, list) > 0)
                w.Refresh();

        foreach (var p in list)
        {
            int i = IndexOf(cfg.Items, p);
            if (i < 0) continue;
            cfg.Items.RemoveAt(i);
            if (index >= 0 && i < index) index--;
        }
        if (index < 0 || index > cfg.Items.Count) index = cfg.Items.Count;
        cfg.Items.InsertRange(index, list);

        HideIfEnabled(list);
        target.Refresh();
        Store.SaveSoon();
    }

    public void RemoveItems(FenceWindow fence, IEnumerable<string> paths, bool restore)
    {
        var list = paths.ToList();
        RemoveAll(fence.Config.Items, list);
        if (restore)
            foreach (var p in list)
                if (FenceOf(p) == null)
                {
                    _lastFence[p] = fence.Config.Id;
                    Hider.Unhide(p);
                }
        fence.Refresh();
        Store.SaveSoon();
    }

    public bool IsCursorOverFence()
    {
        Native.GetCursorPos(out var p);
        var root = Native.GetAncestor(Native.WindowFromPoint(p), 2);
        return _windows.Any(w => w.Handle == root);
    }

    /// <summary>
    /// Re-hides released files once whatever is copying/moving them has finished: at least
    /// <paramref name="minDelay"/> has passed, Explorer has no file operation (progress window,
    /// "replace or skip", admin prompt) open, and – for Cut/Copy – the clipboard no longer holds
    /// them (a paste copies the attributes too).
    /// </summary>
    /// <param name="childBaseline">If set, also wait (up to 2 minutes) while processes launched after this
    /// snapshot are still running – e.g. an archiver's "Add to archive" dialog started by a menu command.</param>
    public void RehideWhenSettled(DesktopHider.ReleaseTicket ticket, TimeSpan minDelay, bool waitForClipboard,
        HashSet<uint>? childBaseline = null)
    {
        if (ticket.IsEmpty) return;
        var started = DateTime.UtcNow;
        int quietTicks = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            if (ShuttingDown) { t.Stop(); return; } // still recorded as ours: re-hidden at next start
            if (DateTime.UtcNow - started < minDelay) return;
            if (childBaseline != null && DateTime.UtcNow - started < TimeSpan.FromMinutes(2) &&
                Native.ChildProcessIds().Any(id => !childBaseline.Contains(id)))
            {
                quietTicks = 0;
                return;
            }
            if (waitForClipboard)
            {
                try
                {
                    if (Clipboard.ContainsFileDropList() &&
                        Clipboard.GetFileDropList().Cast<string>()
                                 .Any(c => ticket.Paths.Contains(c, StringComparer.OrdinalIgnoreCase)))
                    {
                        quietTicks = 0;
                        return;
                    }
                }
                catch { return; } // clipboard busy: try again next tick
            }
            if (Native.ShellFileOperationRunning()) { quietTicks = 0; return; }
            if (++quietTicks < 2) return; // quiet for two checks in a row
            t.Stop();
            Hider.Rehide(ticket);
            foreach (var w in _windows) w.Refresh();
        };
        t.Start();
    }

    /// <summary>
    /// Items we hid that are no longer in any fence (e.g. the app quit while a UAC prompt for
    /// putting them back was pending) must not stay invisible: un-hide them now.
    /// </summary>
    void ReconcileOrphans()
    {
        foreach (var p in Config.HiddenByUs.ToList())
            if (FenceOf(p) == null) Hider.Unhide(p);
    }

    public void ReplacePath(string oldPath, string newPath)
    {
        ShellInfo.Invalidate(oldPath);
        Hider.Rename(oldPath, newPath);
        foreach (var w in _windows)
        {
            if (w.Config.Kind == FenceKind.Portal)
            {
                w.Refresh();
                continue;
            }
            int i = IndexOf(w.Config.Items, oldPath);
            if (i < 0) continue;
            w.Config.Items[i] = newPath;
            w.Refresh();
        }
        Store.SaveSoon();
    }

    public void MoveIntoPortal(FenceWindow portal, string[] files, bool move)
    {
        var dest = portal.Config.PortalPath;
        if (string.IsNullOrEmpty(dest)) return;
        foreach (var f in files) _transferring.Add(f);
        try { MoveIntoPortalCore(portal, dest, files, move); }
        finally { foreach (var f in files) _transferring.Remove(f); }

        // Copies (or a cancelled move) leave the original on the desktop: tuck it away again if still fenced.
        foreach (var f in files)
            if (ShellInfo.Exists(f) && FenceOf(f) != null && Config.Settings.HideIconsOnDesktop && DesktopHider.IsOnDesktop(f))
                Hider.Hide(f);
        portal.Refresh();
        Store.SaveSoon();
    }

    void MoveIntoPortalCore(FenceWindow portal, string dest, string[] files, bool move)
    {
        // Nothing may hide these files while they're on their way (it would make the result invisible).
        Hider.CancelQueuedHides(files);
        // Hidden+System would travel with the file and make it invisible in the portal, so clear it first.
        var released = Hider.ReleaseBits(files);
        var publicHidden = files.Where(f => Hider.IsHiddenByUs(f) && DesktopHider.IsOnPublicDesktop(f)).ToList();
        if (publicHidden.Count > 0)
        {
            foreach (var f in publicHidden) Hider.Unhide(f);
            Hider.FlushElevatedNow(); // must finish before the transfer
        }
        // Anything still protected-hidden (UAC declined) is left where it is.
        var transferable = files.Where(f => !(DesktopHider.IsOnPublicDesktop(f) && Hider.IsHiddenByUs(f))).ToArray();

        if (transferable.Length > 0)
            ShellActions.Transfer(transferable, dest, move, portal.Handle);

        foreach (var f in transferable)
        {
            if (ShellInfo.Exists(f)) continue;
            foreach (var w in _windows)
                if (w.Config.Kind == FenceKind.Standard && RemoveAll(w.Config.Items, new[] { f }) > 0) w.Refresh();
            Hider.Forget(f);
        }
        // Drop the release ticket without re-hiding (still marked transferring); the caller re-hides
        // originals that are still on the desktop once the transfer is over.
        Hider.Rehide(released);
    }

    void HideIfEnabled(IEnumerable<string> paths)
    {
        if (!Config.Settings.HideIconsOnDesktop) return;
        foreach (var p in paths)
            if (DesktopHider.IsOnDesktop(p))
                Hider.Hide(p);
    }

    void EnsureFencedItemsHidden()
    {
        foreach (var w in _windows)
            if (w.Config.Kind == FenceKind.Standard)
                HideIfEnabled(w.Config.Items.Where(ShellInfo.Exists));
        Store.SaveSoon();
    }

    // ------------------------------------------------------------------ desktop changes

    void OnDesktopCreated(string path)
    {
        if (_pendingDeletes.Remove(path, out var t))
        {
            // Deleted and recreated (installers do this when updating shortcuts): keep it fenced.
            t.Stop();
            ShellInfo.Invalidate(path);
            if (FenceOf(path) is { } fence)
            {
                HideIfEnabled(new[] { path });
                fence.Refresh();
            }
            return;
        }
        if (FenceOf(path) != null) return;

        try
        {
            var name = Path.GetFileName(path);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
            var attr = File.GetAttributes(path);
            if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) return;
        }
        catch { return; }

        var target = FindRuleTarget(path);
        if (target != null) AddItems(target, new[] { path }, -1, isReorder: false);
    }

    FenceWindow? FindRuleTarget(string path)
    {
        bool isDir = Directory.Exists(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var standard = _windows.Where(w => w.Config.Kind == FenceKind.Standard).ToList();

        var name = Path.GetFileName(path);
        FenceWindow? ById(string id) => standard.FirstOrDefault(w => w.Config.Id == id);

        // 1. Rules from Settings → Rules, in order.
        foreach (var rule in Config.Rules)
            if (ById(rule.TargetFenceId) is { } ruleTarget && SortRules.Matches(rule, path))
                return ruleTarget;

        // 2. Each fence's own auto-capture list. Name patterns ("*invoice*") are more specific than extensions, so they win.
        foreach (var w in standard)
            if (w.Config.AutoCaptureExtensions.Any(x => IsPattern(x) && MatchesPattern(name, x))) return w;
        foreach (var w in standard)
        {
            if (isDir && w.Config.CaptureFolders) return w;
            if (!isDir && ext.Length > 0 && w.Config.AutoCaptureExtensions.Any(x => !IsPattern(x) && NormalizeExt(x) == ext)) return w;
        }
        // 3. Catch-alls: a fence listing "*", then the default fence from Settings → Rules.
        return standard.FirstOrDefault(w => w.Config.AutoCaptureExtensions.Contains("*"))
               ?? (Config.Settings.DefaultFenceId.Length > 0 ? ById(Config.Settings.DefaultFenceId) : null);
    }

    /// <summary>A rule with wildcards that isn't the lone catch-all "*".</summary>
    public static bool IsPattern(string rule) => rule != "*" && rule.IndexOfAny(new[] { '*', '?' }) >= 0;

    public static bool MatchesPattern(string name, string pattern) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    /// <summary>Cleans up a rule: "PDF" becomes ".pdf"; patterns and "*" are kept as typed (lower case).</summary>
    public static string NormalizeExt(string x)
    {
        x = x.Trim().ToLowerInvariant();
        if (x == "*" || x.Length == 0 || IsPattern(x)) return x;
        return x.StartsWith('.') ? x : "." + x;
    }

    void OnDesktopDeleted(string path)
    {
        if (FenceOf(path) == null && !Hider.IsHiddenByUs(path)) return;
        if (_pendingDeletes.ContainsKey(path)) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _pendingDeletes.Remove(path);
            if (ShellInfo.Exists(path)) return;
            ShellInfo.Invalidate(path);
            foreach (var w in _windows)
                if (w.Config.Kind == FenceKind.Standard && RemoveAll(w.Config.Items, new[] { path }) > 0)
                    w.Refresh();
            Hider.Forget(path);
            Store.SaveSoon();
        };
        _pendingDeletes[path] = t;
        t.Start();
    }

    void OnDesktopRenamed(string oldPath, string newPath)
    {
        if (FenceOf(oldPath) != null || Hider.IsHiddenByUs(oldPath))
            ReplacePath(oldPath, newPath);
        else
            OnDesktopCreated(newPath); // e.g. a finished download "x.crdownload" → "x.zip"
    }

    // ------------------------------------------------------------------ snapping

    internal Native.RECT Snap(FenceWindow self, Native.RECT r, int edge)
    {
        double dpi = VisualTreeHelper.GetDpi(self).DpiScaleX;
        var s = Config.Settings;
        int th = (int)(12 * dpi);
        int gap = s.SnapKeepSpacing ? (int)(Math.Clamp(s.SnapSpacing, 0, 100) * dpi) : 0;
        var wa = Native.WorkAreaFor(r);

        var lefts = new List<int> { wa.Left + gap };
        var rights = new List<int> { wa.Right - gap };
        var tops = new List<int> { wa.Top + gap };
        var bottoms = new List<int> { wa.Bottom - gap };

        foreach (var w in _windows)
        {
            if (w == self || !w.IsVisible || w.Handle == IntPtr.Zero) continue;
            if (!Native.GetWindowRect(w.Handle, out var o)) continue;
            bool vClose = o.Top - th <= r.Bottom && o.Bottom + th >= r.Top;
            bool hClose = o.Left - th <= r.Right && o.Right + th >= r.Left;
            // Side by side (with the chosen spacing), and optionally lined up with the other fence's edges.
            if (vClose) { lefts.Add(o.Right + gap); rights.Add(o.Left - gap); }
            if (hClose) { tops.Add(o.Bottom + gap); bottoms.Add(o.Top - gap); }
            if (s.SnapAlignEdges)
            {
                if (vClose) { lefts.Add(o.Left); rights.Add(o.Right); }
                if (hClose) { tops.Add(o.Top); bottoms.Add(o.Bottom); }
            }
        }

        int? Best(int value, List<int> targets)
        {
            int? best = null;
            foreach (var t in targets)
            {
                int d = t - value;
                if (Math.Abs(d) <= th && (best == null || Math.Abs(d) < Math.Abs(best.Value))) best = d;
            }
            return best;
        }

        static int? Smaller(int? a, int? b) =>
            a == null ? b : b == null ? a : Math.Abs(a.Value) <= Math.Abs(b.Value) ? a : b;

        if (edge == 0)
        {
            int dx = Smaller(Best(r.Left, lefts), Best(r.Right, rights)) ?? 0;
            int dy = Smaller(Best(r.Top, tops), Best(r.Bottom, bottoms)) ?? 0;
            r.Left += dx; r.Right += dx; r.Top += dy; r.Bottom += dy;
        }
        else
        {
            // WMSZ_*: 1 left, 2 right, 3 top, 4 topleft, 5 topright, 6 bottom, 7 bottomleft, 8 bottomright
            if (edge is 1 or 4 or 7 && Best(r.Left, lefts) is int l) r.Left += l;
            if (edge is 2 or 5 or 8 && Best(r.Right, rights) is int rr) r.Right += rr;
            if (edge is 3 or 4 or 5 && Best(r.Top, tops) is int t) r.Top += t;
            if (edge is 6 or 7 or 8 && Best(r.Bottom, bottoms) is int b) r.Bottom += b;

            if (s.SnapSizeToIcons)
            {
                // Size to whole rows and columns of icons.
                double icon = Math.Clamp(s.IconSize, 24, 96);
                double cellW = (TileWidth() + 12) * dpi, cellH = (icon + (s.ShowIconLabels ? 50 : 14)) * dpi;
                double chromeW = 14 * dpi, chromeH = (30 + 10) * dpi;
                int w = (int)Math.Round(Math.Max(1, Math.Round((r.Width - chromeW) / cellW)) * cellW + chromeW);
                int h = (int)Math.Round(Math.Max(1, Math.Round((r.Height - chromeH) / cellH)) * cellH + chromeH);
                if (edge is 1 or 4 or 7) r.Left = r.Right - w;
                if (edge is 2 or 5 or 8) r.Right = r.Left + w;
                if (edge is 3 or 4 or 5) r.Top = r.Bottom - h;
                if (edge is 6 or 7 or 8) r.Bottom = r.Top + h;
            }
        }
        return r;
    }

    /// <summary>Width of one icon tile (DIPs): from the icon size, or Windows' desktop icon spacing if chosen.</summary>
    double TileWidth()
    {
        var s = Config.Settings;
        double icon = Math.Clamp(s.IconSize, 24, 96);
        if (s.MatchDesktopIconSpacing)
        {
            try
            {
                // Stored in twips as a negative number (e.g. -1125 = 75 px), measured at 96 DPI.
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\WindowMetrics");
                if (key?.GetValue("IconSpacing") is string v && int.TryParse(v, out int tw) && tw < 0)
                    return Math.Max(icon + 8, -tw / 15.0 - 12);
            }
            catch { }
        }
        return icon + 36;
    }

    // ------------------------------------------------------------------ settings

    public void ApplyIconSize()
    {
        double s = Math.Clamp(Config.Settings.IconSize, 24, 96);
        Application.Current.Resources["IconSize"] = s;
        Application.Current.Resources["TileWidth"] = TileWidth();
        Application.Current.Resources["LabelVisibility"] = Config.Settings.ShowIconLabels ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ApplySettings(AppSettings updated)
    {
        var old = Config.Settings;
        Config.Settings = updated;
        if (old.HideIconsOnDesktop != updated.HideIconsOnDesktop)
        {
            if (updated.HideIconsOnDesktop) EnsureFencedItemsHidden();
            else Hider.UnhideAll();
        }
        else if (updated.HideIconsOnDesktop && old.HidePublicDesktopIcons != updated.HidePublicDesktopIcons)
        {
            if (updated.HidePublicDesktopIcons) EnsureFencedItemsHidden();
            else
                foreach (var p in Config.HiddenByUs.Where(DesktopHider.IsOnPublicDesktop).ToList())
                    Hider.Unhide(p);
        }
        ApplyIconSize();
        if (old.HotkeysEnabled != updated.HotkeysEnabled) RegisterHotkeys();
        if (old.DesktopMenuEntry != updated.DesktopMenuEntry) UpdateDesktopMenu();
        foreach (var w in _windows)
        {
            w.ApplyAppearance();
            w.Refresh();
        }
        Store.SaveSoon();
    }

    // ------------------------------------------------------------------ all fences

    public bool AllLocked => _windows.Count > 0 && _windows.All(w => w.Config.Locked);
    public bool AnyUnrolled => _windows.Any(w => !w.Config.RolledUp);

    public void RollUpAll(bool rolledUp)
    {
        foreach (var w in _windows) w.SetRolledUp(rolledUp);
    }

    public void LockAll(bool locked)
    {
        foreach (var w in _windows) w.SetLocked(locked);
    }

    // ------------------------------------------------------------------ layout snapshots

    public void SaveLayoutInteractive()
    {
        var screen = $"{SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}";
        var dlg = new Dialogs.InputDialog("Save layout", "Name for this layout:", $"Layout {Config.Layouts.Count + 1} ({screen})");
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value.Trim();
        if (name.Length == 0) return;
        Config.Layouts.RemoveAll(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Config.Layouts.Add(new LayoutSnapshot
        {
            Name = name,
            Saved = DateTime.Now,
            Fences = _windows.Select(w => w.CaptureLayout()).ToList(),
        });
        Store.SaveSoon();
        Tray?.Balloon("Layout saved", $"Restore \"{name}\" any time from the tray menu under Layouts.");
    }

    /// <summary>
    /// Once a day: saves the fence layout as an automatic snapshot (restorable from the tray) and copies
    /// config.json to the Backups folder. The newest 7 of each are kept.
    /// </summary>
    public void DailyBackup(bool force = false)
    {
        if ((!Config.Settings.DailyBackups && !force) || ShuttingDown) return;
        var today = DateTime.Today;
        if (force) Config.Layouts.RemoveAll(l => l.Automatic && l.Saved.Date == today);
        if (Config.Layouts.Any(l => l.Automatic && l.Saved.Date == today)) return;
        Config.Layouts.Add(new LayoutSnapshot
        {
            Name = $"Daily backup {today:yyyy-MM-dd}",
            Saved = DateTime.Now,
            Automatic = true,
            Fences = _windows.Select(w => w.CaptureLayout()).ToList(),
        });
        foreach (var old in Config.Layouts.Where(l => l.Automatic).OrderByDescending(l => l.Saved).Skip(7).ToList())
            Config.Layouts.Remove(old);
        Store.SaveNow();
        try
        {
            var dir = Path.Combine(Store.Dir, "Backups");
            Directory.CreateDirectory(dir);
            File.Copy(Store.FilePath, Path.Combine(dir, $"config-{today:yyyy-MM-dd}.json"), overwrite: true);
            foreach (var f in new DirectoryInfo(dir).GetFiles("config-*.json").OrderByDescending(f => f.Name).Skip(7))
                f.Delete();
        }
        catch (Exception ex) { Log.Error("Daily backup", ex); }
    }

    public void RestoreLayout(LayoutSnapshot snap)
    {
        if (!FencesVisible) SetVisible(true);
        int applied = 0;
        foreach (var l in snap.Fences)
        {
            var w = _windows.FirstOrDefault(x => x.Config.Id == l.Id);
            if (w == null) continue;
            w.ApplyLayout(l);
            applied++;
        }
        Store.SaveSoon();
        if (applied < snap.Fences.Count)
            Tray?.Balloon("Layout restored", $"{snap.Fences.Count - applied} fence(s) in this layout have been deleted since it was saved.");
    }

    public void DeleteLayout(LayoutSnapshot snap)
    {
        if (MessageBox.Show($"Delete the saved layout \"{snap.Name}\"?\n\nYour fences aren't changed.", "Desktop Fences",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Config.Layouts.Remove(snap);
        Store.SaveSoon();
    }

    // ------------------------------------------------------------------ auto-organise

    static readonly (string Title, string Color, string[] Ext, bool Folders)[] Categories =
    {
        ("Shortcuts", "#1E1E1E", new[] { ".lnk", ".url", ".appref-ms", ".website" }, false),
        ("Folders", "#2B4A6F", Array.Empty<string>(), true),
        ("Documents", "#2E5E4E", new[] { ".pdf", ".doc", ".docx", ".txt", ".rtf", ".odt", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".md", ".json", ".xml", ".log" }, false),
        ("Images & Media", "#5E2E4E", new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".heic", ".tif", ".tiff", ".psd", ".mp4", ".mov", ".mkv", ".avi", ".mp3", ".wav", ".flac", ".m4a" }, false),
        ("Installers & Archives", "#6F4A2B", new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".exe", ".msi", ".iso", ".msix", ".appx" }, false),
        ("Other", "#3A3A4A", new[] { "*" }, false),
    };

    /// <summary>Sorts loose desktop icons into category fences. Returns false if nothing was done.</summary>
    public bool AutoOrganize(bool confirm)
    {
        var buckets = Categories.Select(_ => new List<string>()).ToArray();
        foreach (var dir in DesktopHider.DesktopFolders)
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) { Log.Error($"Scanning {dir}", ex); continue; }

            foreach (var e in entries)
            {
                if ((e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (e.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (FenceOf(e.FullName) != null) continue;

                int cat;
                if (e is DirectoryInfo) cat = 1;
                else
                {
                    var ext = e.Extension.ToLowerInvariant();
                    cat = Array.FindIndex(Categories, c => c.Ext.Contains(ext));
                    if (cat < 0) cat = Categories.Length - 1;
                }
                buckets[cat].Add(e.FullName);
            }
        }

        int total = buckets.Sum(b => b.Count);
        if (total == 0)
        {
            if (confirm)
                MessageBox.Show("There are no loose icons on your desktop to organise.", "Desktop Fences",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        int fences = buckets.Count(b => b.Count > 0);
        if (confirm && MessageBox.Show(
                $"Sort {total} desktop icon(s) into {fences} fence(s)?\n\nNothing is moved or deleted – " +
                "icons are just hidden from the bare desktop and shown inside the fences.",
                "Auto-organise desktop", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return false;

        if (!FencesVisible) SetVisible(true);
        var wa = SystemParameters.WorkArea;
        double iconSize = Config.Settings.IconSize;
        double tileW = iconSize + 36 + 14, tileH = iconSize + 62;
        const int cols = 4;
        double width = cols * tileW + 22;
        double x = wa.Left + 120, y = wa.Top + 24, rowH = 0;

        for (int i = 0; i < Categories.Length; i++)
        {
            if (buckets[i].Count == 0) continue;
            var cat = Categories[i];
            var existing = _windows.FirstOrDefault(w => w.Config.Kind == FenceKind.Standard &&
                                                        w.Config.Title.Equals(cat.Title, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                AddItems(existing, buckets[i], -1, isReorder: false);
                continue;
            }

            int rows = (int)Math.Ceiling(buckets[i].Count / (double)cols);
            double height = 32 + Math.Min(rows, 3) * tileH + 14;
            if (x + width > wa.Right - 16 && x > wa.Left + 120)
            {
                x = wa.Left + 120;
                y += rowH + 16;
                rowH = 0;
            }
            var w = CreateFence(cat.Title, x, y);
            w.Config.Width = width;
            w.Config.Height = height;
            w.Width = width;
            w.Height = height;
            w.Config.TintColor = cat.Color;
            w.Config.Sort = SortMode.Name;
            w.Config.AutoCaptureExtensions = cat.Ext.ToList();
            w.Config.CaptureFolders = cat.Folders;
            w.ApplyAppearance();
            AddItems(w, buckets[i], -1, isReorder: false);
            x += width + 16;
            rowH = Math.Max(rowH, height);
        }
        Store.SaveSoon();
        return true;
    }
}
