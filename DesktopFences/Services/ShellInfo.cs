using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopFences.Interop;

namespace DesktopFences.Services;

/// <summary>Display names and icons/thumbnails exactly as Explorer shows them.</summary>
public static class ShellInfo
{
    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Virtual shell items such as "::{645FF040-...}" (Recycle Bin).</summary>
    public static bool IsShellGuid(string p) => p.StartsWith("::{", StringComparison.Ordinal);

    public static bool Exists(string p) => IsShellGuid(p) || File.Exists(p) || Directory.Exists(p);

    public static void Invalidate(string p)
    {
        NameCache.Remove(p);
        var prefix = p + "|";
        var stale = new List<string>();
        foreach (var k in IconCache.Keys)
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) stale.Add(k);
        foreach (var k in stale) IconCache.Remove(k);
    }

    public static string GetDisplayName(string p)
    {
        if (NameCache.TryGetValue(p, out var n)) return n;
        n = TryShellName(p) ?? FallbackName(p);
        if (Exists(p)) NameCache[p] = n;
        return n;
    }

    static string FallbackName(string p)
    {
        var f = Path.GetFileName(p.TrimEnd('\\'));
        if (string.IsNullOrEmpty(f)) return p;
        var ext = Path.GetExtension(f);
        return ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(f) : f;
    }

    static IShellItem? CreateItem(string p)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            return Native.SHCreateItemFromParsingName(p, IntPtr.Zero, ref iid, out var o) == 0 ? o as IShellItem : null;
        }
        catch { return null; }
    }

    static string? TryShellName(string p)
    {
        var item = CreateItem(p);
        if (item == null) return null;
        try
        {
            item.GetDisplayName(Native.SIGDN_NORMALDISPLAY, out var ptr);
            var s = Marshal.PtrToStringUni(ptr);
            Marshal.FreeCoTaskMem(ptr);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch { return null; }
        finally { Marshal.ReleaseComObject(item); }
    }

    public static bool TryGetCachedIcon(string p, int px, out ImageSource? icon) =>
        IconCache.TryGetValue($"{p}|{px}", out icon);

    public static ImageSource? GetIcon(string p, int px)
    {
        var key = $"{p}|{px}";
        if (IconCache.TryGetValue(key, out var cached)) return cached;

        ImageSource? result = null;
        var item = CreateItem(p);
        if (item != null)
        {
            try
            {
                if (item is IShellItemImageFactory factory)
                {
                    var size = new Native.SIZE { cx = px, cy = px };
                    // 0 = SIIGBF_RESIZETOFIT (thumbnail if available, else icon); 4 = SIIGBF_ICONONLY
                    if (factory.GetImage(size, 0, out var hbmp) != 0 || hbmp == IntPtr.Zero)
                        factory.GetImage(size, 4, out hbmp);
                    if (hbmp != IntPtr.Zero)
                    {
                        try { result = ToBitmapSource(hbmp); }
                        finally { Native.DeleteObject(hbmp); }
                    }
                }
            }
            catch (Exception ex) { Log.Error($"Icon for {p}", ex); }
            finally { Marshal.ReleaseComObject(item); }
        }

        if (result != null || Exists(p)) IconCache[key] = result;
        return result;
    }

    static BitmapSource ToBitmapSource(IntPtr hbmp)
    {
        int dsSize = Marshal.SizeOf<Native.DIBSECTION>();
        if (Native.GetObject(hbmp, dsSize, out var ds) == dsSize && ds.dsBm.bmBitsPixel == 32)
        {
            // GetObject can't tell top-down from bottom-up DIBs, so let GetDIBits hand us a
            // top-down 32-bit copy (alpha bytes are preserved for same-depth copies).
            int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight), stride = w * 4;
            var px = new byte[stride * h];
            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            try
            {
                if (Native.GetDIBits(dc, hbmp, 0, (uint)h, px, ref bmi, 0) == 0)
                    goto fallback;
            }
            finally { Native.ReleaseDC(IntPtr.Zero, dc); }

            bool anyAlpha = false, premultiplied = true;
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (a != 0) anyAlpha = true;
                if (px[i] > a || px[i + 1] > a || px[i + 2] > a) premultiplied = false;
            }
            var fmt = !anyAlpha ? PixelFormats.Bgr32 : premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;
            var bs = BitmapSource.Create(w, h, 96, 96, fmt, null, px, stride);
            bs.Freeze();
            return bs;
        }

        fallback:
        var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        fallback.Freeze();
        return fallback;
    }

    /// <summary>Turns a "Shell IDList Array" drag payload into parsing names (paths or ::{GUID}).</summary>
    public static List<string> ParseIdListArray(object? data)
    {
        var result = new List<string>();
        if (data is not MemoryStream ms) return result;
        var bytes = ms.ToArray();
        if (bytes.Length < 8) return result;

        IntPtr mem = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            int count = Marshal.ReadInt32(mem, 0);
            int parentOffset = Marshal.ReadInt32(mem, 4);
            IntPtr parent = mem + parentOffset;
            for (int i = 1; i <= count; i++)
            {
                int off = Marshal.ReadInt32(mem, 4 * (i + 1));
                IntPtr abs = Native.ILCombine(parent, mem + off);
                if (abs == IntPtr.Zero) continue;
                try
                {
                    if (Native.SHGetNameFromIDList(abs, Native.SIGDN_DESKTOPABSOLUTEPARSING, out var namePtr) == 0)
                    {
                        var name = Marshal.PtrToStringUni(namePtr);
                        Marshal.FreeCoTaskMem(namePtr);
                        if (!string.IsNullOrEmpty(name)) result.Add(name);
                    }
                }
                finally { Native.ILFree(abs); }
            }
        }
        catch (Exception ex) { Log.Error("Parsing IDList", ex); }
        finally { Marshal.FreeHGlobal(mem); }
        return result;
    }
}
