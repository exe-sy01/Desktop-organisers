using System.Windows.Media;
using WF = System.Windows.Forms;

namespace DesktopFences.Services;

/// <summary>Eyedropper: shows a frozen screenshot of all monitors and returns the colour the user clicks (Esc cancels).</summary>
public static class ScreenColorPicker
{
    public static Color? Pick()
    {
        var bounds = WF.SystemInformation.VirtualScreen;
        using var shot = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var g = System.Drawing.Graphics.FromImage(shot))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

        Color? result = null;
        using var form = new WF.Form
        {
            FormBorderStyle = WF.FormBorderStyle.None,
            StartPosition = WF.FormStartPosition.Manual,
            Bounds = bounds,
            TopMost = true,
            ShowInTaskbar = false,
            Cursor = WF.Cursors.Cross,
            BackgroundImage = shot,
            BackgroundImageLayout = WF.ImageLayout.None,
            KeyPreview = true,
        };
        var tip = new WF.Label
        {
            Text = "  Click a colour  (Esc to cancel)  ",
            AutoSize = true,
            BackColor = System.Drawing.Color.FromArgb(32, 32, 32),
            ForeColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Segoe UI", 10),
            Padding = new WF.Padding(6),
        };
        form.Controls.Add(tip);
        form.Load += (_, _) =>
        {
            var primary = WF.Screen.PrimaryScreen!.Bounds;
            tip.Location = new System.Drawing.Point(primary.Left - bounds.Left + 20, primary.Top - bounds.Top + 20);
        };
        form.MouseMove += (_, e) =>
        {
            if (e.X < 0 || e.Y < 0 || e.X >= shot.Width || e.Y >= shot.Height) return;
            var c = shot.GetPixel(e.X, e.Y);
            tip.BackColor = c;
            tip.ForeColor = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 140 ? System.Drawing.Color.Black : System.Drawing.Color.White;
        };
        form.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left && e.X >= 0 && e.Y >= 0 && e.X < shot.Width && e.Y < shot.Height)
            {
                var c = shot.GetPixel(e.X, e.Y);
                result = Color.FromRgb(c.R, c.G, c.B);
            }
            form.Close();
        };
        form.KeyDown += (_, e) => { if (e.KeyCode == WF.Keys.Escape) form.Close(); };
        form.ShowDialog();
        return result;
    }
}
