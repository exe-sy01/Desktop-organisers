using System;
using System.Collections.Generic;

namespace DesktopFences.Models;

public enum FenceKind { Standard, Portal }

public enum SortMode { Manual, Name, Type, DateModified }

public sealed class FenceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New Fence";
    public FenceKind Kind { get; set; } = FenceKind.Standard;

    /// <summary>Folder shown by a Portal fence.</summary>
    public string? PortalPath { get; set; }

    // Position/size in WPF device-independent units.
    public double Left { get; set; } = 120;
    public double Top { get; set; } = 40;
    public double Width { get; set; } = 380;
    public double Height { get; set; } = 260;

    public bool RolledUp { get; set; }
    public bool Locked { get; set; }

    public string TintColor { get; set; } = "#1E1E1E";
    /// <summary>Background opacity 0..1.</summary>
    public double Opacity { get; set; } = 0.55;

    public SortMode Sort { get; set; } = SortMode.Manual;

    /// <summary>Shell parsing names (normally full file paths) of the items in a Standard fence.</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>New desktop files with these extensions are captured automatically ("*" = anything unclaimed).</summary>
    public List<string> AutoCaptureExtensions { get; set; } = new();
    public bool CaptureFolders { get; set; }
}

public sealed class AppSettings
{
    public bool HideIconsOnDesktop { get; set; } = true;
    /// <summary>Also hide icons on the shared Public Desktop (needs a UAC prompt).</summary>
    public bool HidePublicDesktopIcons { get; set; } = true;
    public bool DoubleClickDesktopToToggle { get; set; } = true;
    public bool SnapToEdges { get; set; } = true;
    public int IconSize { get; set; } = 48;
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<FenceConfig> Fences { get; set; } = new();

    /// <summary>Desktop items whose visibility we changed, so we only ever un-hide what we hid.</summary>
    public List<string> HiddenByUs { get; set; } = new();
}
