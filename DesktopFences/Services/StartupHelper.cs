using System;
using Microsoft.Win32;

namespace DesktopFences.Services;

public static class StartupHelper
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DesktopFences";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) k.SetValue(ValueName, Command);
            else k.DeleteValue(ValueName, false);
        }
        catch (Exception ex)
        {
            Log.Error("Startup registration", ex);
        }
    }

    /// <summary>Keeps the Run entry pointing at the current exe if the user moved it.</summary>
    public static void RefreshPathIfEnabled()
    {
        if (IsEnabled) Set(true);
    }
}
