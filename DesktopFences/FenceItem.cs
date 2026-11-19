using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DesktopFences;

public sealed class FenceItem : INotifyPropertyChanged
{
    ImageSource? _icon, _mutedIcon;

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

    /// <summary>The icon as shown (tinted if icon tint is on).</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnChanged(); }
    }

    /// <summary>Grey version used while the fence blends into the wallpaper.</summary>
    public ImageSource? MutedIcon
    {
        get => _mutedIcon;
        set { _mutedIcon = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
