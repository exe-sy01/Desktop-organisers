using System.IO;
using System.Windows;

namespace DesktopFences.Dialogs;

public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string value)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = value;
        Loaded += (_, _) =>
        {
            Activate();
            ValueBox.Focus();
            // Select the name but not the extension, like Explorer.
            var ext = Path.GetExtension(value);
            ValueBox.Select(0, ext.Length > 0 && ext.Length < value.Length ? value.Length - ext.Length : value.Length);
        };
    }

    public string Value => ValueBox.Text;

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
