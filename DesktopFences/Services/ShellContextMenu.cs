using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DesktopFences.Interop;

namespace DesktopFences.Services;

/// <summary>Shows the real Explorer right-click menu for files, plus our own extra commands.</summary>
public static class ShellContextMenu
{
    public sealed record Extra(string Text, Action Action, bool Enabled = true);

    const uint CmdFirst = 1;
    const uint CmdLast = 0x6FFF;
    const uint CustomBase = 0x7000;

    const uint CMF_NORMAL = 0x0, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    const uint GCS_VERBW = 0x4;
    const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
    const uint CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;

    /// <param name="onVerb">Called with the chosen shell verb (and the items it applies to) just before it runs;
    /// the returned action, if any, runs right after. For a verb-less command inside a submenu, the verb of
    /// the submenu's own entry is passed (e.g. "openas" for items under "Open with").</param>
    /// <param name="restrictItem">If any item the menu actually covers matches, only commands accepted by
    /// <paramref name="keepVerb"/> are shown.</param>
    public static void Show(Window owner, IReadOnlyList<string> paths, IReadOnlyList<Extra> extras, Action<string>? onRename,
        Func<string?, IReadOnlyList<string>, Action?>? onVerb = null,
        Func<string, bool>? restrictItem = null, Func<string?, bool>? keepVerb = null)
    {
        IntPtr hwnd = new WindowInteropHelper(owner).Handle;
        Native.GetCursorPos(out var pt);

        var pidls = new List<IntPtr>();
        IShellFolder? folder = null;
        IContextMenu? cm = null;
        IContextMenu2? cm2 = null;
        IContextMenu3? cm3 = null;
        IntPtr hmenu = Native.CreatePopupMenu();
        HwndSource? source = HwndSource.FromHwnd(hwnd);
        HwndSourceHook? hook = null;
        var usable = new List<string>();

        try
        {
            usable = paths.Where(ShellInfo.Exists).ToList();
            if (usable.Count > 0)
            {
                // One IContextMenu can only cover items that share a parent folder.
                var parent0 = ParentKey(usable[0]);
                usable = usable.Where(p => string.Equals(ParentKey(p), parent0, StringComparison.OrdinalIgnoreCase)).ToList();
                cm = CreateMenu(hwnd, usable, pidls, out folder);
            }

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (cm != null)
            {
                cm.QueryContextMenu(hmenu, 0, CmdFirst, CmdLast, CMF_NORMAL | CMF_CANRENAME | (shift ? CMF_EXTENDEDVERBS : 0));
                cm2 = cm as IContextMenu2;
                cm3 = cm as IContextMenu3;
                // Decide from the items this menu really acts on (only those sharing the first item's folder).
                if (keepVerb != null && restrictItem != null && usable.Any(restrictItem)) Filter(hmenu, cm, keepVerb);
            }

            if (extras.Count > 0)
            {
                if (Native.GetMenuItemCount(hmenu) > 0)
                    Native.AppendMenu(hmenu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                for (int i = 0; i < extras.Count; i++)
                    Native.AppendMenu(hmenu, Native.MF_STRING | (extras[i].Enabled ? 0 : Native.MF_GRAYED),
                        (UIntPtr)(CustomBase + (uint)i), extras[i].Text);
            }

            // Owner-drawn submenus ("Open with", "Send to", …) need these messages forwarded.
            hook = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg is Native.WM_INITMENUPOPUP or Native.WM_DRAWITEM or Native.WM_MEASUREITEM or Native.WM_MENUCHAR)
                {
                    try
                    {
                        if (cm3 != null)
                        {
                            if (cm3.HandleMenuMsg2((uint)msg, w, l, out var res) == 0) { handled = true; return res; }
                        }
                        else if (cm2 != null && msg != Native.WM_MENUCHAR)
                        {
                            if (cm2.HandleMenuMsg((uint)msg, w, l) == 0) { handled = true; return IntPtr.Zero; }
                        }
                    }
                    catch { /* buggy shell extension – ignore */ }
                }
                return IntPtr.Zero;
            };
            source?.AddHook(hook);

            Native.SetForegroundWindow(hwnd);
            uint cmd = Native.TrackPopupMenuEx(hmenu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);

            source?.RemoveHook(hook);
            hook = null;

            if (cmd == 0) return;
            if (cmd >= CustomBase)
            {
                extras[(int)(cmd - CustomBase)].Action();
                return;
            }
            if (cm == null) return;

            uint offset = cmd - CmdFirst;
            var verb = GetVerb(cm, offset);
            if (verb == null && FindParentPopupId(hmenu, cmd) is uint parentId && parentId >= CmdFirst && parentId <= CmdLast)
                verb = GetVerb(cm, parentId - CmdFirst);
            if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
            {
                onRename?.Invoke(usable[0]);
                return;
            }
            var after = onVerb?.Invoke(verb, usable);
            try
            {
                Invoke(cm, hwnd, offset, pt, ShellInfo.IsShellGuid(usable[0]) ? null : Path.GetDirectoryName(usable[0]));
            }
            finally { after?.Invoke(); }
        }
        catch (Exception ex)
        {
            Log.Error("Context menu", ex);
        }
        finally
        {
            if (hook != null) source?.RemoveHook(hook);
            Native.DestroyMenu(hmenu);
            if (cm != null) Marshal.ReleaseComObject(cm);
            if (folder != null) Marshal.ReleaseComObject(folder);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }

    static string ParentKey(string p) => ShellInfo.IsShellGuid(p) ? "::" : Path.GetDirectoryName(p) ?? "";

    static IContextMenu? CreateMenu(IntPtr hwnd, List<string> paths, List<IntPtr> pidls, out IShellFolder? folder)
    {
        folder = null;
        var children = new List<IntPtr>();
        foreach (var p in paths)
        {
            if (Native.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) continue;
            pidls.Add(pidl);
            var iidFolder = typeof(IShellFolder).GUID;
            if (Native.SHBindToParent(pidl, ref iidFolder, out var psf, out var child) != 0) continue;
            if (folder == null) folder = (IShellFolder)Marshal.GetObjectForIUnknown(psf);
            Marshal.Release(psf);
            children.Add(child);
        }
        if (folder == null || children.Count == 0) return null;

        var iidMenu = typeof(IContextMenu).GUID;
        if (folder.GetUIObjectOf(hwnd, (uint)children.Count, children.ToArray(), ref iidMenu, IntPtr.Zero, out var pcm) != 0
            || pcm == IntPtr.Zero)
            return null;
        var cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);
        Marshal.Release(pcm);
        return cm;
    }

    static Native.MENUITEMINFO ItemInfo(IntPtr hmenu, int pos)
    {
        var mii = new Native.MENUITEMINFO
        {
            cbSize = Marshal.SizeOf<Native.MENUITEMINFO>(),
            fMask = Native.MIIM_ID | Native.MIIM_SUBMENU | Native.MIIM_FTYPE,
        };
        Native.GetMenuItemInfo(hmenu, (uint)pos, true, ref mii);
        return mii;
    }

    static bool IsSeparator(Native.MENUITEMINFO mii) => (mii.fType & Native.MFT_SEPARATOR) != 0;

    /// <summary>
    /// Keeps only commands whose verb passes <paramref name="keepVerb"/>. A submenu is kept only if its own
    /// entry has an accepted verb (e.g. "Open with"); verb-less ones like "Send to" are removed.
    /// </summary>
    static void Filter(IntPtr hmenu, IContextMenu cm, Func<string?, bool> keepVerb)
    {
        string? VerbOf(uint id) => id >= CmdFirst && id <= CmdLast ? GetVerb(cm, id - CmdFirst) : null;

        for (int i = Native.GetMenuItemCount(hmenu) - 1; i >= 0; i--)
        {
            var mii = ItemInfo(hmenu, i);
            if (IsSeparator(mii)) continue;
            if (!keepVerb(VerbOf(mii.wID))) Native.DeleteMenu(hmenu, (uint)i, Native.MF_BYPOSITION);
        }
        // Tidy separators left at the ends or doubled up.
        bool prevSep = true;
        for (int i = 0; i < Native.GetMenuItemCount(hmenu); i++)
        {
            bool sep = IsSeparator(ItemInfo(hmenu, i));
            if (sep && prevSep) { Native.DeleteMenu(hmenu, (uint)i, Native.MF_BYPOSITION); i--; continue; }
            prevSep = sep;
        }
        int last = Native.GetMenuItemCount(hmenu) - 1;
        if (last >= 0 && IsSeparator(ItemInfo(hmenu, last)))
            Native.DeleteMenu(hmenu, (uint)last, Native.MF_BYPOSITION);
    }

    /// <summary>Id of the submenu entry that contains command <paramref name="cmd"/> (null if it's top-level).</summary>
    static uint? FindParentPopupId(IntPtr hmenu, uint cmd, uint? parentId = null, int depth = 0)
    {
        if (depth > 8) return null;
        for (int i = 0; i < Native.GetMenuItemCount(hmenu); i++)
        {
            var mii = ItemInfo(hmenu, i);
            if (mii.hSubMenu != IntPtr.Zero)
            {
                if (FindParentPopupId(mii.hSubMenu, cmd, mii.wID, depth + 1) is uint found) return found;
            }
            else if (mii.wID == cmd)
            {
                return parentId;
            }
        }
        return null;
    }

    static string? GetVerb(IContextMenu cm, uint offset)
    {
        IntPtr buf = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt16(buf, 0, 0);
            return cm.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buf, 512) == 0
                ? Marshal.PtrToStringUni(buf) : null;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static void Invoke(IContextMenu cm, IntPtr hwnd, uint offset, Native.POINT pt, string? dir)
    {
        IntPtr dirA = dir != null ? Marshal.StringToHGlobalAnsi(dir) : IntPtr.Zero;
        IntPtr dirW = dir != null ? Marshal.StringToHGlobalUni(dir) : IntPtr.Zero;
        try
        {
            uint mask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mask |= CMIC_MASK_SHIFT_DOWN;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mask |= CMIC_MASK_CONTROL_DOWN;
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = mask,
                hwnd = hwnd,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectory = dirA,
                lpDirectoryW = dirW,
                nShow = 1,
                ptInvoke = pt,
            };
            cm.InvokeCommand(ref info);
        }
        finally
        {
            if (dirA != IntPtr.Zero) Marshal.FreeHGlobal(dirA);
            if (dirW != IntPtr.Zero) Marshal.FreeHGlobal(dirW);
        }
    }
}
