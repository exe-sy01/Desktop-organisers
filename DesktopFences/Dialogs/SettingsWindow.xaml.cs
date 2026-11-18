using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DesktopFences.Models;
using DesktopFences.Services;

namespace DesktopFences.Dialogs;

public partial class SettingsWindow : Window
{
    readonly FenceManager _mgr;

    public SettingsWindow(FenceManager mgr)
    {
        _mgr = mgr;
        InitializeComponent();
        var s = mgr.Config.Settings;
        HideIcons.IsChecked = s.HideIconsOnDesktop;
        DoubleClick.IsChecked = s.DoubleClickDesktopToToggle;
        Snap.IsChecked = s.SnapToEdges;
        Startup.IsChecked = StartupHelper.IsEnabled;
        IconSize.SelectedItem = IconSize.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == s.IconSize.ToString()) ?? IconSize.Items[1];
        ConfigPath.Text = mgr.Store.Dir;
        HidePublic.IsChecked = s.HidePublicDesktopIcons;
        HiddenFilesWarning.Visibility = DesktopHider.ExplorerShowsProtectedFiles() ? Visibility.Visible : Visibility.Collapsed;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var updated = new AppSettings
        {
            HideIconsOnDesktop = HideIcons.IsChecked == true,
            HidePublicDesktopIcons = HidePublic.IsChecked == true,
            DoubleClickDesktopToToggle = DoubleClick.IsChecked == true,
            SnapToEdges = Snap.IsChecked == true,
            IconSize = int.Parse((string)((ComboBoxItem)IconSize.SelectedItem).Tag),
        };
        if (Startup.IsChecked != StartupHelper.IsEnabled) StartupHelper.Set(Startup.IsChecked == true);
        _mgr.ApplySettings(updated);
        DialogResult = true;
    }
}
