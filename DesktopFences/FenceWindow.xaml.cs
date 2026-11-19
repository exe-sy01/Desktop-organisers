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
    bool _dragOver;
    int _fadeVersion;
    double _idleFadeTarget = 1;

    // Title bar at the bottom (fence sits low on its screen). Config.Top always stores the *expanded*
    // top, so a rolled-up fence with a bottom title bar stays anchored at its bottom edge.
    bool _titleBottom;
    bool _layingOut;

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
        _menu.Closed += (_, _) => { _menuOpen = false; ScheduleCollapse(); UpdateIdleFade(); };
        ContextMenuOpening += OnContextMenuOpening;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); TryCollapse(); };
        _portalRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _portalRefreshTimer.Tick += (_, _) => { _portalRefreshTimer.Stop(); Refresh(); };

        EnsureOnScreen();
        _titleBottom = cfg.TitlePosition == TitlePosition.Bottom || (cfg.TitlePosition == TitlePosition.Auto && cfg.TitleAtBottom);
        Left = cfg.Left;
        Top = cfg.Top;
        Width = cfg.Width;
        Height = cfg.RolledUp ? CollapsedHeight : cfg.Height;

        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) =>
        {
            if (_layingOut) return;
            Config.Left = Left;
            Config.Top = Collapsed && _titleBottom ? Top - (ExpandedHeight - CollapsedHeight) : Top;
            _mgr.Store.SaveSoon();
        };
        SizeChanged += OnSizeChanged;
        StateChanged += (_, _) => { if (WindowState != WindowState.Normal) WindowState = WindowState.Normal; };
        MouseEnter += (_, _) => { Expand(); UpdateIdleFade(); };
        MouseLeave += (_, _) => { ScheduleCollapse(); UpdateIdleFade(); };
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => { _dragOver = false; SetDropHighlight(false); ScheduleCollapse(); UpdateIdleFade(); };
        Drop += OnDrop;
        // Fences without a title bar are moved by dragging empty space in the icon area.
        ItemsList.AddHandler(MouseMoveEvent, new MouseEventHandler(TitleBar_MouseMove), true);
        ItemsList.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(TitleBar_MouseLeftButtonUp), true);
        ItemsList.AddHandler(LostMouseCaptureEvent, new MouseEventHandler(TitleBar_LostMouseCapture), true);

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
        UpdateTitlePosition(); // now that we know which screen we're on
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_WINDOWPOSCHANGING:
                // Stay on the desktop layer: never let activation raise us above other windows.
                if (!_mgr.Layer.DesktopShown && !_mgr.RaisingFences && !_mgr.Peeking)
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
            {
                var r = Marshal.PtrToStructure<Native.RECT>(lParam);
                if (_mgr.Config.Settings.SnapToEdges && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                    r = _mgr.Snap(this, r, wParam.ToInt32());
                // Never resize past the edges of the screen.
                var wa = Native.WorkAreaFor(r);
                if (r.Left < wa.Left) r.Left = wa.Left;
                if (r.Top < wa.Top) r.Top = wa.Top;
                if (r.Right > wa.Right) r.Right = wa.Right;
                if (r.Bottom > wa.Bottom) r.Bottom = wa.Bottom;
                Marshal.StructureToPtr(r, lParam, false);
                handled = true;
                return new IntPtr(1);
            }

            case Native.WM_EXITSIZEMOVE: // finished resizing with the mouse
                Dispatcher.InvokeAsync(UpdateTitlePosition);
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
        if (_layingOut) return;
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
        _normalBackground = Color.FromArgb(a, c.R, c.G, c.B);
        _background = new SolidColorBrush(_blendShown ? BlendedBackground : _normalBackground);
        Root.Background = _background;
        TitleText.Text = Config.Title;

        // Text colour: white with a dark shadow, or dark with a light glow on pale fences.
        bool dark = UseDarkText();
        Resources["FenceTextBrush"] = dark ? DarkTextBrush : Brushes.White;
        Resources["FenceTextEffect"] = dark ? LightGlow : DarkShadow;
        TitleText.Foreground = dark ? DarkTextBrush : Brushes.White;
        TabMenuGlyph.Foreground = TitleText.Foreground;
        TitleText.Effect = dark ? LightGlow : DarkTitleShadow;
        EmptyHint.Foreground = new SolidColorBrush(dark ? Color.FromArgb(0xAA, 0, 0, 0) : Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
        EmptyHint.Effect = dark ? null : DarkShadow;

        var s = _mgr.Config.Settings;
        TitleText.HorizontalAlignment = s.TitleAlignment switch
        {
            TitleAlignment.Left => HorizontalAlignment.Left,
            TitleAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        TitleEdit.HorizontalContentAlignment = TitleText.HorizontalAlignment;

        ApplyTitleLayout();
        RebuildTabs();
        UpdateResizeability();
        _idleFadeTarget = -1; // re-apply hover visuals with the new settings
        UpdateIdleFade();
    }

    double _titleOpacityTarget = 1;
    Color _normalBackground;
    SolidColorBrush _background = new(Colors.Transparent);
    bool _blendShown;
    Color BlendedBackground => Color.FromArgb(0x0C, _normalBackground.R, _normalBackground.G, _normalBackground.B);

    public static readonly DependencyProperty BlendedProperty =
        DependencyProperty.Register(nameof(Blended), typeof(bool), typeof(FenceWindow), new PropertyMetadata(false));

    /// <summary>True while the fence blends into the wallpaper (icon template switches to grey icons).</summary>
    public bool Blended
    {
        get => (bool)GetValue(BlendedProperty);
        set => SetValue(BlendedProperty, value);
    }

    /// <summary>The fence is being pointed at or used (so idle fading / blending / hidden title bars don't apply).</summary>
    bool InUse => IsMouseOver || _menuOpen || _dragging || _dragOver || _moving || _mgr.Peeking || TitleEdit.IsVisible;

    static readonly Brush DarkTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    static readonly System.Windows.Media.Effects.Effect DarkShadow =
        Freeze(new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 1, BlurRadius = 3, Opacity = 0.85, Color = Colors.Black });
    static readonly System.Windows.Media.Effects.Effect DarkTitleShadow =
        Freeze(new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 4, Opacity = 0.7, Color = Colors.Black });
    static readonly System.Windows.Media.Effects.Effect LightGlow =
        Freeze(new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 4, Opacity = 0.6, Color = Colors.White });

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    bool UseDarkText()
    {
        if (Config.TextColor == TextColorMode.White) return false;
        if (Config.TextColor == TextColorMode.Black) return true;
        // Auto: dark text only on a pale, mostly opaque background (the wallpaper shows through otherwise).
        var c = ParseColor(Config.TintColor);
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        return lum > 0.6 && Config.Opacity >= 0.45;
    }

    /// <summary>
    /// Everything that changes when the mouse arrives or leaves: idle fading, blending into the
    /// wallpaper, title bars shown only on hover, and scrollbars shown only on hover.
    /// </summary>
    public void UpdateIdleFade()
    {
        var s = _mgr.Config.Settings;
        bool inUse = InUse;

        double idle = Math.Clamp(s.IdleOpacity, 0.15, 1);
        double target = inUse || idle >= 0.999 ? 1 : idle;
        if (Math.Abs(target - _idleFadeTarget) >= 0.001)
        {
            _idleFadeTarget = target;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(target >= 1 ? 120 : 450)));
        }

        bool blend = s.BlendWithWallpaper && !inUse;
        if (blend != _blendShown)
        {
            _blendShown = blend;
            Blended = blend;
            if (blend && Items.Any(i => i.MutedIcon == null)) Refresh(); // make the grey icons
            _background.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(blend ? BlendedBackground : _normalBackground, TimeSpan.FromMilliseconds(blend ? 500 : 150)));
            SetDropHighlight(false);
            TitleBar.Background = blend ? Brushes.Transparent : TitleBarBrush;
        }
        TitleText.Opacity = blend ? 0.55 : 1;
        TabStrip.Opacity = TabMenuButton.Opacity = blend ? 0.45 : 1;

        // Title bar only while pointed at (a rolled-up fence always shows it – it's all there is).
        double titleTarget = s.TitleBars == TitleBarMode.OnHover && !inUse && !_shownCollapsed && Config.GroupId == null ? 0 : 1;
        if (Math.Abs(titleTarget - _titleOpacityTarget) > 0.01)
        {
            _titleOpacityTarget = titleTarget;
            TitleBar.BeginAnimation(OpacityProperty, new DoubleAnimation(titleTarget, TimeSpan.FromMilliseconds(titleTarget > 0 ? 120 : 300)));
        }

        ScrollViewer.SetVerticalScrollBarVisibility(ItemsList,
            s.HideScrollbarsUntilHover && !inUse ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
    }

    static readonly Brush TitleBarBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)));

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
        Root.BorderBrush = new SolidColorBrush(on ? Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)
            : _blendShown ? Colors.Transparent : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    public void FadeTo(bool show)
    {
        int version = ++_fadeVersion;
        if (show)
        {
            if (!IsActiveTab) return; // other tabs of a group stay hidden
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
        ApplyRollState(animate: true);
        _mgr.Store.SaveSoon();
    }

    public void SetRolledUp(bool rolledUp)
    {
        if (Config.RolledUp != rolledUp) ToggleRollUp();
    }

    public void SetLocked(bool locked)
    {
        Config.Locked = locked;
        UpdateResizeability();
        _mgr.Store.SaveSoon();
    }

    // Without a title bar there's nothing to roll up to, so "Never" fences always show their icons.
    bool Collapsed => Config.RolledUp && !_hoverExpanded && TitleMode != TitleBarMode.Never;

    /// <summary>Title bar mode for this fence (tab groups always show their tabs).</summary>
    TitleBarMode TitleMode => Config.GroupId != null ? TitleBarMode.Always : _mgr.Config.Settings.TitleBars;

    public bool IsActiveTab => Config.GroupId == null || Config.ActiveTab;
    double ExpandedHeight => Math.Max(Config.Height, 80);

    /// <summary>What's on screen right now (lags behind <see cref="Collapsed"/> while rolling up).</summary>
    bool _shownCollapsed;
    int _rollVersion;

    /// <param name="animate">
    /// Slide the fence open/closed instead of snapping. The window itself is resized only once (to the
    /// full size before unrolling, or to the title bar after rolling up); in between, only the fence
    /// panel inside it grows or shrinks, anchored at the title bar, so the animation stays smooth.
    /// </param>
    void ApplyRollState(bool animate = false)
    {
        bool collapsed = Collapsed;
        int version = ++_rollVersion;
        // With the title bar at the bottom the fence rolls up (and unrolls) towards its bottom edge.
        double top = collapsed && _titleBottom ? Config.Top + ExpandedHeight - CollapsedHeight : Config.Top;
        double height = collapsed ? CollapsedHeight : ExpandedHeight;
        double from = Root.ActualHeight;
        int ms = Math.Clamp(_mgr.Config.Settings.RollAnimationMs, 0, 2000);

        if (!animate || ms == 0 || _hwnd == IntPtr.Zero || !IsVisible || from <= 0 || Math.Abs(from - height) < 2)
        {
            Root.BeginAnimation(HeightProperty, null);
            Root.Height = double.NaN;
            Root.VerticalAlignment = VerticalAlignment.Stretch;
            ShowCollapsed(collapsed);
            SetVerticalBounds(top, height);
            UpdateResizeability();
            return;
        }

        Root.VerticalAlignment = _titleBottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        if (!collapsed)
        {
            // Unrolling: make room first (the panel keeps its current height), then grow into it.
            Root.Height = from;
            ShowCollapsed(false);
            SetVerticalBounds(top, height);
        }
        var anim = new DoubleAnimation(from, height, TimeSpan.FromMilliseconds(ms * Math.Abs(height - from) / Math.Max(1, ExpandedHeight - CollapsedHeight)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        anim.Completed += (_, _) =>
        {
            if (version != _rollVersion) return; // interrupted by a newer roll
            if (collapsed) ApplyRollState(); // shrink the window to the title bar
            else
            {
                Root.BeginAnimation(HeightProperty, null);
                Root.Height = double.NaN;
                Root.VerticalAlignment = VerticalAlignment.Stretch;
            }
        };
        Root.BeginAnimation(HeightProperty, anim);
        UpdateResizeability();
    }

    void ShowCollapsed(bool collapsed)
    {
        _shownCollapsed = collapsed;
        ItemsList.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        UpdateEmptyHint();
        ApplyTitleLayout();
    }

    /// <summary>Moves and resizes vertically in one step (no flicker when the bottom edge stays put).</summary>
    void SetVerticalBounds(double top, double height)
    {
        _layingOut = true;
        try
        {
            if (_hwnd != IntPtr.Zero && Native.GetWindowRect(_hwnd, out var r))
            {
                double dpi = Dpi;
                int newTop = r.Top + (int)Math.Round((top - Top) * dpi);
                int newHeight = (int)Math.Round(height * dpi);
                if (newTop != r.Top || newHeight != r.Height)
                    Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, newTop, r.Width, newHeight,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            }
            else
            {
                Top = top;
                Height = height;
            }
        }
        finally { _layingOut = false; }
    }

    void ApplyTitleLayout()
    {
        bool b = _titleBottom, collapsed = _shownCollapsed;
        double r = _mgr.Config.Settings.RoundedCorners ? 7 : 0;
        // "Never": no title bar at all. "On hover": the bar floats over the icons instead of taking up room.
        var mode = TitleMode;
        bool floating = mode == TitleBarMode.OnHover && !collapsed;
        double titleRow = mode == TitleBarMode.Never || floating ? 0 : TitleHeight;
        TitleBar.Visibility = mode == TitleBarMode.Never ? Visibility.Collapsed : Visibility.Visible;
        TitleBar.Height = floating ? TitleHeight : double.NaN;
        TitleBar.VerticalAlignment = !floating ? VerticalAlignment.Stretch : b ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Grid.SetRowSpan(TitleBar, floating ? 2 : 1);
        Panel.SetZIndex(TitleBar, 1);
        LayoutGrid.RowDefinitions[0].Height = b ? new GridLength(1, GridUnitType.Star) : new GridLength(titleRow);
        LayoutGrid.RowDefinitions[1].Height = b ? new GridLength(titleRow) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(TitleBar, floating ? 0 : b ? 1 : 0);
        Grid.SetRow(ItemsList, b ? 0 : 1);
        Grid.SetRow(EmptyHint, b ? 0 : 1);
        ItemsList.Padding = b ? new Thickness(4, 4, 4, 2) : new Thickness(4, 2, 4, 4);
        TitleBar.CornerRadius = collapsed ? new CornerRadius(r) : b ? new CornerRadius(0, 0, r, r) : new CornerRadius(r, r, 0, 0);
        Root.CornerRadius = new CornerRadius(r > 0 ? 8 : 0);
    }

    /// <summary>
    /// Works out whether the title bar belongs at the top or bottom (Auto: bottom when the fence is in
    /// the lower half of its screen). Called when a move/resize ends, not during, so the bar never
    /// jumps out from under the mouse.
    /// </summary>
    public void UpdateTitlePosition()
    {
        bool bottom = Config.TitlePosition switch
        {
            TitlePosition.Top => false,
            TitlePosition.Bottom => true,
            _ => IsInLowerHalf(),
        };
        Config.TitleAtBottom = bottom;
        if (bottom != _titleBottom)
        {
            // A rolled-up fence keeps its title bar where it is; only the direction it unrolls in changes.
            if (Collapsed) Config.Top = bottom ? Top - (ExpandedHeight - CollapsedHeight) : Top;
            _titleBottom = bottom;
            ApplyRollState();
            _mgr.Store.SaveSoon();
        }
        KeepOnScreen();
    }

    /// <summary>Pixel rectangle the fence covers when unrolled (a rolled-up fence unrolls away from its title bar).</summary>
    Native.RECT ExpandedRectPx(Native.RECT r)
    {
        if (!Collapsed) return r;
        int h = (int)Math.Round(ExpandedHeight * Dpi);
        if (_titleBottom) r.Top = r.Bottom - h;
        else r.Bottom = r.Top + h;
        return r;
    }

    /// <summary>Shifts <paramref name="r"/> (keeping its size) so it lies inside <paramref name="wa"/>.</summary>
    static Native.RECT ClampInto(Native.RECT r, Native.RECT wa)
    {
        int dx = 0, dy = 0;
        if (r.Right > wa.Right) dx = wa.Right - r.Right;
        if (r.Left + dx < wa.Left) dx = wa.Left - r.Left;
        if (r.Bottom > wa.Bottom) dy = wa.Bottom - r.Bottom;
        if (r.Top + dy < wa.Top) dy = wa.Top - r.Top;
        r.Left += dx; r.Right += dx; r.Top += dy; r.Bottom += dy;
        return r;
    }

    /// <summary>
    /// Pulls the fence fully onto the nearest screen (shrinking it if it's bigger than the screen).
    /// Runs at startup, after moves/resizes, and when monitors are added, removed or rearranged.
    /// </summary>
    public void KeepOnScreen()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r)) return;
        var full = ExpandedRectPx(r);
        var wa = Native.WorkAreaFor(full);
        double dpi = Dpi;
        if (full.Width > wa.Width || full.Height > wa.Height)
        {
            if (full.Width > wa.Width) Config.Width = Math.Max(MinWidth, wa.Width / dpi);
            if (full.Height > wa.Height) Config.Height = Math.Max(80, wa.Height / dpi);
            _layingOut = true;
            try { Width = Config.Width; }
            finally { _layingOut = false; }
            ApplyRollState();
            if (!Native.GetWindowRect(_hwnd, out r)) return;
            full = ExpandedRectPx(r);
            _mgr.Store.SaveSoon();
        }
        MoveBy(ClampInto(full, wa), full, r);
    }

    /// <summary>Moves the window so its unrolled rectangle goes from <paramref name="from"/> to <paramref name="to"/>.</summary>
    void MoveBy(Native.RECT to, Native.RECT from, Native.RECT window)
    {
        int dx = to.Left - from.Left, dy = to.Top - from.Top;
        if (dx == 0 && dy == 0) return;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, window.Left + dx, window.Top + dy, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Puts the fence in the middle of a screen: the one it's on, or <paramref name="workArea"/> if given.
    /// <paramref name="offset"/> (pixels) staggers several fences so they don't sit exactly on top of each other.
    /// </summary>
    /// <summary>Keeps the fence's relative place when moving it from one screen's work area to another's.</summary>
    internal void MoveBetweenScreens(Native.RECT from, Native.RECT to)
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r)) return;
        var full = ExpandedRectPx(r);
        double fx = from.Width > full.Width ? (full.Left - from.Left) / (double)(from.Width - full.Width) : 0;
        double fy = from.Height > full.Height ? (full.Top - from.Top) / (double)(from.Height - full.Height) : 0;
        var dest = full;
        dest.Left = to.Left + (int)Math.Round(Math.Clamp(fx, 0, 1) * Math.Max(0, to.Width - full.Width));
        dest.Top = to.Top + (int)Math.Round(Math.Clamp(fy, 0, 1) * Math.Max(0, to.Height - full.Height));
        MoveBy(dest, full, r);
        UpdateTitlePosition();
    }

    /// <summary>Is the screen point on this fence's title bar (used to drop one fence onto another to make tabs)?</summary>
    internal bool TitleBarContains(Native.POINT p)
    {
        if (_hwnd == IntPtr.Zero || !TitleBar.IsVisible || !Native.GetWindowRect(_hwnd, out var r)) return false;
        int h = (int)Math.Round((TitleHeight + 6) * Dpi);
        int top = _titleBottom ? r.Bottom - h : r.Top, bottom = _titleBottom ? r.Bottom : r.Top + h;
        return p.X >= r.Left && p.X < r.Right && p.Y >= top && p.Y < bottom;
    }

    internal void MoveToCentre(Native.RECT? workArea = null, int offset = 0)
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r)) return;
        var full = ExpandedRectPx(r);
        var wa = workArea ?? Native.WorkAreaFor(full);
        var to = full;
        to.Left = wa.Left + (wa.Width - full.Width) / 2 + offset;
        to.Top = wa.Top + (wa.Height - full.Height) / 2 + offset;
        to.Right = to.Left + full.Width;
        to.Bottom = to.Top + full.Height;
        MoveBy(to, full, r);
        UpdateTitlePosition(); // also re-checks that it fits on the screen
    }

    bool IsInLowerHalf()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r)) return _titleBottom;
        var wa = Native.WorkAreaFor(r);
        return r.Top + r.Bottom > wa.Top + wa.Bottom;
    }

    public FenceLayout CaptureLayout() => new()
    {
        Id = Config.Id, Left = Config.Left, Top = Config.Top, Width = Config.Width, Height = Config.Height, RolledUp = Config.RolledUp,
    };

    public void ApplyLayout(FenceLayout l)
    {
        Config.Left = l.Left;
        Config.Top = l.Top;
        Config.Width = Math.Max(l.Width, MinWidth);
        Config.Height = l.Height;
        _hoverExpanded = false;
        EnsureOnScreen();
        _layingOut = true;
        try { Left = Config.Left; Width = Config.Width; }
        finally { _layingOut = false; }
        // Decide the title edge from the full (unrolled) fence, then roll up towards that edge.
        Config.RolledUp = false;
        ApplyRollState();
        UpdateTitlePosition();
        Config.RolledUp = l.RolledUp;
        ApplyRollState();
        _mgr.Store.SaveSoon();
    }

    void Expand()
    {
        _collapseTimer.Stop();
        if (Config.RolledUp && !_hoverExpanded)
        {
            _hoverExpanded = true;
            ApplyRollState(animate: true);
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
        ApplyRollState(animate: true);
    }

    // ------------------------------------------------------------------ items

    public void Refresh()
    {
        var selected = new HashSet<string>(ItemsList.SelectedItems.Cast<FenceItem>().Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> paths = Config.Kind == FenceKind.Portal ? ListPortal() : Config.Items;

        var items = paths.Select(p => new FenceItem(p, ShellInfo.GetDisplayName(p), !ShellInfo.Exists(p))).ToList();
        items = Sort(items).ToList();

        // Remember where each icon was, so icons that move can glide to their new place.
        var oldPositions = _mgr.Config.Settings.AnimateIconMoves && IsVisible ? IconPositions() : null;

        Items.Clear();
        int px = Math.Max(32, (int)Math.Round(_mgr.Config.Settings.IconSize * Dpi));
        foreach (var item in items)
        {
            Items.Add(item);
            if (ShellInfo.TryGetCachedIcon(item.Path, px, out var icon))
                SetIcon(item, icon);
            else
                Dispatcher.InvokeAsync(() => SetIcon(item, ShellInfo.GetIcon(item.Path, px)), DispatcherPriority.Background);
            if (selected.Contains(item.Path)) ItemsList.SelectedItems.Add(item);
        }
        UpdateEmptyHint();

        if (oldPositions is { Count: > 0 })
            Dispatcher.InvokeAsync(() => AnimateIconMoves(oldPositions), DispatcherPriority.Loaded);
    }

    /// <summary>Applies icon tint and prepares the grey "blend" version.</summary>
    void SetIcon(FenceItem item, ImageSource? raw)
    {
        var s = _mgr.Config.Settings;
        if (s.BlendWithWallpaper) item.MutedIcon = ImageFx.Greyscale(raw);
        if (s.TintIcons && raw != null)
        {
            var tint = s.TintSource == IconTintSource.FenceColor ? ParseColor(Config.TintColor) : ParseColor(s.IconTintColor);
            // A near-black fence colour would make icons black: lift it so the tint stays visible.
            var (h, sat, v) = ImageFx.ToHsv(tint);
            item.Icon = ImageFx.Tint(raw, ImageFx.FromHsv(h, sat, Math.Max(v, 0.55)));
        }
        else item.Icon = raw;
    }

    Dictionary<string, Point> IconPositions()
    {
        var map = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
            if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c && c.IsVisible)
                map[item.Path] = c.TranslatePoint(new Point(0, 0), ItemsList);
        return map;
    }

    void AnimateIconMoves(Dictionary<string, Point> oldPositions)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var item in Items)
        {
            if (!oldPositions.TryGetValue(item.Path, out var from)) continue;
            if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem c || !c.IsVisible) continue;
            var to = c.TranslatePoint(new Point(0, 0), ItemsList);
            double dx = from.X - to.X, dy = from.Y - to.Y;
            if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1) continue;
            var t = new TranslateTransform(dx, dy);
            c.RenderTransform = t;
            var d = TimeSpan.FromMilliseconds(260);
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, d) { EasingFunction = ease });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, d) { EasingFunction = ease });
        }
    }

    void UpdateEmptyHint()
    {
        bool collapsed = _shownCollapsed;
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
            // No title bar to grab: drag the fence by its empty space instead.
            if (TitleMode == TitleBarMode.Never && !Config.Locked)
            {
                BeginMove(ItemsList);
                e.Handled = true;
            }
            return;
        }

        if (e.ClickCount == 2 && _dragCandidate != null && !_mgr.Config.Settings.SingleClickToOpen)
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
        if (_mgr.Config.Settings.SingleClickToOpen && _dragCandidate != null && !_dragging &&
            Keyboard.Modifiers == ModifierKeys.None && ContainerAt(e.OriginalSource)?.DataContext == _dragCandidate)
        {
            ShellActions.Open(_dragCandidate.Path);
            _deferredSelect = null;
            _dragCandidate = null;
            return;
        }
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
        if (!_dragOver) { _dragOver = true; UpdateIdleFade(); }
        e.Effects = ComputeEffect(e);
        SetDropHighlight(e.Effects != DragDropEffects.None);
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        _dragOver = false;
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
        BeginMove(TitleBar);
        e.Handled = true;
    }

    UIElement? _moveHandle;

    void BeginMove(UIElement handle)
    {
        Native.GetCursorPos(out _moveStartCursor);
        Native.GetWindowRect(_hwnd, out _moveStartRect);
        _moveHandle = handle;
        _moving = handle.CaptureMouse();
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
        // Keep the whole fence (as it is when unrolled) on the screen the mouse is on.
        var full = ExpandedRectPx(r);
        var fit = ClampInto(full, Native.WorkAreaFor(new Native.RECT { Left = p.X, Top = p.Y, Right = p.X + 1, Bottom = p.Y + 1 }));
        r.Left += fit.Left - full.Left;
        r.Top += fit.Top - full.Top;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_moving) return;
        _moving = false;
        _moveHandle?.ReleaseMouseCapture();
        EndMove();
    }

    void TitleBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_moving) return;
        _moving = false;
        EndMove();
    }

    void EndMove()
    {
        // Dropped onto another fence's title bar: offer to combine them into tabs.
        if (_mgr.TryMergeOnDrop(this)) return;
        UpdateTitlePosition();
        UpdateIdleFade();
    }

    public void BeginRename()
    {
        Expand();
        TitleEdit.Text = Config.Title;
        TitleText.Visibility = Visibility.Hidden;
        TabStrip.Visibility = Visibility.Hidden;
        TitleEdit.Visibility = Visibility.Visible;
        Activate();
        TitleEdit.Focus();
        TitleEdit.SelectAll();
    }

    void EndRename(bool commit)
    {
        if (TitleEdit.Visibility != Visibility.Visible) return;
        TitleEdit.Visibility = Visibility.Collapsed;
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
        _mgr.RefreshTabs(this); // restores the title / tab strip
        ScheduleCollapse();
    }

    // ------------------------------------------------------------------ tabs

    /// <summary>Draws the tab strip when this fence is part of a tab group (or the plain title otherwise).</summary>
    public void RebuildTabs()
    {
        var members = _mgr.GroupMembers(this);
        var style = _mgr.Config.Settings.TabStyle;
        bool grouped = members.Count > 1;
        TabStrip.Children.Clear();
        TabMenuButton.Visibility = grouped && style == TabStyle.MenuOnly ? Visibility.Visible : Visibility.Collapsed;
        if (TitleEdit.IsVisible) return;
        if (!grouped || style == TabStyle.MenuOnly)
        {
            TabStrip.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            TitleText.Margin = grouped ? new Thickness(12, 0, 28, 0) : new Thickness(12, 0, 12, 0);
            return;
        }
        TitleText.Visibility = Visibility.Hidden;
        TabStrip.Visibility = Visibility.Visible;

        var fg = TitleText.Foreground;
        bool dark = fg == DarkTextBrush;
        Color hi = dark ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            bool active = m == this;
            var text = new TextBlock
            {
                Text = m.Config.Title,
                Foreground = fg,
                FontSize = 12.5,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Opacity = active ? 1 : 0.75,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = TitleText.Effect,
            };
            var tab = new Border { Child = text, Padding = new Thickness(8, 0, 8, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = m.Config.Title };
            switch (style)
            {
                case TabStyle.Segmented:
                    tab.Background = new SolidColorBrush(active ? hi : Color.FromArgb(0x14, hi.R, hi.G, hi.B));
                    tab.Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0);
                    tab.CornerRadius = new CornerRadius(i == 0 ? 4 : 0, i == members.Count - 1 ? 4 : 0, i == members.Count - 1 ? 4 : 0, i == 0 ? 4 : 0);
                    break;
                case TabStyle.Flat:
                    tab.BorderBrush = fg;
                    tab.BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0);
                    break;
                case TabStyle.Rounded:
                    if (active) tab.Background = new SolidColorBrush(hi);
                    tab.CornerRadius = new CornerRadius(10);
                    tab.Margin = new Thickness(2, 0, 2, 0);
                    break;
            }
            if (!active)
            {
                tab.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _mgr.DetachTab(m);
                    else _mgr.SwitchTab(this, m);
                };
                tab.MouseEnter += (_, _) =>
                {
                    if (!_mgr.Config.Settings.SwitchTabsOnHover) return;
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    t.Tick += (_, _) => { t.Stop(); if (tab.IsMouseOver && IsVisible) _mgr.SwitchTab(this, m); };
                    t.Start();
                };
            }
            else
            {
                tab.MouseLeftButtonDown += (_, e) =>
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return; // normal clicks move / roll up the fence
                    e.Handled = true;
                    _mgr.DetachTab(this);
                };
            }
            TabStrip.Children.Add(tab);
        }
    }

    void TabMenuButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = TabMenuButton };
        foreach (var m in _mgr.GroupMembers(this))
        {
            var mi = new MenuItem { Header = m.Config.Title, IsChecked = m == this };
            var target = m;
            mi.Click += (_, _) => { if (target != this) _mgr.SwitchTab(this, target); };
            menu.Items.Add(mi);
        }
        _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; ScheduleCollapse(); UpdateIdleFade(); };
        menu.IsOpen = true;
    }

    /// <summary>
    /// Shows this (hidden) tab in place of <paramref name="from"/>: same position, size and roll-up state.
    /// </summary>
    public void ShowAsTab(FenceWindow from)
    {
        Config.Left = from.Config.Left;
        Config.Top = from.Config.Top;
        Config.Width = from.Config.Width;
        Config.Height = from.Config.Height;
        Config.RolledUp = from.Config.RolledUp;
        Config.TitlePosition = from.Config.TitlePosition;
        Config.TitleAtBottom = from.Config.TitleAtBottom;
        Config.Locked = from.Config.Locked;
        _titleBottom = from._titleBottom;
        _hoverExpanded = from._hoverExpanded;
        _layingOut = true;
        try { Left = Config.Left; Width = Config.Width; }
        finally { _layingOut = false; }
        ApplyRollState();
        if (!IsVisible)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            Show();
        }
        // Line up exactly with the old tab (DIP rounding can differ by a pixel).
        if (_hwnd != IntPtr.Zero && from.Handle != IntPtr.Zero && Native.GetWindowRect(from.Handle, out var r))
            Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _mgr.PlaceInZOrder(this);
        ApplyAppearance();
        if (_hoverExpanded) ScheduleCollapse();
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
        bool grouped = Config.GroupId != null;
        Add(_menu, grouped ? "Rename tab" : "Rename fence", BeginRename);
        if (TitleMode != TitleBarMode.Never)
            Add(_menu, Config.RolledUp ? "Unroll (keep open)" : "Roll up", ToggleRollUp);
        if (grouped)
        {
            var tabs = new MenuItem { Header = "Tab" };
            var members = _mgr.GroupMembers(this);
            int idx = members.IndexOf(this);
            Add(tabs, "Move tab left", () => _mgr.MoveTab(this, -1), enabled: idx > 0);
            Add(tabs, "Move tab right", () => _mgr.MoveTab(this, +1), enabled: idx < members.Count - 1);
            Add(tabs, "Detach into its own fence", () => _mgr.DetachTab(this));
            _menu.Items.Add(tabs);
        }

        var sort = new MenuItem { Header = "Sort by" };
        if (Config.Kind == FenceKind.Standard)
            Add(sort, "Manual (drag to arrange)", () => SetSort(SortMode.Manual), Config.Sort == SortMode.Manual);
        Add(sort, "Name", () => SetSort(SortMode.Name), Config.Sort == SortMode.Name || (Config.Kind == FenceKind.Portal && Config.Sort == SortMode.Manual));
        Add(sort, "Type", () => SetSort(SortMode.Type), Config.Sort == SortMode.Type);
        Add(sort, "Date modified", () => SetSort(SortMode.DateModified), Config.Sort == SortMode.DateModified);
        _menu.Items.Add(sort);

        var title = new MenuItem { Header = "Title bar" };
        void SetTitlePos(TitlePosition p) { Config.TitlePosition = p; UpdateTitlePosition(); _mgr.Store.SaveSoon(); }
        Add(title, "Automatic (bottom when low on the screen)", () => SetTitlePos(TitlePosition.Auto), Config.TitlePosition == TitlePosition.Auto);
        Add(title, "Top", () => SetTitlePos(TitlePosition.Top), Config.TitlePosition == TitlePosition.Top);
        Add(title, "Bottom", () => SetTitlePos(TitlePosition.Bottom), Config.TitlePosition == TitlePosition.Bottom);
        _menu.Items.Add(title);

        Add(_menu, "Lock position and size", () => SetLocked(!Config.Locked), Config.Locked);
        Add(_menu, "Move to centre of screen", () => MoveToCentre());
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
