using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DesktopFences.Models;
using DesktopFences.Services;

namespace DesktopFences.Dialogs;

/// <summary>Add / edit one sorting rule: what to match, and which fence it goes to.</summary>
public sealed class RuleEditorWindow : Window
{
    static readonly (RuleKind Kind, string Label, string Hint)[] Kinds =
    {
        (RuleKind.FileType, "File type is…", ""),
        (RuleKind.NameContains, "Name contains…", "Text to look for, e.g. invoice"),
        (RuleKind.NameStartsWith, "Name starts with…", "e.g. Screenshot"),
        (RuleKind.NameEndsWith, "Name ends with…", "e.g. (final) – the extension doesn't count for shortcuts"),
        (RuleKind.ShortcutTarget, "Shortcut points to…", "Part of the target path or web address, e.g. steam or C:\\Games"),
        (RuleKind.CreatedBetweenHours, "Created between hours…", "From-to in 24-hour time, e.g. 9-17 (or 22-6 overnight)"),
        (RuleKind.CreatedOnDays, "Created on days…", "Comma separated, e.g. Sat, Sun"),
        (RuleKind.SizeLargerThan, "File is larger than (MB)…", "Size in megabytes, e.g. 500"),
    };

    readonly ComboBox _kind = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly ComboBox _category = new() { Margin = new Thickness(0, 4, 0, 4) };
    readonly TextBox _value = new() { Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 4, 0, 4) };
    readonly TextBlock _hint = new() { Foreground = System.Windows.Media.Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    readonly ComboBox _fence = new() { Margin = new Thickness(0, 4, 0, 14) };

    public SortRule Rule { get; }

    public RuleEditorWindow(SortRule rule, IEnumerable<FenceWindow> fences)
    {
        Rule = rule;
        Title = "Sorting rule";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        Topmost = true;

        foreach (var k in Kinds) _kind.Items.Add(new ComboBoxItem { Content = k.Label, Tag = k.Kind });
        foreach (FileCategory c in Enum.GetValues(typeof(FileCategory)))
            _category.Items.Add(new ComboBoxItem { Content = SortRules.CategoryName(c), Tag = c.ToString() });
        foreach (var f in fences) _fence.Items.Add(new ComboBoxItem { Content = f.Config.Title, Tag = f.Config.Id });

        _kind.SelectedItem = _kind.Items.Cast<ComboBoxItem>().First(i => (RuleKind)i.Tag == rule.Kind);
        _category.SelectedItem = _category.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == rule.Value) ?? _category.Items[0];
        _value.Text = rule.Kind == RuleKind.FileType ? "" : rule.Value;
        _fence.SelectedItem = _fence.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == rule.TargetFenceId) ?? _fence.Items[0];
        _kind.SelectionChanged += (_, _) => UpdateKind();

        var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)Application.Current.Resources["DialogButton"] };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)Application.Current.Resources["DialogButton"] };
        ok.Click += (_, _) => Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "New desktop icons whose…", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_kind);
        panel.Children.Add(_category);
        panel.Children.Add(_value);
        panel.Children.Add(_hint);
        panel.Children.Add(new TextBlock { Text = "…go into the fence", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_fence);
        panel.Children.Add(buttons);
        Content = panel;
        UpdateKind();
        Loaded += (_, _) => { if (_value.IsVisible) _value.Focus(); };
    }

    RuleKind Kind => (RuleKind)((ComboBoxItem)_kind.SelectedItem).Tag;

    void UpdateKind()
    {
        bool type = Kind == RuleKind.FileType;
        _category.Visibility = type ? Visibility.Visible : Visibility.Collapsed;
        _value.Visibility = type ? Visibility.Collapsed : Visibility.Visible;
        _hint.Text = type ? "Downloaded files are files saved from the internet (browsers mark them)." : Kinds.First(k => k.Kind == Kind).Hint;
    }

    void Save()
    {
        var value = Kind == RuleKind.FileType ? (string)((ComboBoxItem)_category.SelectedItem).Tag : _value.Text.Trim();
        string? problem = Kind switch
        {
            RuleKind.FileType => null,
            RuleKind.CreatedBetweenHours when !SortRules.TryParseHours(value, out _, out _) => "Enter the hours as from-to, e.g. 9-17.",
            RuleKind.SizeLargerThan when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => "Enter a number of megabytes, e.g. 500.",
            _ when value.Length == 0 => "Enter something to look for.",
            _ => null,
        };
        if (problem != null)
        {
            MessageBox.Show(this, problem, "Sorting rule", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Rule.Kind = Kind;
        Rule.Value = value;
        Rule.TargetFenceId = (string)((ComboBoxItem)_fence.SelectedItem).Tag;
        DialogResult = true;
    }
}
