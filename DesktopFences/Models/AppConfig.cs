using System;
using System.Collections.Generic;

namespace DesktopFences.Models;

public enum FenceKind { Standard, Portal }

public enum SortMode { Manual, Name, Type, DateModified }

/// <summary>Where a fence's title bar sits. Auto = at the bottom when the fence is in the lower half of its screen.</summary>
public enum TitlePosition { Auto, Top, Bottom }

public enum TextColorMode { Auto, White, Black }

public enum TitleAlignment { Left, Center, Right }

/// <summary>When fence title bars are shown.</summary>
public enum TitleBarMode { Always, OnHover, Never }

public enum TabStyle { Segmented, Flat, Rounded, MenuOnly }

public enum IconTintSource { FenceColor, Custom }

public enum RuleKind { FileType, NameContains, NameStartsWith, NameEndsWith, ShortcutTarget, CreatedBetweenHours, CreatedOnDays, SizeLargerThan }

public enum FileCategory { Folders, Documents, Images, MusicAndVideo, Downloads, WebLinks, ProgramShortcuts }

/// <summary>Sends new desktop items that match into a fence. Checked in list order; the first match wins.</summary>
public sealed class SortRule
{
    public bool Enabled { get; set; } = true;
    public RuleKind Kind { get; set; } = RuleKind.FileType;
    /// <summary>
    /// FileType: a FileCategory name. Name rules / ShortcutTarget: the text to look for.
    /// CreatedBetweenHours: "9-17". CreatedOnDays: "Sat,Sun". SizeLargerThan: megabytes.
    /// </summary>
    public string Value { get; set; } = "";
    public string TargetFenceId { get; set; } = "";
}

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

    public TitlePosition TitlePosition { get; set; } = TitlePosition.Auto;
    /// <summary>Where the title bar ended up last time (resolved from TitlePosition), so a rolled-up fence reopens in place.</summary>
    public bool TitleAtBottom { get; set; }
    public TextColorMode TextColor { get; set; } = TextColorMode.Auto;

    public string TintColor { get; set; } = "#1E1E1E";
    /// <summary>Background opacity 0..1.</summary>
    public double Opacity { get; set; } = 0.55;

    public SortMode Sort { get; set; } = SortMode.Manual;

    /// <summary>Shell parsing names (normally full file paths) of the items in a Standard fence.</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>
    /// New desktop items matching these rules are captured automatically: extensions (".pdf"), name
    /// patterns with wildcards ("*invoice*"), or "*" for anything no other fence claims.
    /// </summary>
    public List<string> AutoCaptureExtensions { get; set; } = new();
    public bool CaptureFolders { get; set; }

    /// <summary>Fences sharing a GroupId are shown as tabs of one fence (null = standalone).</summary>
    public string? GroupId { get; set; }
    /// <summary>The tab currently shown in its group.</summary>
    public bool ActiveTab { get; set; }
}

public sealed class AppSettings
{
    public bool HideIconsOnDesktop { get; set; } = true;
    /// <summary>Also hide icons on the shared Public Desktop (needs a UAC prompt).</summary>
    public bool HidePublicDesktopIcons { get; set; } = true;
    public bool DoubleClickDesktopToToggle { get; set; } = true;
    public bool SnapToEdges { get; set; } = true;
    public int IconSize { get; set; } = 48;

    public TitleAlignment TitleAlignment { get; set; } = TitleAlignment.Center;
    public bool RoundedCorners { get; set; } = true;
    public bool ShowIconLabels { get; set; } = true;
    /// <summary>Fence opacity while the mouse isn't over it (1 = no fading).</summary>
    public double IdleOpacity { get; set; } = 1.0;
    public bool SingleClickToOpen { get; set; }
    /// <summary>Ctrl+Alt+H hides/shows fences, Ctrl+Alt+F brings them in front of windows ("peek").</summary>
    public bool HotkeysEnabled { get; set; } = true;
    /// <summary>How long rolling a fence up or down takes, in milliseconds (0 = instant).</summary>
    public int RollAnimationMs { get; set; } = 300;

    // Personalization
    public TitleBarMode TitleBars { get; set; } = TitleBarMode.Always;
    /// <summary>Fences fade into the wallpaper (see-through, grey icons) until you point at them.</summary>
    public bool BlendWithWallpaper { get; set; }
    public bool TintIcons { get; set; }
    public IconTintSource TintSource { get; set; } = IconTintSource.FenceColor;
    public string IconTintColor { get; set; } = "#4A90E2";
    public bool AnimateIconMoves { get; set; } = true;
    public bool HideScrollbarsUntilHover { get; set; } = true;

    // Tabs
    public bool EnableTabs { get; set; } = true;
    public bool ConfirmNewTabs { get; set; } = true;
    public bool SwitchTabsOnHover { get; set; }
    public TabStyle TabStyle { get; set; } = TabStyle.Segmented;

    // Rules
    /// <summary>Where new desktop items that match no rule go ("" = they stay on the desktop).</summary>
    public string DefaultFenceId { get; set; } = "";

    // Layout
    public bool DailyBackups { get; set; } = true;
    public bool SnapKeepSpacing { get; set; } = true;
    public int SnapSpacing { get; set; } = 8;
    public bool SnapSizeToIcons { get; set; }
    public bool SnapAlignEdges { get; set; } = true;
    /// <summary>Space icons inside fences like icons on the desktop (Windows' icon spacing).</summary>
    public bool MatchDesktopIconSpacing { get; set; }
}

/// <summary>A saved arrangement of fences (position, size, roll-up) that can be restored later.</summary>
public sealed class LayoutSnapshot
{
    public string Name { get; set; } = "";
    public DateTime Saved { get; set; }
    /// <summary>Taken by the daily backup rather than by hand.</summary>
    public bool Automatic { get; set; }
    public List<FenceLayout> Fences { get; set; } = new();
}

public sealed class FenceLayout
{
    public string Id { get; set; } = "";
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool RolledUp { get; set; }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<FenceConfig> Fences { get; set; } = new();

    /// <summary>Desktop items whose visibility we changed, so we only ever un-hide what we hid.</summary>
    public List<string> HiddenByUs { get; set; } = new();

    public List<LayoutSnapshot> Layouts { get; set; } = new();

    public List<SortRule> Rules { get; set; } = new();
}
