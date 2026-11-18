using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using DesktopFences.Interop;

namespace DesktopFences.Services;

public static class ShellActions
{
    public static void Open(string p)
    {
        try
        {
            if (ShellInfo.IsShellGuid(p))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:" + p) { UseShellExecute = true });
                return;
            }
            var psi = new ProcessStartInfo(p) { UseShellExecute = true };
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* user cancelled (UAC etc.) */ }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open \"{p}\".\n\n{ex.Message}", "Desktop Fences",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Open folder", ex); }
    }

    /// <summary>Sends files to the Recycle Bin using the normal Explorer behaviour (undo, confirmations).</summary>
    public static void Recycle(IEnumerable<string> paths, IntPtr owner)
    {
        var list = paths.Where(p => !ShellInfo.IsShellGuid(p) && (File.Exists(p) || Directory.Exists(p))).ToList();
        if (list.Count == 0) return;
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = Native.FO_DELETE,
            pFrom = string.Join("\0", list) + "\0\0",
            fFlags = Native.FOF_ALLOWUNDO,
        };
        Native.SHFileOperation(ref op);
    }

    /// <summary>Moves or copies files into a folder with the standard Explorer progress/conflict UI.</summary>
    public static bool Transfer(IEnumerable<string> paths, string destFolder, bool move, IntPtr owner)
    {
        var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (list.Count == 0) return false;
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = move ? Native.FO_MOVE : Native.FO_COPY,
            pFrom = string.Join("\0", list) + "\0\0",
            pTo = destFolder + "\0\0",
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR),
        };
        return Native.SHFileOperation(ref op) == 0 && op.fAnyOperationsAborted == 0;
    }
}
