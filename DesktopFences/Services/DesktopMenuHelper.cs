using System;
using Microsoft.Win32;

namespace DesktopFences.Services;

/// <summary>
/// Adds "Desktop Fences settings" to the right-click menu of the desktop background (per user, no admin
/// needed). On Windows 11 it appears under "Show more options" (or with Shift+right-click).
/// </summary>
public static class DesktopMenuHelper
{
    const string KeyPath = @"Software\Classes\DesktopBackground\Shell\DesktopFencesSettings";
    public const string Label = "Desktop Fences settings";

    public static bool IsRegistered
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(KeyPath + @"\command");
                return k?.GetValue(null) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Adds (or refreshes the exe path of) the menu entry, or removes it.</summary>
    public static void Set(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
                return;
            }
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            k.SetValue("MUIVerb", Label);
            k.SetValue("Icon", $"\"{exe}\",0");
            k.SetValue("Position", "Bottom");
            using var cmd = k.CreateSubKey("command");
            cmd.SetValue(null, $"\"{exe}\" --settings");
        }
        catch (Exception ex)
        {
            Log.Error("Desktop menu registration", ex);
        }
    }
}
