using System.Drawing.Text;

namespace ChessKit
{
    /// <summary>
    /// Shared per-control DPI scaling helpers. The app runs PerMonitorV2-aware
    /// (Program sets HighDpiMode.PerMonitorV2), so <see cref="Control.DeviceDpi"/>
    /// reflects the ACTUAL DPI of the monitor the control is on
    /// (96 = 100%, 120 = 125%, 144 = 150%, 168 = 175%, 192 = 200%).
    ///
    /// Use these to scale layout literals, paddings, and custom GDI coordinates that
    /// WinForms auto-scaling does not touch (manual <c>e.Graphics</c> drawing, forms
    /// with <c>AutoScaleMode.None</c>, hand-computed control bounds). Mirrors the
    /// pattern proven in <see cref="SettingsToolbarForm"/>.
    ///
    /// IMPORTANT: at 100% (96 DPI) the factor is exactly 1.0, so every Scale() call is
    /// an identity no-op — wrapping a literal can never change 100% rendering; it only
    /// corrects higher scalings. That makes adopting these helpers strictly safe.
    /// </summary>
    internal static class Dpi
    {
        /// <summary>DeviceDpi / 96, floored at 1.0. Safe if <paramref name="c"/> is null.</summary>
        public static float Factor(Control? c)
        {
            int dpi = 96;
            try { dpi = System.Math.Max(96, c?.DeviceDpi ?? 96); } catch { }
            return dpi / 96f;
        }

        public static int Scale(Control? c, int logical)
            => (int)System.Math.Round(logical * Factor(c), System.MidpointRounding.AwayFromZero);

        public static float Scale(Control? c, float logical) => logical * Factor(c);

        public static Size Scale(Control? c, Size s)
            => new(System.Math.Max(1, Scale(c, s.Width)), System.Math.Max(1, Scale(c, s.Height)));

        public static Point Scale(Control? c, Point p) => new(Scale(c, p.X), Scale(c, p.Y));

        public static Padding Scale(Control? c, Padding p)
            => new(Scale(c, p.Left), Scale(c, p.Top), Scale(c, p.Right), Scale(c, p.Bottom));

        /// <summary>
        /// A 1x1 <see cref="Graphics"/> whose resolution matches the control's
        /// DeviceDpi, so text measured for layout equals what OnPaint renders at this
        /// DPI (a default Bitmap Graphics is 96 DPI and under-measures point-size fonts
        /// at high DPI — the classic cause of clipped/ellipsized labels). The caller
        /// disposes BOTH the returned Graphics and the out bitmap.
        /// </summary>
        public static Graphics CreateMeasureGraphics(Control? c, out Bitmap bmp)
        {
            int dpi = 96;
            try { dpi = System.Math.Max(96, c?.DeviceDpi ?? 96); } catch { }
            bmp = new Bitmap(1, 1);
            bmp.SetResolution(dpi, dpi);
            var g = Graphics.FromImage(bmp);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            return g;
        }
    }
}
