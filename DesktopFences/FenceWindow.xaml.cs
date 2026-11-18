using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopFences.Dialogs;
using DesktopFences.Interop;
using DesktopFences.Models;
using DesktopFences.Services;

namespace DesktopFences;

public partial class FenceWindow : Window
{
    public const string InternalFormat = "DesktopFences.Items";
    const double TitleHeight = 30;
    const double CollapsedHeight = TitleHeight + 2;

    readonly FenceManager _mgr;
    readonly ContextMenu _menu = new();
    readonly DispatcherTimer _collapseTimer;
    readonly DispatcherTimer _portalRefreshTimer;
    FileSystemWatcher? _portalWatcher;
    IntPtr _hwnd;

    bool _hoverExpanded;
    bool _menuOpen;
    bool _closingForReal;
    int _fadeVersion;

    // title-bar move
    bool _moving;
    Native.POINT _moveStartCursor;
    Native.RECT _moveStartRect;

    // item drag
    Point _dragStart;
    FenceItem? _dragCandidate;
    ListBoxItem? _deferredSelect;
    bool _dragging;
    bool _droppedOnDesktop;

    public FenceConfig Config { get; }
    public ObservableCollection<FenceItem> Items { get; } = new();
    public IntPtr Handle => _hwnd;

    public FenceWindow(FenceManager mgr, FenceConfig cfg)
    {
        _mgr = mgr;
        Config = cfg;
        InitializeComponent();

        ItemsList.ItemsSource = Items;
        ContextMenu = _menu;
        _menu.Opened += (_, _) => _menuOpen = true;
        _menu.Closed += (_, _) => { _menuOpen = false; ScheduleCollapse(); };
        ContextMenuOpening += OnContextMenuOpening;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); TryCollapse(); };
        _portalRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _portalRefreshTimer.Tick += (_, _) => { _portalRefreshTimer.Stop(); Refresh(); };

        EnsureOnScreen();
        Left = cfg.Left;
        Top = cfg.Top;
        Width = cfg.Width;
        Height = cfg.RolledUp ? CollapsedHeight : cfg.Height;

        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => { Config.Left = Left; Config.Top = Top; _mgr.Store.SaveSoon(); };
        SizeChanged += OnSizeChanged;
        StateChanged += (_, _) => { if (WindowState != WindowState.Normal) WindowState = WindowState.Normal; };
        MouseEnter += (_, _) => Expand();
        MouseLeave += (_, _) => ScheduleCollapse();
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => { SetDropHighlight(false); ScheduleCollapse(); };
        Drop += OnDrop;

        ApplyAppearance();
        ApplyRollState();
        SetupPortalWatcher();
        Refresh();
    }

    // ------------------------------------------------------------------ window plumbing

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        long ex = Native.ExStyle(_hwnd);
        ex = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        long style = Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE).ToInt64();
        style &= ~(Native.WS_MAXIMIZEBOX | Native.WS_MINIMIZEBOX);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, new IntPtr(style));

        int on = 1;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_EXCLUDED_FROM_PEEK, ref on, sizeof(int));

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _mgr.PlaceInZOrder(this);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_WINDOWPOSCHANGING:
                // Stay on the desktop layer: never let activation raise us above other windows.
                if (!_mgr.Layer.DesktopShown && !_mgr.RaisingFences)
                {
                    var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                    if ((wp.flags & Native.SWP_NOZORDER) == 0)
                    {
                        wp.hwndInsertAfter = Native.HWND_BOTTOM;
                        Marshal.StructureToPtr(wp, lParam, false);
                    }
                }
                break;

            case Native.WM_SIZING:
                if (_mgr.Config.Settings.SnapToEdges && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                {
                    var r = Marshal.PtrToStructure<Native.RECT>(lParam);
                    r = _mgr.Snap(this, r, wParam.ToInt32());
                    Marshal.StructureToPtr(r, lParam, false);
                    handled = true;
                    return new IntPtr(1);
                }
                break;

            case Native.WM_SYSCOMMAND:
                int sc = wParam.ToInt32() & 0xFFF0;
                if (sc is Native.SC_MAXIMIZE or Native.SC_MINIMIZE) { handled = true; return IntPtr.Zero; }
                break;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 on a fence must not destroy it.
        if (!_closingForReal && !_mgr.ShuttingDown) e.Cancel = true;
        base.OnClosing(e);
    }

    public void CloseForReal()
    {
        _closingForReal = true;
        _portalWatcher?.Dispose();
        Close();
    }

    void EnsureOnScreen()
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(Config.Left, Config.Top, Math.Max(Config.Width, 50), TitleHeight);
        if (!vs.IntersectsWith(r))
        {
            var wa = SystemParameters.WorkArea;
            Config.Left = wa.Left + 120;
            Config.Top = wa.Top + 40;
        }
    }

    double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Config.Width = Width;
        if (!Config.RolledUp && !_hoverExpanded && ItemsList.Visibility == Visibility.Visible)
            Config.Height = Height;
        _mgr.Store.SaveSoon();
    }

    // ------------------------------------------------------------------ appearance

    public void ApplyAppearance()
    {
        var c = ParseColor(Config.TintColor);
        byte a = (byte)Math.Round(Math.Clamp(Config.Opacity, 0, 1) * 255);
        Root.Background = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        TitleText.Text = Config.Title;
        UpdateResizeability();
    }

    public static Color ParseColor(string? s)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(s) && ColorConverter.ConvertFromString(s.Trim()) is Color c) return c;
        }
        catch { }
        return Color.FromRgb(0x1E, 0x1E, 0x1E);
    }

    void UpdateResizeability()
    {
        bool collapsed = Config.RolledUp && !_hoverExpanded;
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome != null)
            chrome.ResizeBorderThickness = Config.Locked || collapsed ? new Thickness(0) : new Thickness(6);
    }

    void SetDropHighlight(bool on) =>
        Root.BorderBrush = new SolidColorBrush(on ? Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    public void FadeTo(bool show)
    {
        int version = ++_fadeVersion;
        if (show)
        {
            if (!IsVisible) { Opacity = 0; Show(); _mgr.PlaceInZOrder(this); }
        }
        else if (!IsVisible) return;

        var anim = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(160));
        anim.Completed += (_, _) =>
        {
            if (version != _fadeVersion) return;
            BeginAnimation(OpacityProperty, null);
            Opacity = show ? 1 : 0;
            if (!show) Hide();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    // ------------------------------------------------------------------ roll up

    public void ToggleRollUp()
    {
        Config.RolledUp = !Config.RolledUp;
        _hoverExpanded = false;
        ApplyRollState();
        _mgr.Store.SaveSoon();
    }

    void ApplyRollState()
    {
        bool collapsed = Config.RolledUp && !_hoverExpanded;
        ItemsList.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        UpdateEmptyHint();
        TitleBar.CornerRadius = collapsed ? new CornerRadius(7) : new CornerRadius(7, 7, 0, 0);
        Height = collapsed ? CollapsedHeight : Math.Max(Config.Height, 80);
        UpdateResizeability();
    }

    void Expand()
    {
        _collapseTimer.Stop();
        if (Config.RolledUp && !_hoverExpanded)
        {
            _hoverExpanded = true;
            ApplyRollState();
        }
    }

    void ScheduleCollapse()
    {
        if (_hoverExpanded) { _collapseTimer.Stop(); _collapseTimer.Start(); }
    }

    void TryCollapse()
    {
        if (!_hoverExpanded) return;
        if (IsMouseOver || _menuOpen || _dragging || _moving || TitleEdit.IsVisible) { ScheduleCollapse(); return; }
        _hoverExpanded = false;
        ApplyRollState();
    }

    // ------------------------------------------------------------------ items

    public void Refresh()
    {
        var selected = new HashSet<string>(ItemsList.SelectedItems.Cast<FenceItem>().Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> paths = Config.Kind == FenceKind.Portal ? ListPortal() : Config.Items;

        var items = paths.Select(p => new FenceItem(p, ShellInfo.GetDisplayName(p), !ShellInfo.Exists(p))).ToList();
        items = Sort(items).ToList();

        Items.Clear();
        int px = Math.Max(32, (int)Math.Round(_mgr.Config.Settings.IconSize * Dpi));
        foreach (var item in items)
        {
            Items.Add(item);
            if (ShellInfo.TryGetCachedIcon(item.Path, px, out var icon))
                item.Icon = icon;
            else
                Dispatcher.InvokeAsync(() => item.Icon = ShellInfo.GetIcon(item.Path, px), DispatcherPriority.Background);
            if (selected.Contains(item.Path)) ItemsList.SelectedItems.Add(item);
        }
        UpdateEmptyHint();
    }

    void UpdateEmptyHint()
    {
        bool collapsed = Config.RolledUp && !_hoverExpanded;
        if (Config.Kind == FenceKind.Portal && !Directory.Exists(Config.PortalPath ?? ""))
            EmptyHint.Text = "Folder not found.\nRight-click → Customize… to pick another.";
        else
            EmptyHint.Text = Config.Kind == FenceKind.Portal ? "This folder is empty" : "Drag icons here";
        EmptyHint.Visibility = !collapsed && Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    IEnumerable<FenceItem> Sort(List<FenceItem> items)
    {
        var mode = Config.Sort;
        if (mode == SortMode.Manual)
        {
            if (Config.Kind != FenceKind.Portal) return items;
            mode = SortMode.Name;
        }
        var byName = Comparer<FenceItem>.Create((a, b) => Native.StrCmpLogicalW(a.Name, b.Name));
        return mode switch
        {
            SortMode.Name => items.OrderBy(i => IsFolder(i.Path) ? 0 : 1).ThenBy(i => i, byName),
            SortMode.Type => items.OrderBy(i => IsFolder(i.Path) ? 0 : 1)
                                  .ThenBy(i => Path.GetExtension(i.Path), StringComparer.OrdinalIgnoreCase)
                                  .ThenBy(i => i, byName),
            SortMode.DateModified => items.OrderByDescending(i => Modified(i.Path)),
            _ => items,
        };
    }

    static bool IsFolder(string p) => !ShellInfo.IsShellGuid(p) && Directory.Exists(p);

    static DateTime Modified(string p)
    {
        try
        {
            if (File.Exists(p)) return File.GetLastWriteTime(p);
            if (Directory.Exists(p)) return Directory.GetLastWriteTime(p);
        }
        catch { }
        return DateTime.MinValue;
    }

    IEnumerable<string> ListPortal()
    {
        var dir = Config.PortalPath;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return Array.Empty<string>();
        try
        {
            return new DirectoryInfo(dir).EnumerateFileSystemInfos()
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error($"Listing portal {dir}", ex);
            return Array.Empty<string>();
        }
    }

    public void SetupPortalWatcher()
    {
        _portalWatcher?.Dispose();
        _portalWatcher = null;
        if (Config.Kind != FenceKind.Portal || string.IsNullOrEmpty(Config.PortalPath) || !Directory.Exists(Config.PortalPath)) return;
        try
        {
            var w = new FileSystemWatcher(Config.PortalPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler changed = (_, e) => Dispatcher.InvokeAsync(() =>
            {
                ShellInfo.Invalidate(e.FullPath);
                _portalRefreshTimer.Stop();
                _portalRefreshTimer.Start();
            });
            w.Created += changed;
            w.Deleted += changed;
            w.Changed += changed;
            w.Renamed += (_, e) => Dispatcher.InvokeAsync(() =>
            {
                ShellInfo.Invalidate(e.OldFullPath);
                _portalRefreshTimer.Stop();
                _portalRefreshTimer.Start();
            });
            w.EnableRaisingEvents = true;
            _portalWatcher = w;
        }
        catch (Exception ex) { Log.Error("Portal watcher", ex); }
    }

    List<string> SelectedPaths() => ItemsList.SelectedItems.Cast<FenceItem>().Select(i => i.Path).ToList();

    /// <summary>Paths in the order they're currently displayed.</summary>
    public List<string> DisplayedPaths() => Items.Select(i => i.Path).ToList();

    ListBoxItem? ContainerAt(object source) =>
        source is DependencyObject d ? ItemsControl.ContainerFromElement(ItemsList, d) as ListBoxItem : null;

    // ------------------------------------------------------------------ mouse on items

    void Items_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var lbi = ContainerAt(e.OriginalSource);
        _dragStart = e.GetPosition(this);
        _dragCandidate = lbi?.DataContext as FenceItem;
        _deferredSelect = null;

        if (lbi == null)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(d) != null) return;
            ItemsList.UnselectAll();
            ItemsList.Focus();
            return;
        }

        if (e.ClickCount == 2 && _dragCandidate != null)
        {
            ShellActions.Open(_dragCandidate.Path);
            _dragCandidate = null;
            e.Handled = true;
            return;
        }

        // Clicking an already-selected item may start a multi-item drag; postpone the re-selection.
        if (lbi.IsSelected && Keyboard.Modifiers == ModifierKeys.None)
        {
            _deferredSelect = lbi;
            lbi.Focus();
            e.Handled = true;
        }
    }

    void Items_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelect != null && !_dragging)
        {
            ItemsList.SelectedItems.Clear();
            _deferredSelect.IsSelected = true;
        }
        _deferredSelect = null;
        _dragCandidate = null;
    }

    void Items_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragCandidate;
        _dragCandidate = null;
        _deferredSelect = null;
        if (!ItemsList.SelectedItems.Contains(item))
        {
            ItemsList.SelectedItems.Clear();
            ItemsList.SelectedItems.Add(item);
        }
        StartDrag();
    }

    void Items_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var lbi = ContainerAt(e.OriginalSource);
        if (lbi == null) return;
        if (!lbi.IsSelected)
        {
            ItemsList.SelectedItems.Clear();
            lbi.IsSelected = true;
        }
        lbi.Focus();
        e.Handled = true;
    }

    void Items_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ContainerAt(e.OriginalSource) == null) return;
        e.Handled = true;
        ShowItemMenu();
    }

    void ShowItemMenu()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        var extras = new List<ShellContextMenu.Extra>();
        if (Config.Kind == FenceKind.Standard)
        {
            bool allMissing = paths.All(p => !ShellInfo.Exists(p));
            extras.Add(new(allMissing ? "Remove from fence" : "Remove from fence (back to desktop)",
                () => _mgr.RemoveItems(this, paths, restore: true)));
        }
        _menuOpen = true;
        // For shared (Public Desktop) shortcuts we hid, only offer commands that can't copy/move/delete
        // them – their hidden bits would go along and the result would be invisible.
        // (Decided per menu, from the items it actually covers, so a mixed selection keeps full menus.)
        try
        {
            ShellContextMenu.Show(this, paths, extras, RenameItem, OnShellVerb,
                restrictItem: IsLockedPublic, keepVerb: DesktopHider.IsSafeVerb);
        }
        finally { _menuOpen = false; }
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var p in paths) ShellInfo.Invalidate(p);
            Refresh();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Hidden+System travels with a file, so before Explorer copies, moves or recycles a fenced
    /// file we release the bits, and put them back afterwards if the file is still here.
    /// </summary>
    Action? OnShellVerb(string? verb, IReadOnlyList<string> paths)
    {
        // Verbs that only open/show/launch the file (Open, Open with, Edit, Play, Print…) can't carry the bits anywhere.
        if (DesktopHider.IsSafeVerb(verb)) return null;
        // Everything else (copy, cut, delete, Send to, "compress", extensions…) may copy/move the file.
        var ticket = _mgr.Hider.ReleaseBits(paths);
        bool clipboard = string.Equals(verb, "cut", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(verb, "copy", StringComparison.OrdinalIgnoreCase);
        // Extension commands often open their own dialog/app (e.g. "Add to archive…"): wait for those too.
        var baseline = Native.ChildProcessIds();
        return () => _mgr.RehideWhenSettled(ticket, TimeSpan.FromSeconds(3), clipboard, baseline);
    }

    /// <summary>Public Desktop items we hid can't be released without admin rights.</summary>
    bool IsLockedPublic(string p) => DesktopHider.IsOnPublicDesktop(p) && _mgr.Hider.IsHiddenByUs(p);

    void Items_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var paths = SelectedPaths();
        switch (e.Key)
        {
            case Key.Enter when paths.Count > 0:
                foreach (var p in paths.Take(15)) ShellActions.Open(p);
                e.Handled = true;
                break;
            case Key.Delete when paths.Count > 0:
                var locked = paths.Where(IsLockedPublic).ToList();
                if (locked.Count > 0)
                    MessageBox.Show(
                        "Shortcuts shared by all users (on the Public Desktop) can't be deleted from a fence.\n\n" +
                        "Drag them out of the fence onto the desktop first, then delete them there.",
                        "Desktop Fences", MessageBoxButton.OK, MessageBoxImage.Information);
                var deletable = paths.Except(locked).ToList();
                if (deletable.Count > 0)
                {
                    // Release first so a file restored from the Recycle Bin comes back visible.
                    var released = _mgr.Hider.ReleaseBits(deletable);
                    ShellActions.Recycle(deletable, _hwnd); // synchronous
                    _mgr.RehideWhenSettled(released, TimeSpan.Zero, waitForClipboard: false); // if it was cancelled
                }
                e.Handled = true;
                break;
            case Key.F2 when paths.Count > 0:
                RenameItem(paths[0]);
                e.Handled = true;
                break;
            case Key.Apps when paths.Count > 0:
                ShowItemMenu();
                e.Handled = true;
                break;
        }
    }

    void RenameItem(string path)
    {
        if (ShellInfo.IsShellGuid(path) || !ShellInfo.Exists(path)) return;
        var dir = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        bool hideExt = !Directory.Exists(path) &&
                       (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase));
        var shown = hideExt ? Path.GetFileNameWithoutExtension(path) : fileName;

        var dlg = new InputDialog("Rename", "New name:", shown);
        if (dlg.ShowDialog() != true) return;
        var value = dlg.Value.Trim();
        if (value.Length == 0 || value == shown) return;
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("A file name can't contain any of these characters:\n\\ / : * ? \" < > |", "Rename",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var newPath = Path.Combine(dir, hideExt ? value + ext : value);
        try
        {
            if (Directory.Exists(path)) Directory.Move(path, newPath);
            else File.Move(path, newPath);
            _mgr.ReplacePath(path, newPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't rename:\n{ex.Message}", "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ drag out

    void StartDrag()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;

        var data = new DataObject();
        // Files are offered to other apps (Explorer folders etc.), except Public Desktop items we hid:
        // their Hidden+System bits can't be released without admin rights and would travel with them.
        var files = paths.Where(p => !ShellInfo.IsShellGuid(p) && (File.Exists(p) || Directory.Exists(p)) && !IsLockedPublic(p))
                         .ToArray();
        if (files.Length > 0) data.SetData(DataFormats.FileDrop, files);
        data.SetData(InternalFormat, Config.Id + "\n" + string.Join("\n", paths));

        _dragging = true;
        _droppedOnDesktop = false;
        _dragFiles = files;
        _releasedForDrag = null;
        var result = DragDropEffects.None;
        try
        {
            result = DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex) { Log.Error("Drag", ex); }
        finally { _dragging = false; }

        if (_droppedOnDesktop)
            _mgr.RemoveItems(this, paths, restore: true);
        if (_releasedForDrag != null)
        {
            if ((result & (DragDropEffects.Copy | DragDropEffects.Move)) == 0)
                _mgr.Hider.Rehide(_releasedForDrag); // target refused it (or only linked): nothing was copied
            else
                // Explorer copies/moves on its own thread after the drop; wait for it to finish.
                _mgr.RehideWhenSettled(_releasedForDrag, TimeSpan.FromSeconds(3), waitForClipboard: false);
        }
        ScheduleCollapse();
    }

    string[] _dragFiles = Array.Empty<string>();
    DesktopHider.ReleaseTicket? _releasedForDrag;

    void Items_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed || (e.KeyStates & DragDropKeyStates.LeftMouseButton) != 0) return;
        // Dropping a standard-fence item onto the desktop just takes it out of the fence.
        // We cancel the shell drop so Explorer doesn't try to move the file onto itself.
        if (Config.Kind == FenceKind.Standard && Native.IsCursorOverDesktop())
        {
            _droppedOnDesktop = true;
            e.Action = DragAction.Cancel;
            e.Handled = true;
            return;
        }
        // Dropping into any other app: it may copy or move the file (Explorer, VS Code, Total Commander,
        // archivers…), so clear our Hidden+System first or the result would be invisible. If the target
        // only reads it (a browser upload) the icon shows briefly and is tucked away again afterwards.
        if (!_mgr.IsCursorOverFence() && _releasedForDrag == null)
            _releasedForDrag = _mgr.Hider.ReleaseBits(_dragFiles);
    }

    // ------------------------------------------------------------------ drop in

    static (string sourceId, List<string> paths)? ReadInternal(IDataObject data)
    {
        if (!data.GetDataPresent(InternalFormat)) return null;
        if (data.GetData(InternalFormat) is not string s) return null;
        var parts = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : (parts[0], parts.Skip(1).ToList());
    }

    static List<string> ReadExternal(IDataObject data)
    {
        var list = new List<string>();
        if (data.GetDataPresent("Shell IDList Array"))
            list = ShellInfo.ParseIdListArray(data.GetData("Shell IDList Array")).Where(ShellInfo.Exists).ToList();
        if (list.Count == 0 && data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            list = files.ToList();
        return list;
    }

    /// <summary>
    /// Files to move into a portal: the FileDrop list plus, for drags from our own fences, items left
    /// out of FileDrop (shared Public Desktop shortcuts – MoveIntoPortal un-hides those first).
    /// </summary>
    static string[] PortalFiles(IDataObject data)
    {
        var list = new List<string>();
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            list.AddRange(files);
        if (ReadInternal(data) is { } internalDrag)
            list.AddRange(internalDrag.paths.Where(p => !ShellInfo.IsShellGuid(p) && (File.Exists(p) || Directory.Exists(p))));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    DragDropEffects ComputeEffect(DragEventArgs e)
    {
        var internalDrag = ReadInternal(e.Data);
        if (Config.Kind == FenceKind.Portal)
        {
            if (string.IsNullOrEmpty(Config.PortalPath) || !Directory.Exists(Config.PortalPath)) return DragDropEffects.None;
            if (internalDrag?.sourceId == Config.Id) return DragDropEffects.None;
            var files = PortalFiles(e.Data);
            if (files.Length == 0) return DragDropEffects.None;
            if (files.All(f => string.Equals(Path.GetDirectoryName(f), Config.PortalPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                return DragDropEffects.None;
            bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
            bool sameDrive = string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(Config.PortalPath), StringComparison.OrdinalIgnoreCase);
            bool copy = ctrl || (!shift && !sameDrive);
            var want = copy ? DragDropEffects.Copy : DragDropEffects.Move;
            return (e.AllowedEffects & want) != 0 ? want : e.AllowedEffects & (DragDropEffects.Copy | DragDropEffects.Move);
        }

        if (internalDrag != null) return DragDropEffects.Move;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("Shell IDList Array"))
        {
            // We only *reference* the item, so never report Move back to Explorer (it could delete the source).
            if ((e.AllowedEffects & DragDropEffects.Link) != 0) return DragDropEffects.Link;
            if ((e.AllowedEffects & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
        }
        return DragDropEffects.None;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        Expand();
        e.Effects = ComputeEffect(e);
        SetDropHighlight(e.Effects != DragDropEffects.None);
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        var effect = ComputeEffect(e);
        e.Handled = true;
        if (effect == DragDropEffects.None) { e.Effects = DragDropEffects.None; return; }

        try
        {
            var internalDrag = ReadInternal(e.Data);
            if (Config.Kind == FenceKind.Portal)
            {
                var files = PortalFiles(e.Data);
                // Defer so Explorer's drag source isn't blocked by our file-operation UI.
                Dispatcher.InvokeAsync(() => _mgr.MoveIntoPortal(this, files, effect == DragDropEffects.Move));
                e.Effects = DragDropEffects.None;
                return;
            }

            if (internalDrag != null)
            {
                int index = GetInsertIndex(e);
                _mgr.AddItems(this, internalDrag.Value.paths, index, isReorder: internalDrag.Value.sourceId == Config.Id);
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                var paths = ReadExternal(e.Data);
                _mgr.AddItems(this, paths, GetInsertIndex(e), isReorder: false);
                e.Effects = effect;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Drop", ex);
            e.Effects = DragDropEffects.None;
        }
    }

    int GetInsertIndex(DragEventArgs e)
    {
        var p = e.GetPosition(ItemsList);
        var hit = ItemsList.InputHitTest(p) as DependencyObject;
        var lbi = hit != null ? ItemsControl.ContainerFromElement(ItemsList, hit) as ListBoxItem : null;
        if (lbi == null) return -1;
        int idx = ItemsList.ItemContainerGenerator.IndexFromContainer(lbi);
        if (idx < 0) return -1;
        if (e.GetPosition(lbi).X > lbi.ActualWidth / 2) idx++;
        return idx;
    }

    // ------------------------------------------------------------------ title bar

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TitleEdit.IsVisible) return;
        if (e.ClickCount == 2)
        {
            ToggleRollUp();
            e.Handled = true;
            return;
        }
        if (Config.Locked) return;
        Native.GetCursorPos(out _moveStartCursor);
        Native.GetWindowRect(_hwnd, out _moveStartRect);
        _moving = TitleBar.CaptureMouse();
        e.Handled = true;
    }

    void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_moving) return;
        Native.GetCursorPos(out var p);
        var r = _moveStartRect;
        int dx = p.X - _moveStartCursor.X, dy = p.Y - _moveStartCursor.Y;
        r.Left += dx; r.Right += dx; r.Top += dy; r.Bottom += dy;
        if (_mgr.Config.Settings.SnapToEdges && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
            r = _mgr.Snap(this, r, 0);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_moving) return;
        _moving = false;
        TitleBar.ReleaseMouseCapture();
    }

    void TitleBar_LostMouseCapture(object sender, MouseEventArgs e) => _moving = false;

    public void BeginRename()
    {
        Expand();
        TitleEdit.Text = Config.Title;
        TitleText.Visibility = Visibility.Hidden;
        TitleEdit.Visibility = Visibility.Visible;
        Activate();
        TitleEdit.Focus();
        TitleEdit.SelectAll();
    }

    void EndRename(bool commit)
    {
        if (TitleEdit.Visibility != Visibility.Visible) return;
        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        if (commit)
        {
            var t = TitleEdit.Text.Trim();
            if (t.Length > 0 && t != Config.Title)
            {
                Config.Title = t;
                TitleText.Text = t;
                _mgr.Store.SaveSoon();
            }
        }
        ScheduleCollapse();
    }

    void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndRename(true); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndRename(false); e.Handled = true; }
    }

    void TitleEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndRename(true);

    // ------------------------------------------------------------------ fence menu

    void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ContainerAt(d) != null)
        {
            e.Handled = true; // item menus are handled by the shell menu
            return;
        }
        BuildMenu();
    }

    void BuildMenu()
    {
        _menu.Items.Clear();
        MenuItem Add(ItemsControl parent, string header, Action action, bool isChecked = false, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            parent.Items.Add(mi);
            return mi;
        }

        Add(_menu, "New fence", () => _mgr.CreateFence("New Fence", Left + 30, Top + 30, startRename: true));
        Add(_menu, "New folder portal…", () => _mgr.CreatePortalInteractive(Left + 30, Top + 30));
        _menu.Items.Add(new Separator());
        Add(_menu, "Rename fence", BeginRename);
        Add(_menu, Config.RolledUp ? "Unroll (keep open)" : "Roll up", ToggleRollUp);

        var sort = new MenuItem { Header = "Sort by" };
        if (Config.Kind == FenceKind.Standard)
            Add(sort, "Manual (drag to arrange)", () => SetSort(SortMode.Manual), Config.Sort == SortMode.Manual);
        Add(sort, "Name", () => SetSort(SortMode.Name), Config.Sort == SortMode.Name || (Config.Kind == FenceKind.Portal && Config.Sort == SortMode.Manual));
        Add(sort, "Type", () => SetSort(SortMode.Type), Config.Sort == SortMode.Type);
        Add(sort, "Date modified", () => SetSort(SortMode.DateModified), Config.Sort == SortMode.DateModified);
        _menu.Items.Add(sort);

        Add(_menu, "Lock position and size", () =>
        {
            Config.Locked = !Config.Locked;
            UpdateResizeability();
            _mgr.Store.SaveSoon();
        }, Config.Locked);
        Add(_menu, "Customize…", () => new FenceSettingsWindow(_mgr, this).ShowDialog());

        if (Config.Kind == FenceKind.Portal && !string.IsNullOrEmpty(Config.PortalPath))
            Add(_menu, "Open folder in Explorer", () => ShellActions.OpenFolder(Config.PortalPath!), enabled: Directory.Exists(Config.PortalPath));

        _menu.Items.Add(new Separator());
        Add(_menu, "Delete fence", () => _mgr.DeleteFence(this, confirm: true));
    }

    void SetSort(SortMode mode)
    {
        Config.Sort = mode;
        Refresh();
        _mgr.Store.SaveSoon();
    }

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
