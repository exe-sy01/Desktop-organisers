using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopFences.Models;
using DesktopFences.Services;

namespace DesktopFences.Dialogs;

public partial class SettingsWindow : Window
{
    readonly FenceManager _mgr;
    readonly AppSettings _s; // working copy; applied on OK
    readonly List<SortRule> _rules;
    readonly Dictionary<string, (string Color, double Opacity)> _origColors;
    bool _loading = true, _saved;

    public SettingsWindow(FenceManager mgr)
    {
        _mgr = mgr;
        _s = Clone(mgr.Config.Settings);
        _rules = mgr.Config.Rules.Select(Clone).ToList();
        _origColors = mgr.Windows.ToDictionary(w => w.Config.Id, w => (w.Config.TintColor, w.Config.Opacity));
        InitializeComponent();

        // General
        HideIcons.IsChecked = _s.HideIconsOnDesktop;
        HidePublic.IsChecked = _s.HidePublicDesktopIcons;
        DoubleClick.IsChecked = _s.DoubleClickDesktopToToggle;
        SingleClick.IsChecked = _s.SingleClickToOpen;
        Startup.IsChecked = StartupHelper.IsEnabled;
        DesktopMenu.IsChecked = _s.DesktopMenuEntry;
        Hotkeys.IsChecked = _s.HotkeysEnabled;
        if (_s.HotkeysEnabled && mgr.HotkeyProblem.Length > 0)
        {
            HotkeyProblem.Text = mgr.HotkeyProblem;
            HotkeyProblem.Visibility = Visibility.Visible;
        }
        HiddenFilesWarning.Visibility = DesktopHider.ExplorerShowsProtectedFiles() ? Visibility.Visible : Visibility.Collapsed;

        // Personalization
        ColorTarget.Items.Add(new ComboBoxItem { Content = "All fences" });
        foreach (var w in mgr.Windows)
            ColorTarget.Items.Add(new ComboBoxItem { Content = w.Config.Title, Tag = w });
        ColorTarget.SelectedIndex = 0;

        TintIcons.IsChecked = _s.TintIcons;
        Select(TintSource, _s.TintSource.ToString(), 0);
        var (th, ts, tv) = ImageFx.ToHsv(FenceWindow.ParseColor(_s.IconTintColor));
        TintHue.Value = th; TintSat.Value = ts * 100; TintBright.Value = tv * 100;
        Blend.IsChecked = _s.BlendWithWallpaper;
        (_s.TitleBars switch { TitleBarMode.OnHover => TitleHover, TitleBarMode.Never => TitleNever, _ => TitleAlways }).IsChecked = true;
        Select(TitleAlign, _s.TitleAlignment.ToString(), 1);
        Select(IconSize, _s.IconSize.ToString(CultureInfo.InvariantCulture), 1);
        SelectNearest(IdleFade, _s.IdleOpacity);
        SelectNearest(RollSpeed, _s.RollAnimationMs);
        Labels.IsChecked = _s.ShowIconLabels;
        Rounded.IsChecked = _s.RoundedCorners;
        AnimateMoves.IsChecked = _s.AnimateIconMoves;
        HideScrollbars.IsChecked = _s.HideScrollbarsUntilHover;

        // Tabs
        EnableTabs.IsChecked = _s.EnableTabs;
        ConfirmTabs.IsChecked = _s.ConfirmNewTabs;
        HoverTabs.IsChecked = _s.SwitchTabsOnHover;
        (_s.TabStyle switch { TabStyle.Flat => TabFlat, TabStyle.Rounded => TabRounded, TabStyle.MenuOnly => TabMenu, _ => TabSegmented }).IsChecked = true;

        // Rules
        DefaultFence.Items.Add(new ComboBoxItem { Content = "Stay on the desktop", Tag = "" });
        foreach (var w in StandardFences())
            DefaultFence.Items.Add(new ComboBoxItem { Content = w.Config.Title, Tag = w.Config.Id });
        Select(DefaultFence, _s.DefaultFenceId, 0);
        RefreshRules();

        // Layout
        DailyBackups.IsChecked = _s.DailyBackups;
        Snap.IsChecked = _s.SnapToEdges;
        SnapSpacing.IsChecked = _s.SnapKeepSpacing;
        SnapSpacingPx.Text = _s.SnapSpacing.ToString(CultureInfo.InvariantCulture);
        SnapGrid.IsChecked = _s.SnapSizeToIcons;
        SnapAlign.IsChecked = _s.SnapAlignEdges;
        MatchSpacing.IsChecked = _s.MatchDesktopIconSpacing;

        ConfigPath.Text = mgr.Store.Dir;
        UpdateTintEnabled();
        Closed += (_, _) => { if (!_saved) RevertColors(); };
        _loading = false;
    }

    static T Clone<T>(T o) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(o))!;

    IEnumerable<FenceWindow> StandardFences() => _mgr.Windows.Where(w => w.Config.Kind == FenceKind.Standard);

    static void Select(ComboBox box, string tag, int fallback) =>
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == tag) ?? box.Items[fallback];

    static void SelectNearest(ComboBox box, double value) =>
        box.SelectedItem = box.Items.Cast<ComboBoxItem>()
            .OrderBy(i => Math.Abs(double.Parse((string)i.Tag, CultureInfo.InvariantCulture) - value)).First();

    static string TagOf(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    // ------------------------------------------------------------------ background colour (live preview)

    IEnumerable<FenceWindow> ColorTargets() =>
        (ColorTarget.SelectedItem as ComboBoxItem)?.Tag is FenceWindow w ? new[] { w } : _mgr.Windows;

    void ColorTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var first = ColorTargets().FirstOrDefault();
        if (first == null) return;
        bool was = _loading;
        _loading = true;
        var (h, s, v) = ImageFx.ToHsv(FenceWindow.ParseColor(first.Config.TintColor));
        HueSlider.Value = h;
        SatSlider.Value = s * 100;
        BrightSlider.Value = v * 100;
        TransSlider.Value = Math.Round((1 - first.Config.Opacity) * 100);
        _loading = was;
        UpdatePreview();
    }

    Color CurrentColor => ImageFx.FromHsv(HueSlider.Value, SatSlider.Value / 100, BrightSlider.Value / 100);

    void UpdatePreview()
    {
        var c = CurrentColor;
        ColorPreview.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round((1 - TransSlider.Value / 100) * 255), c.R, c.G, c.B));
    }

    void Color_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ColorPreview == null) return;
        UpdatePreview();
        var hex = ImageFx.ToHex(CurrentColor);
        double opacity = 1 - TransSlider.Value / 100;
        foreach (var w in ColorTargets())
        {
            w.Config.TintColor = hex;
            w.Config.Opacity = opacity;
            w.ApplyAppearance();
        }
    }

    void RevertColors()
    {
        foreach (var w in _mgr.Windows)
            if (_origColors.TryGetValue(w.Config.Id, out var o))
            {
                w.Config.TintColor = o.Color;
                w.Config.Opacity = o.Opacity;
                w.ApplyAppearance();
            }
    }

    void PickColor_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        Color? picked;
        try { picked = ScreenColorPicker.Pick(); }
        finally { Show(); Activate(); }
        if (picked is not { } c) return;
        var (h, s, v) = ImageFx.ToHsv(c);
        _loading = true;
        HueSlider.Value = h;
        SatSlider.Value = s * 100;
        _loading = false;
        BrightSlider.Value = v * 100; // triggers the live preview
    }

    void Tint_Changed(object sender, RoutedEventArgs e) => UpdateTintEnabled();

    void UpdateTintEnabled()
    {
        if (TintCustom == null) return;
        TintSource.IsEnabled = TintIcons.IsChecked == true;
        TintCustom.IsEnabled = TintIcons.IsChecked == true && TintSource.SelectedIndex == 1;
    }

    // ------------------------------------------------------------------ rules

    string FenceName(string id) =>
        _mgr.Windows.FirstOrDefault(w => w.Config.Id == id)?.Config.Title is { } t ? $"\"{t}\"" : "(deleted fence)";

    void RefreshRules(int select = -1)
    {
        RulesList.Items.Clear();
        foreach (var r in _rules)
        {
            var rule = r;
            var cb = new CheckBox
            {
                IsChecked = r.Enabled,
                Content = SortRules.Describe(r, FenceName),
                Margin = new Thickness(2),
            };
            cb.Checked += (_, _) => rule.Enabled = true;
            cb.Unchecked += (_, _) => rule.Enabled = false;
            RulesList.Items.Add(new ListBoxItem { Content = cb, Tag = r });
        }
        if (select >= 0 && select < RulesList.Items.Count) RulesList.SelectedIndex = select;
    }

    void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (!StandardFences().Any())
        {
            MessageBox.Show(this, "Create a fence first – rules send icons into fences.", "Rules", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new RuleEditorWindow(new SortRule(), StandardFences()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _rules.Add(dlg.Rule);
        RefreshRules(_rules.Count - 1);
    }

    void EditRule_Click(object sender, RoutedEventArgs e)
    {
        int i = RulesList.SelectedIndex;
        if (i < 0) return;
        var dlg = new RuleEditorWindow(Clone(_rules[i]), StandardFences()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _rules[i] = dlg.Rule;
        RefreshRules(i);
    }

    void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        int i = RulesList.SelectedIndex;
        if (i < 0) return;
        _rules.RemoveAt(i);
        RefreshRules(Math.Min(i, _rules.Count - 1));
    }

    void RuleUp_Click(object sender, RoutedEventArgs e) => MoveRule(-1);
    void RuleDown_Click(object sender, RoutedEventArgs e) => MoveRule(+1);

    void MoveRule(int delta)
    {
        int i = RulesList.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _rules.Count) return;
        (_rules[i], _rules[j]) = (_rules[j], _rules[i]);
        RefreshRules(j);
    }

    // ------------------------------------------------------------------ layout

    void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        _mgr.DailyBackup(force: true);
        MessageBox.Show(this, "Backup taken. Restore it from the tray menu → Layouts → Daily backups.", "Backups",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(_mgr.Store.Dir, "Backups");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    void SwapMonitors_Click(object sender, RoutedEventArgs e) => _mgr.SwapMonitors();

    // ------------------------------------------------------------------ OK

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var s = _s;
        s.HideIconsOnDesktop = HideIcons.IsChecked == true;
        s.HidePublicDesktopIcons = HidePublic.IsChecked == true;
        s.DoubleClickDesktopToToggle = DoubleClick.IsChecked == true;
        s.SingleClickToOpen = SingleClick.IsChecked == true;
        s.HotkeysEnabled = Hotkeys.IsChecked == true;
        s.DesktopMenuEntry = DesktopMenu.IsChecked == true;

        s.TintIcons = TintIcons.IsChecked == true;
        s.TintSource = Enum.Parse<IconTintSource>(TagOf(TintSource));
        s.IconTintColor = ImageFx.ToHex(ImageFx.FromHsv(TintHue.Value, TintSat.Value / 100, TintBright.Value / 100));
        s.BlendWithWallpaper = Blend.IsChecked == true;
        s.TitleBars = TitleHover.IsChecked == true ? TitleBarMode.OnHover : TitleNever.IsChecked == true ? TitleBarMode.Never : TitleBarMode.Always;
        s.TitleAlignment = Enum.Parse<TitleAlignment>(TagOf(TitleAlign));
        s.IconSize = int.Parse(TagOf(IconSize), CultureInfo.InvariantCulture);
        s.IdleOpacity = double.Parse(TagOf(IdleFade), CultureInfo.InvariantCulture);
        s.RollAnimationMs = int.Parse(TagOf(RollSpeed), CultureInfo.InvariantCulture);
        s.ShowIconLabels = Labels.IsChecked == true;
        s.RoundedCorners = Rounded.IsChecked == true;
        s.AnimateIconMoves = AnimateMoves.IsChecked == true;
        s.HideScrollbarsUntilHover = HideScrollbars.IsChecked == true;

        s.EnableTabs = EnableTabs.IsChecked == true;
        s.ConfirmNewTabs = ConfirmTabs.IsChecked == true;
        s.SwitchTabsOnHover = HoverTabs.IsChecked == true;
        s.TabStyle = TabFlat.IsChecked == true ? TabStyle.Flat : TabRounded.IsChecked == true ? TabStyle.Rounded
                   : TabMenu.IsChecked == true ? TabStyle.MenuOnly : TabStyle.Segmented;

        s.DefaultFenceId = TagOf(DefaultFence);

        s.DailyBackups = DailyBackups.IsChecked == true;
        s.SnapToEdges = Snap.IsChecked == true;
        s.SnapKeepSpacing = SnapSpacing.IsChecked == true;
        if (int.TryParse(SnapSpacingPx.Text.Trim(), out int px)) s.SnapSpacing = Math.Clamp(px, 0, 100);
        s.SnapSizeToIcons = SnapGrid.IsChecked == true;
        s.SnapAlignEdges = SnapAlign.IsChecked == true;
        s.MatchDesktopIconSpacing = MatchSpacing.IsChecked == true;

        if (Startup.IsChecked != StartupHelper.IsEnabled) StartupHelper.Set(Startup.IsChecked == true);
        _mgr.Config.Rules = _rules;
        _saved = true; // keep the colour changes
        _mgr.ApplySettings(s);
        DialogResult = true;
    }
}
