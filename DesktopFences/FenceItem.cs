using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DesktopFences;

public sealed class FenceItem : INotifyPropertyChanged
{
    ImageSource? _icon;

    public FenceItem(string path, string name, bool missing)
    {
        Path = path;
        Name = name;
        IsMissing = missing;
    }

    public string Path { get; }
    public string Name { get; }
    public bool IsMissing { get; }
    public double ItemOpacity => IsMissing ? 0.4 : 1.0;
    public string ToolTip => IsMissing ? $"Missing: {Path}" : Name;

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
