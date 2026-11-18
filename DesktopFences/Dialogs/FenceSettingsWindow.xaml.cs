using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopFences.Models;
using Microsoft.Win32;

namespace DesktopFences.Dialogs;

public partial class FenceSettingsWindow : Window
{
    static readonly (string Name, string Hex)[] Presets =
    {
        ("Charcoal", "#1E1E1E"), ("Black", "#000000"), ("Slate", "#3A3A4A"), ("Blue", "#2B4A6F"),
        ("Teal", "#1F5C63"), ("Green", "#2E5E4E"), ("Olive", "#4F5A2A"), ("Amber", "#6F4A2B"),
        ("Red", "#6F2B2B"), ("Plum", "#5E2E4E"), ("Purple", "#4A2B6F"), ("White", "#F0F0F0"),
    };

    readonly FenceManager _mgr;
    readonly FenceWindow _fence;
    readonly string _origTitle, _origColor;
    readonly double _origOpacity;
    readonly bool _ready;
    bool _saved;

    public FenceSettingsWindow(FenceManager mgr, FenceWindow fence)
    {
        _mgr = mgr;
        _fence = fence;
        var c = fence.Config;
        _origTitle = c.Title;
        _origColor = c.TintColor;
        _origOpacity = c.Opacity;

        InitializeComponent();

        foreach (var (name, hex) in Presets)
        {
            var b = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 6), ToolTip = name,
                Background = new SolidColorBrush(FenceWindow.ParseColor(hex)),
                BorderBrush = Brushes.Gray, Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => HexBox.Text = hex;
            Swatches.Children.Add(b);
        }

        TitleBox.Text = c.Title;
        HexBox.Text = c.TintColor;
        OpacitySlider.Value = Math.Round(c.Opacity * 100);
        OpacityLabel.Text = $"({OpacitySlider.Value:0}%)";
        RulesBox.Text = string.Join(", ", c.AutoCaptureExtensions);
        FoldersCheck.IsChecked = c.CaptureFolders;
        PortalBox.Text = c.PortalPath ?? "";
        bool portal = c.Kind == FenceKind.Portal;
        RulesPanel.Visibility = portal ? Visibility.Collapsed : Visibility.Visible;
        PortalPanel.Visibility = portal ? Visibility.Visible : Visibility.Collapsed;

        Closed += (_, _) => { if (!_saved) Revert(); };
        _ready = true;
    }

    void OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        OpacityLabel.Text = $"({OpacitySlider.Value:0}%)";
        var c = _fence.Config;
        if (!string.IsNullOrWhiteSpace(TitleBox.Text)) c.Title = TitleBox.Text.Trim();
        if (IsValidColor(HexBox.Text)) c.TintColor = HexBox.Text.Trim();
        c.Opacity = OpacitySlider.Value / 100.0;
        _fence.ApplyAppearance(); // live preview
    }

    static bool IsValidColor(string s)
    {
        try { return ColorConverter.ConvertFromString(s.Trim()) is Color; }
        catch { return false; }
    }

    void Revert()
    {
        var c = _fence.Config;
        c.Title = _origTitle;
        c.TintColor = _origColor;
        c.Opacity = _origOpacity;
        _fence.ApplyAppearance();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to show in the portal fence" };
        if (Directory.Exists(PortalBox.Text)) dlg.InitialDirectory = PortalBox.Text;
        if (dlg.ShowDialog(this) == true) PortalBox.Text = dlg.FolderName;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var c = _fence.Config;
        if (c.Kind == FenceKind.Portal)
        {
            var path = PortalBox.Text.Trim();
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, "That folder doesn't exist.", "Customize fence", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.Equals(path, c.PortalPath, StringComparison.OrdinalIgnoreCase))
            {
                c.PortalPath = path;
                _fence.SetupPortalWatcher();
            }
        }
        else
        {
            c.AutoCaptureExtensions = RulesBox.Text
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(FenceManager.NormalizeExt)
                .Where(x => x.Length > 0)
                .Distinct()
                .ToList();
            c.CaptureFolders = FoldersCheck.IsChecked == true;
        }

        _saved = true;
        _fence.ApplyAppearance();
        _fence.Refresh();
        _mgr.Store.SaveSoon();
        DialogResult = true;
    }
}
