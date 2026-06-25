using System.Drawing.Drawing2D;
using System.Globalization;

namespace ChessKit
{
    /// <summary>
    /// Tiny SVG-icon renderer for Lucide icons. Lucide icons all use a 24×24
    /// viewBox and stroke-only paths with stroke-width 2, round caps, round
    /// joins. We embed each icon's path data as a string constant and render
    /// it by parsing the path commands into a GraphicsPath, scaling to the
    /// requested pixel size, and stroking with the requested color.
    ///
    /// Why not just use the Svg NuGet package? We only need a tiny subset of
    /// SVG (path commands + stroke), and pulling a 200KB dependency for that
    /// is overkill. This file is ~150 lines and produces crisp icons at any
    /// DPI — which is what we couldn't get from Unicode glyphs.
    ///
    /// Icons are sourced from lucide.dev (ISC license, free for any use).
    /// </summary>
    internal static class IconRenderer
    {
        // Lucide icons are designed on a 24×24 grid. All paths below assume
        // this coordinate space; we scale to the destination rectangle.
        private const float LucideViewBox = 24f;

        // Icon path data extracted from lucide.dev. Each icon may have
        // multiple sub-paths (drawn with the same stroke).
        // Source: https://github.com/lucide-icons/lucide
        public static class Paths
        {
            // menu (hamburger)
            public static readonly string[] Menu = {
                "M4 5h16",
                "M4 12h16",
                "M4 19h16"
            };

            // x (close)
            public static readonly string[] Close = {
                "M18 6 6 18",
                "M6 6l12 12"
            };

            // chevron-up (collapse menu)
            public static readonly string[] ChevronUp = {
                "m18 15-6-6-6 6"
            };

            // flip-vertical-2 (flip board) — was used; replaced by ArrowUpDown
            // because the user wants simpler vertical arrows.
            public static readonly string[] FlipVertical = {
                "M4 3h6",
                "M4 21h6",
                "M14 3h6",
                "M14 21h6",
                "M5 9 2 12l3 3",
                "M19 9l3 3-3 3",
                "M2 12h20"
            };

            // arrow-up-down (flip board, simpler version)
            public static readonly string[] ArrowUpDown = {
                "m21 16-4 4-4-4",
                "M17 20V4",
                "m3 8 4-4 4 4",
                "M7 4v16"
            };

            // cpu (engine selector — distinct from generic settings gear)
            public static readonly string[] Cpu = {
                "M12 20v2",
                "M12 2v2",
                "M17 20v2",
                "M17 2v2",
                "M2 12h2",
                "M2 17h2",
                "M2 7h2",
                "M20 12h2",
                "M20 17h2",
                "M20 7h2",
                "M7 20v2",
                "M7 2v2",
                "M9 22h6a2 2 0 0 0 2-2v-1a1 1 0 0 1 1-1h1a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-1a1 1 0 0 1-1-1V5a2 2 0 0 0-2-2H9a2 2 0 0 0-2 2v1a1 1 0 0 1-1 1H5a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h1a1 1 0 0 1 1 1v1a2 2 0 0 0 2 2z",
                "M9 9h6v6H9z"
            };

            // gauge (debug HUD — live stats)
            public static readonly string[] Gauge = {
                "m12 14 4-4",
                "M3.34 19a10 10 0 1 1 17.32 0"
            };

            // settings (gear)
            public static readonly string[] Settings = {
                "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z",
                "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z"
            };

            // square-pen (analysis board — board with edit affordance)
            public static readonly string[] SquarePen = {
                "M12 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7",
                "M18.375 2.625a1 1 0 0 1 3 3l-9.013 9.014a2 2 0 0 1-.853.505l-2.873.84a.5.5 0 0 1-.62-.62l.84-2.873a2 2 0 0 1 .506-.852z"
            };

            // bar-chart-3 (eval bar)
            public static readonly string[] BarChart = {
                "M3 3v18h18",
                "M7 16V8",
                "M12 16v-5",
                "M17 16V4"
            };

            // list (engine lines)
            public static readonly string[] List = {
                "M8 6h13",
                "M8 12h13",
                "M8 18h13",
                "M3 6h.01",
                "M3 12h.01",
                "M3 18h.01"
            };

            // graduation-cap (coach)
            public static readonly string[] Coach = {
                "M21.42 10.922a1 1 0 0 0-.019-1.838L12.83 5.18a2 2 0 0 0-1.66 0L2.6 9.08a1 1 0 0 0 0 1.832l8.57 3.908a2 2 0 0 0 1.66 0z",
                "M22 10v6",
                "M6 12.5V16a6 3 0 0 0 12 0v-3.5"
            };
        }

        /// <summary>
        /// Render an icon (set of SVG path strings) into the given rectangle
        /// with the given stroke color. Stroke width auto-scales with size.
        ///
        /// <paramref name="dpiFactor"/> is the monitor DPI factor (DeviceDpi/96,
        /// e.g. 1.0 at 100%, 1.5 at 150%, 2.0 at 200%) of the control doing the
        /// drawing. Callers pass <c>Dpi.Factor(control)</c>; the default 1f keeps
        /// any caller that doesn't — and all 100% rendering — byte-identical. It
        /// scales the stroke-width clamp band so that on a high-DPI monitor, where
        /// the caller hands us a larger DPI-scaled rect, the stroke is no longer
        /// clipped to a fixed device-pixel ceiling and Lucide icons keep a
        /// consistent VISUAL weight across scalings instead of going wispy.
        /// </summary>
        public static void Draw(Graphics g, string[] paths, Rectangle rect, Color color, float dpiFactor = 1f)
        {
            if (paths == null || paths.Length == 0) return;

            // A DPI factor is always >= 1; guard a degenerate value so the band can
            // never collapse to a 0-width (invisible) stroke. At factor 1.0 this is
            // a no-op and the band below stays the exact original [1.2, 2.5].
            float f = dpiFactor < 1f ? 1f : dpiFactor;
            float scale = Math.Min(rect.Width, rect.Height) / LucideViewBox;
            // Stroke width: Lucide draws at stroke-width 2 in 24-unit space. Scale
            // that and clamp to a sensible visual range. The clamp band itself
            // scales with the monitor DPI factor, so the [1.2, 2.5] device-pixel
            // limits become [1.2*f, 2.5*f] at high DPI and the (already DPI-scaled)
            // raw stroke is not prematurely clamped thinner than the icon.
            float strokeWidth = Math.Max(1.2f * f, Math.Min(2.5f * f, 2f * scale * 0.85f));

            var prevSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var pen = new Pen(color, strokeWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            // Translate so the 24×24 viewBox lands centered in rect.
            var prev = g.Transform.Clone();
            try
            {
                g.TranslateTransform(rect.X + (rect.Width - LucideViewBox * scale) / 2f,
                                     rect.Y + (rect.Height - LucideViewBox * scale) / 2f);
                g.ScaleTransform(scale, scale);

                foreach (var pathData in paths)
                {
                    try
                    {
                        using var gp = ParsePath(pathData);
                        g.DrawPath(pen, gp);
                    }
                    catch
                    {
                        // Defensive: a bad path shouldn't black out the
                        // toolbar. Skip this sub-path and keep going.
                    }
                }
            }
            finally
            {
                g.Transform = prev;
                g.SmoothingMode = prevSmoothing;
            }
        }

        // ------------------------------------------------------------------
        // SVG path parser. Supports: M m L l H h V v C c S s Q q T t Z z.
        // That covers everything Lucide uses. No arcs, no fills.
        // ------------------------------------------------------------------
        private static GraphicsPath ParsePath(string d)
        {
            var path = new GraphicsPath();
            float curX = 0, curY = 0;          // current point
            float startX = 0, startY = 0;      // subpath start
            float prevCtrlX = 0, prevCtrlY = 0;// for S/T smooth
            char prevCmd = ' ';
            int i = 0;
            int n = d.Length;

            while (i < n)
            {
                while (i < n && (d[i] == ' ' || d[i] == ',')) i++;
                if (i >= n) break;

                char c = d[i];
                bool isCmd = char.IsLetter(c);
                char cmd;
                if (isCmd) { cmd = c; i++; }
                else { cmd = ImplicitCommand(prevCmd); }

                bool relative = char.IsLower(cmd);
                char up = char.ToUpperInvariant(cmd);

                switch (up)
                {
                    case 'M':
                        {
                            float x = ReadNum(d, ref i);
                            float y = ReadNum(d, ref i);
                            if (relative) { x += curX; y += curY; }
                            curX = x; curY = y;
                            startX = x; startY = y;
                            path.StartFigure();
                            // Subsequent pairs after M are implicit L (handled by ImplicitCommand)
                            break;
                        }
                    case 'L':
                        {
                            float x = ReadNum(d, ref i);
                            float y = ReadNum(d, ref i);
                            if (relative) { x += curX; y += curY; }
                            path.AddLine(curX, curY, x, y);
                            curX = x; curY = y;
                            break;
                        }
                    case 'H':
                        {
                            float x = ReadNum(d, ref i);
                            if (relative) x += curX;
                            path.AddLine(curX, curY, x, curY);
                            curX = x;
                            break;
                        }
                    case 'V':
                        {
                            float y = ReadNum(d, ref i);
                            if (relative) y += curY;
                            path.AddLine(curX, curY, curX, y);
                            curY = y;
                            break;
                        }
                    case 'C':
                        {
                            float x1 = ReadNum(d, ref i);
                            float y1 = ReadNum(d, ref i);
                            float x2 = ReadNum(d, ref i);
                            float y2 = ReadNum(d, ref i);
                            float x = ReadNum(d, ref i);
                            float y = ReadNum(d, ref i);
                            if (relative) { x1 += curX; y1 += curY; x2 += curX; y2 += curY; x += curX; y += curY; }
                            path.AddBezier(curX, curY, x1, y1, x2, y2, x, y);
                            prevCtrlX = x2; prevCtrlY = y2;
                            curX = x; curY = y;
                            break;
                        }
                    case 'S':
                        {
                            // Smooth cubic: first control point is reflection of prev.
                            float x2 = ReadNum(d, ref i);
                            float y2 = ReadNum(d, ref i);
                            float x = ReadNum(d, ref i);
                            float y = ReadNum(d, ref i);
                            if (relative) { x2 += curX; y2 += curY; x += curX; y += curY; }
                            float x1, y1;
                            if (char.ToUpperInvariant(prevCmd) == 'C' || char.ToUpperInvariant(prevCmd) == 'S')
                            {
                                x1 = 2 * curX - prevCtrlX;
                                y1 = 2 * curY - prevCtrlY;
                            }
                            else { x1 = curX; y1 = curY; }
                            path.AddBezier(curX, curY, x1, y1, x2, y2, x, y);
                            prevCtrlX = x2; prevCtrlY = y2;
                            curX = x; curY = y;
                            break;
                        }
                    case 'Q':
                        {
                            float x1 = ReadNum(d, ref i);
                            float y1 = ReadNum(d, ref i);
                            float x = ReadNum(d, ref i);
                            float y = ReadNum(d, ref i);
                            if (relative) { x1 += curX; y1 += curY; x += curX; y += curY; }
                            // Convert quadratic to cubic for GraphicsPath
                            float c1x = curX + 2f / 3f * (x1 - curX);
                            float c1y = curY + 2f / 3f * (y1 - curY);
                            float c2x = x + 2f / 3f * (x1 - x);
                            float c2y = y + 2f / 3f * (y1 - y);
                            path.AddBezier(curX, curY, c1x, c1y, c2x, c2y, x, y);
                            prevCtrlX = x1; prevCtrlY = y1;
                            curX = x; curY = y;
                            break;
                        }
                    case 'T':
                        {
                            float x = ReadNum(d, ref i);
                            float y = ReadNum(d, ref i);
                            if (relative) { x += curX; y += curY; }
                            float x1, y1;
                            if (char.ToUpperInvariant(prevCmd) == 'Q' || char.ToUpperInvariant(prevCmd) == 'T')
                            {
                                x1 = 2 * curX - prevCtrlX;
                                y1 = 2 * curY - prevCtrlY;
                            }
                            else { x1 = curX; y1 = curY; }
                            float c1x = curX + 2f / 3f * (x1 - curX);
                            float c1y = curY + 2f / 3f * (y1 - curY);
                            float c2x = x + 2f / 3f * (x1 - x);
                            float c2y = y + 2f / 3f * (y1 - y);
                            path.AddBezier(curX, curY, c1x, c1y, c2x, c2y, x, y);
                            prevCtrlX = x1; prevCtrlY = y1;
                            curX = x; curY = y;
                            break;
                        }
                    case 'A':
                        {
                            // Elliptical arc: rx ry x-axis-rotation large-arc sweep x y
                            float rx = ReadNum(d, ref i);
                            float ry = ReadNum(d, ref i);
                            float xRot = ReadNum(d, ref i);
                            float largeArc = ReadFlag(d, ref i);
                            float sweep = ReadFlag(d, ref i);
                            float x = ReadNum(d, ref i);
                            float y = ReadNum(d, ref i);
                            if (relative) { x += curX; y += curY; }
                            AddArcAsBeziers(path, curX, curY, rx, ry, xRot,
                                            largeArc != 0, sweep != 0, x, y);
                            curX = x; curY = y;
                            break;
                        }
                    case 'Z':
                        {
                            path.AddLine(curX, curY, startX, startY);
                            path.CloseFigure();
                            curX = startX; curY = startY;
                            break;
                        }
                    default:
                        {
                            // Unknown command — bail out of this path rather than
                            // infinite-looping. Shouldn't happen for valid Lucide
                            // input, but safer than hanging the paint thread.
                            return path;
                        }
                }
                prevCmd = cmd;
            }
            return path;
        }

        private static char ImplicitCommand(char prev)
        {
            // After M, implicit command is L (m -> l).
            char up = char.ToUpperInvariant(prev);
            if (up == 'M') return char.IsUpper(prev) ? 'L' : 'l';
            return prev;
        }

        private static float ReadNum(string d, ref int i)
        {
            while (i < d.Length && (d[i] == ' ' || d[i] == ',')) i++;
            int start = i;
            // Number: optional sign, digits, optional dot+digits, optional e+exp
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            while (i < d.Length && (char.IsDigit(d[i]) || d[i] == '.')) i++;
            if (i < d.Length && (d[i] == 'e' || d[i] == 'E'))
            {
                i++;
                if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
                while (i < d.Length && char.IsDigit(d[i])) i++;
            }
            // Handle SVG quirk: -.5 after a number is a new number with no separator.
            // Already handled because '-' starts a new ReadNum cycle naturally.
            string token = d.Substring(start, i - start);
            return float.Parse(token, CultureInfo.InvariantCulture);
        }

        // SVG arc flags are single 0/1 digits with no separator required.
        private static float ReadFlag(string d, ref int i)
        {
            while (i < d.Length && (d[i] == ' ' || d[i] == ',')) i++;
            if (i < d.Length && (d[i] == '0' || d[i] == '1'))
            {
                char c = d[i++];
                return c == '1' ? 1f : 0f;
            }
            // Fallback to ReadNum if the producer included extra whitespace.
            return ReadNum(d, ref i);
        }

        /// <summary>
        /// Convert an SVG elliptical arc to a series of cubic Bezier curves
        /// and append them to the path. Algorithm follows the W3C SVG spec
        /// "elliptical arc implementation notes" (B.2.4).
        /// </summary>
        private static void AddArcAsBeziers(GraphicsPath path,
            float x1, float y1, float rx, float ry, float xRotDeg,
            bool largeArc, bool sweep, float x2, float y2)
        {
            // Degenerate cases
            if (x1 == x2 && y1 == y2) return;
            if (rx == 0 || ry == 0)
            {
                path.AddLine(x1, y1, x2, y2);
                return;
            }

            rx = Math.Abs(rx);
            ry = Math.Abs(ry);
            double phi = xRotDeg * Math.PI / 180.0;
            double cosPhi = Math.Cos(phi);
            double sinPhi = Math.Sin(phi);

            // Step 1: compute (x1', y1') - midpoint frame
            double dx = (x1 - x2) / 2.0;
            double dy = (y1 - y2) / 2.0;
            double x1p = cosPhi * dx + sinPhi * dy;
            double y1p = -sinPhi * dx + cosPhi * dy;

            // Step 2: scale rx, ry up if necessary
            double rx2 = rx * rx;
            double ry2 = ry * ry;
            double x1p2 = x1p * x1p;
            double y1p2 = y1p * y1p;
            double radCheck = x1p2 / rx2 + y1p2 / ry2;
            if (radCheck > 1.0)
            {
                double s = Math.Sqrt(radCheck);
                rx *= (float)s;
                ry *= (float)s;
                rx2 = rx * rx;
                ry2 = ry * ry;
            }

            // Step 3: compute (cx', cy')
            double sign = (largeArc == sweep) ? -1.0 : 1.0;
            double sq = (rx2 * ry2 - rx2 * y1p2 - ry2 * x1p2)
                      / (rx2 * y1p2 + ry2 * x1p2);
            sq = sq < 0 ? 0 : sq;
            double coef = sign * Math.Sqrt(sq);
            double cxp = coef * (rx * y1p / ry);
            double cyp = coef * -(ry * x1p / rx);

            // Step 4: compute (cx, cy) from (cx', cy')
            double cx = cosPhi * cxp - sinPhi * cyp + (x1 + x2) / 2.0;
            double cy = sinPhi * cxp + cosPhi * cyp + (y1 + y2) / 2.0;

            // Step 5: compute angle start and angle delta
            double ux = (x1p - cxp) / rx;
            double uy = (y1p - cyp) / ry;
            double vx = (-x1p - cxp) / rx;
            double vy = (-y1p - cyp) / ry;

            double n = Math.Sqrt(ux * ux + uy * uy);
            double p = ux;
            double angleStart = (uy < 0 ? -1 : 1) * Math.Acos(Clamp(p / n, -1, 1));

            n = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            p = ux * vx + uy * vy;
            double angleDelta = (ux * vy - uy * vx < 0 ? -1 : 1)
                              * Math.Acos(Clamp(p / n, -1, 1));

            if (!sweep && angleDelta > 0) angleDelta -= 2 * Math.PI;
            else if (sweep && angleDelta < 0) angleDelta += 2 * Math.PI;

            // Step 6: split arc into segments of <= 90 degrees and convert
            // each to a cubic Bezier.
            int segments = (int)Math.Ceiling(Math.Abs(angleDelta) / (Math.PI / 2.0));
            double delta = angleDelta / segments;
            double t = 8.0 / 3.0 * Math.Sin(delta / 4) * Math.Sin(delta / 4)
                     / Math.Sin(delta / 2);

            double curX = x1, curY = y1;
            double a = angleStart;
            for (int seg = 0; seg < segments; seg++)
            {
                double cosA1 = Math.Cos(a);
                double sinA1 = Math.Sin(a);
                double cosA2 = Math.Cos(a + delta);
                double sinA2 = Math.Sin(a + delta);

                // End point of this segment
                double ex = cosPhi * (rx * cosA2) - sinPhi * (ry * sinA2) + cx;
                double ey = sinPhi * (rx * cosA2) + cosPhi * (ry * sinA2) + cy;

                // Control points
                double c1xLocal = rx * cosA1 - t * rx * sinA1;
                double c1yLocal = ry * sinA1 + t * ry * cosA1;
                double c2xLocal = rx * cosA2 + t * rx * sinA2;
                double c2yLocal = ry * sinA2 - t * ry * cosA2;
                double c1x = cosPhi * c1xLocal - sinPhi * c1yLocal + cx;
                double c1y = sinPhi * c1xLocal + cosPhi * c1yLocal + cy;
                double c2x = cosPhi * c2xLocal - sinPhi * c2yLocal + cx;
                double c2y = sinPhi * c2xLocal + cosPhi * c2yLocal + cy;

                path.AddBezier((float)curX, (float)curY,
                               (float)c1x, (float)c1y,
                               (float)c2x, (float)c2y,
                               (float)ex, (float)ey);
                curX = ex; curY = ey;
                a += delta;
            }
        }

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : (v > hi ? hi : v);
    }
}