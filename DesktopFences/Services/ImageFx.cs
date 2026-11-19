using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopFences.Services;

/// <summary>Small pixel effects for icons (tint, greyscale) and colour-space helpers.</summary>
public static class ImageFx
{
    delegate void PixelOp(ref byte b, ref byte g, ref byte r);

    static ImageSource? Map(ImageSource? src, PixelOp op)
    {
        if (src is not BitmapSource bmp) return src;
        try
        {
            BitmapSource conv = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);
            for (int i = 0; i + 3 < px.Length; i += 4)
                op(ref px[i], ref px[i + 1], ref px[i + 2]);
            var result = BitmapSource.Create(w, h, bmp.DpiX, bmp.DpiY, PixelFormats.Bgra32, null, px, stride);
            result.Freeze();
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Icon effect", ex);
            return src;
        }
    }

    static double Luma(byte b, byte g, byte r) => (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
    static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>Recolours an icon in shades of <paramref name="tint"/>, keeping its light and shadow.</summary>
    public static ImageSource? Tint(ImageSource? src, Color tint) => Map(src, (ref byte b, ref byte g, ref byte r) =>
    {
        double l = 0.25 + 0.95 * Luma(b, g, r);
        // 85% tint, 15% original so icons stay recognisable.
        r = Clamp(0.85 * tint.R * l * 1.15 + 0.15 * r);
        g = Clamp(0.85 * tint.G * l * 1.15 + 0.15 * g);
        b = Clamp(0.85 * tint.B * l * 1.15 + 0.15 * b);
    });

    public static ImageSource? Greyscale(ImageSource? src) => Map(src, (ref byte b, ref byte g, ref byte r) =>
    {
        byte y = Clamp(Luma(b, g, r) * 255);
        r = g = b = y;
    });

    // ---------------------------------------------------------------- HSV

    /// <summary>Hue 0–360, saturation and value 0–1.</summary>
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(Clamp((r + m) * 255), Clamp((g + m) * 255), Clamp((b + m) * 255));
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
