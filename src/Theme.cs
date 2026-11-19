using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Snoty
{
    internal enum GlyphKind { Plus, Close, Mic, Sun, Moon, Pin }

    /// <summary>
    /// Colours, DPI scaling, fonts and small vector glyphs. The chrome follows
    /// the Windows app theme; the notes themselves keep their paper colours.
    /// </summary>
    internal static class Theme
    {
        public static float Scale = 1f;
        public static bool IsDark { get; private set; }

        /// <summary>True while the chrome follows the Windows app theme.</summary>
        public static bool Auto { get; private set; }

        public static Font FontBody;
        public static Font FontSmall;
        public static Font FontTitle;

        public static event EventHandler Changed;

        public static Color ChromeBack { get { return IsDark ? C(30, 30, 32) : C(247, 247, 250); } }
        public static Color ChromeHeader { get { return IsDark ? C(37, 37, 40) : C(238, 238, 243); } }
        public static Color ChromeBorder { get { return IsDark ? C(58, 58, 62) : C(203, 203, 212); } }
        public static Color Separator { get { return IsDark ? C(54, 54, 58) : C(225, 225, 232); } }
        public static Color TextMain { get { return IsDark ? C(242, 242, 245) : C(38, 38, 43); } }
        public static Color TextDim { get { return IsDark ? C(160, 160, 168) : C(134, 134, 145); } }
        public static Color HoverBack { get { return IsDark ? C(52, 52, 56) : C(225, 225, 233); } }
        public static Color PressBack { get { return IsDark ? C(66, 66, 71) : C(205, 205, 216); } }
        public static Color Accent { get { return IsDark ? C(240, 183, 52) : C(233, 170, 24); } }
        public static Color OnAccent { get { return C(45, 34, 8); } }
        public static Color Rec { get { return IsDark ? C(255, 107, 107) : C(214, 58, 58); } }
        public static Color Warn { get { return IsDark ? C(240, 178, 90) : C(166, 94, 0); } }

        public static Color MenuBack { get { return IsDark ? C(38, 38, 41) : C(249, 249, 251); } }
        public static Color MenuHover { get { return IsDark ? C(56, 56, 61) : C(232, 232, 238); } }
        public static Color MenuBorder { get { return IsDark ? C(64, 64, 69) : C(205, 205, 214); } }

        /// <summary>The ring around the mic, visible even when the button is idle.</summary>
        public static Color MicRing { get { return IsDark ? C(132, 132, 144) : C(188, 188, 200); } }

        public static readonly Color CardText = Color.FromArgb(60, 52, 24);

        public static readonly Color[] NoteBack =
        {
            Color.FromArgb(255, 246, 173),
            Color.FromArgb(255, 213, 232),
            Color.FromArgb(213, 244, 196),
            Color.FromArgb(205, 228, 255),
            Color.FromArgb(227, 213, 255)
        };

        public static readonly Color[] NoteEdge =
        {
            Color.FromArgb(231, 213, 108),
            Color.FromArgb(233, 175, 203),
            Color.FromArgb(174, 212, 153),
            Color.FromArgb(159, 196, 236),
            Color.FromArgb(196, 175, 236)
        };

        private static Color C(int r, int g, int b)
        {
            return Color.FromArgb(255, r, g, b);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);

        public static void Init()
        {
            try
            {
                IntPtr hdc = GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    int dpi = GetDeviceCaps(hdc, 88); // LOGPIXELSX
                    ReleaseDC(IntPtr.Zero, hdc);
                    if (dpi > 0) Scale = dpi / 96f;
                }
            }
            catch { }

            if (Scale < 1f) Scale = 1f;
            if (Scale > 4f) Scale = 4f;

            Auto = true;
            IsDark = ReadSystemDark();

            FontBody = MakeFont(13.5f, FontStyle.Regular);
            FontSmall = MakeFont(11.5f, FontStyle.Regular);
            FontTitle = MakeFont(13.5f, FontStyle.Bold);
        }

        /// <summary>Windows lets apps choose a light or dark shell independently.</summary>
        public static bool ReadSystemDark()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", null);
                if (value is int) return (int)value == 0;

                value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "SystemUsesLightTheme", null);
                if (value is int) return (int)value == 0;
            }
            catch
            {
            }
            return false;
        }

        /// <summary>Re-reads the Windows theme and notifies the UI if it changed.</summary>
        public static void FollowSystem()
        {
            if (!Auto) return;
            bool dark = ReadSystemDark();
            if (dark == IsDark) return;
            UseDark(dark);
        }

        /// <summary>Applies "auto", "light" or "dark" from the saved settings.</summary>
        public static void SetMode(string mode)
        {
            if (string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase))
            {
                UseManual(false);
            }
            else if (string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase))
            {
                UseManual(true);
            }
            else
            {
                UseAuto();
            }
        }

        /// <summary>Stops following Windows until <see cref="UseAuto"/> is called.</summary>
        public static void UseManual(bool dark)
        {
            Auto = false;
            UseDark(dark);
        }

        /// <summary>Returns to the Windows app theme.</summary>
        public static void UseAuto()
        {
            Auto = true;
            bool dark = ReadSystemDark();
            if (dark != IsDark) UseDark(dark);
        }

        public static void UseDark(bool dark)
        {
            if (dark == IsDark) return;
            IsDark = dark;

            EventHandler h = Changed;
            if (h != null)
            {
                try { h(null, EventArgs.Empty); }
                catch { }
            }
        }

        private static Font MakeFont(float px, FontStyle style)
        {
            float size = px * Scale;
            if (size < 6f) size = 6f;
            return new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
        }

        /// <summary>Scale a design pixel value to device pixels.</summary>
        public static int S(float px)
        {
            return (int)Math.Round(px * Scale);
        }

        public static float SF(float px)
        {
            return px * Scale;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color HoverOver(Color back)
        {
            return Mix(back, Color.Black, 0.09f);
        }

        public static Color PressOver(Color back)
        {
            return Mix(back, Color.Black, 0.17f);
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.Left, r.Top, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Top, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private static int ColorIndexSafe(int i)
        {
            int n = NoteBack.Length;
            if (i < 0) i = 0;
            return i % n;
        }

        public static Color BackFor(int colorIndex)
        {
            return NoteBack[ColorIndexSafe(colorIndex)];
        }

        public static Color EdgeFor(int colorIndex)
        {
            return NoteEdge[ColorIndexSafe(colorIndex)];
        }

        public static int NextColor(int current)
        {
            return (ColorIndexSafe(current) + 1) % NoteBack.Length;
        }

        // ------------------------------------------------------------------
        // Vector glyphs (no font/emoji dependency, so they render everywhere)
        // ------------------------------------------------------------------

        public static void DrawGlyph(Graphics g, GlyphKind kind, RectangleF r, Color color, float thickness)
        {
            SmoothingMode old = g.SmoothingMode;
            PixelOffsetMode oldOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float t = Math.Max(1f, thickness);
            try
            {
                using (Pen p = new Pen(color, t))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;

                    switch (kind)
                    {
                        case GlyphKind.Plus:
                            g.DrawLine(p, r.Left + r.Width / 2f, r.Top, r.Left + r.Width / 2f, r.Bottom);
                            g.DrawLine(p, r.Left, r.Top + r.Height / 2f, r.Right, r.Top + r.Height / 2f);
                            break;

                        case GlyphKind.Close:
                            g.DrawLine(p, r.Left, r.Top, r.Right, r.Bottom);
                            g.DrawLine(p, r.Right, r.Top, r.Left, r.Bottom);
                            break;

                        case GlyphKind.Mic:
                            DrawMic(g, p, r);
                            break;

                        case GlyphKind.Sun:
                            DrawSun(g, p, r);
                            break;

                        case GlyphKind.Pin:
                            DrawPin(g, p, r);
                            break;

                        // Moon is drawn by DrawMoonGlyph, which needs the
                        // button background for the crescent cut-out.
                    }
                }
            }
            finally
            {
                g.SmoothingMode = old;
                g.PixelOffsetMode = oldOffset;
            }
        }

        private static void DrawSun(Graphics g, Pen p, RectangleF r)
        {
            float cx = r.Left + r.Width / 2f;
            float cy = r.Top + r.Height / 2f;
            float rad = Math.Min(r.Width, r.Height) * 0.23f;

            g.DrawEllipse(p, cx - rad, cy - rad, rad * 2f, rad * 2f);

            float rayIn = rad * 1.55f;
            float rayOut = rad * 2.05f;
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4.0;
                float dx = (float)Math.Cos(a);
                float dy = (float)Math.Sin(a);
                g.DrawLine(p,
                    cx + dx * rayIn, cy + dy * rayIn,
                    cx + dx * rayOut, cy + dy * rayOut);
            }
        }

        /// <summary>
        /// Crescent drawn as two anti-aliased filled circles. Filling a region
        /// has hard, jagged edges at small sizes.
        /// </summary>
        public static void DrawMoonGlyph(Graphics g, RectangleF r, Color color, Color background)
        {
            SmoothingMode old = g.SmoothingMode;
            PixelOffsetMode oldOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            try
            {
                float d = Math.Min(r.Width, r.Height) * 0.88f;
                float x = r.Left + (r.Width - d) / 2f;
                float y = r.Top + (r.Height - d) / 2f;

                using (Brush outer = new SolidBrush(color))
                {
                    g.FillEllipse(outer, x, y, d, d);
                }
                using (Brush cut = new SolidBrush(background))
                {
                    g.FillEllipse(cut, x + d * 0.34f, y - d * 0.08f, d * 0.94f, d * 0.94f);
                }
            }
            finally
            {
                g.SmoothingMode = old;
                g.PixelOffsetMode = oldOffset;
            }
        }

        private static void DrawPin(Graphics g, Pen p, RectangleF r)
        {
            float u = Math.Min(r.Width, r.Height);
            float cx = r.Left + r.Width * 0.5f;
            float cy = r.Top + r.Height * 0.5f;

            GraphicsState state = g.Save();
            try
            {
                // A small thumbtack, tilted so it reads as a pin at 16 px.
                g.TranslateTransform(cx, cy);
                g.RotateTransform(-35f);

                using (Brush head = new SolidBrush(p.Color))
                {
                    g.FillEllipse(head, -u * 0.20f, -u * 0.40f, u * 0.40f, u * 0.40f);
                }
                g.DrawLine(p, 0f, -u * 0.02f, 0f, u * 0.34f);
                g.DrawLine(p, -u * 0.22f, -u * 0.02f, u * 0.22f, -u * 0.02f);
            }
            finally
            {
                g.Restore(state);
            }
        }

        private static void DrawMic(Graphics g, Pen p, RectangleF r)
        {
            float cx = r.Left + r.Width / 2f;
            float top = r.Top;
            float h = r.Height;
            float w = r.Width;

            float capW = w * 0.38f;
            float capTop = top + h * 0.05f;
            float capH = h * 0.46f;

            RectangleF cap = new RectangleF(cx - capW / 2f, capTop, capW, capH);
            using (GraphicsPath path = RoundedRect(cap, capW / 2f))
            {
                g.DrawPath(p, path);
            }

            float rad = h * 0.25f;
            float cy = capTop + capH * 0.95f;
            g.DrawArc(p, cx - rad, cy - rad, rad * 2f, rad * 2f, 0f, 180f);

            float stemBottom = top + h * 0.90f;
            g.DrawLine(p, cx, cy + rad, cx, stemBottom);
            float baseW = w * 0.34f;
            g.DrawLine(p, cx - baseW / 2f, stemBottom, cx + baseW / 2f, stemBottom);
        }

        /// <summary>
        /// Draws the Snoty mascot: a sticky note with a face and a wire coil
        /// down its spine. The note is the character - the wire is the only
        /// nod to the old XP helper - so the mark reads as a note first at
        /// every size, and gains a face and a coil as it grows.
        /// </summary>
        public static void DrawAppMark(Graphics g, RectangleF r, bool listening, int noteCount)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                float k = Math.Min(r.Width, r.Height) / 100f;
                float ox = r.Left + (r.Width - 100f * k) / 2f;
                float oy = r.Top + (r.Height - 100f * k) / 2f;
                bool detail = r.Width >= 26f;
                bool showCoil = r.Width >= 34f;

                RectangleF body = new RectangleF(ox + 8f * k, oy + 4f * k, 88f * k, 90f * k);
                float radius = 17f * k;

                using (Brush shadow = new SolidBrush(Color.FromArgb(45, 0, 0, 0)))
                {
                    g.FillEllipse(shadow, ox + 12f * k, oy + 85f * k, 84f * k, 11f * k);
                }

                using (GraphicsPath path = RoundedRect(body, radius))
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(
                        body,
                        Color.FromArgb(255, 255, 241, 164),
                        Color.FromArgb(255, 237, 183, 54),
                        LinearGradientMode.Vertical))
                    {
                        g.FillPath(b, path);
                    }

                    if (detail)
                    {
                        GraphicsState state = g.Save();
                        g.SetClip(path);
                        using (Brush gloss = new SolidBrush(Color.FromArgb(48, 255, 255, 255)))
                        {
                            g.FillRectangle(gloss, body.Left, body.Top,
                                body.Width, body.Height * 0.38f);
                        }
                        g.Restore(state);
                    }

                    using (Pen edge = new Pen(Color.FromArgb(240, 193, 148, 40),
                        Math.Max(1f, r.Width * 0.022f)))
                    {
                        g.DrawPath(edge, path);
                    }
                }

                // Folded corner, top-right.
                float fold = 25f * k;
                PointF[] tri =
                {
                    new PointF(body.Right - fold, body.Top),
                    new PointF(body.Right, body.Top),
                    new PointF(body.Right, body.Top + fold)
                };
                using (Brush fb = new SolidBrush(Color.FromArgb(225, 203, 146, 36)))
                {
                    g.FillPolygon(fb, tri);
                }

                // Spiral wire binding down the left spine. At tray sizes it
                // would read as a grey fringe, so it is drawn only when there
                // are enough pixels for separate coils.
                if (showCoil)
                {
                    using (GraphicsPath coil = new GraphicsPath())
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            float cy = oy + (20f + i * 12f) * k;
                            coil.AddBezier(
                                ox + 21f * k, cy,
                                ox + 7.5f * k, cy - 2f * k,
                                ox + 7.5f * k, cy + 11f * k,
                                ox + 21f * k, cy + 9f * k);
                        }

                        using (Pen outline = new Pen(Color.FromArgb(255, 46, 60, 80), 4.0f * k))
                        {
                            outline.LineJoin = LineJoin.Round;
                            outline.StartCap = LineCap.Round;
                            outline.EndCap = LineCap.Round;
                            g.DrawPath(outline, coil);
                        }

                        RectangleF coilBox = new RectangleF(ox + 5f * k, oy + 18f * k, 18f * k, 56f * k);
                        using (LinearGradientBrush cg = new LinearGradientBrush(coilBox,
                            Color.FromArgb(255, 250, 252, 255),
                            Color.FromArgb(255, 158, 176, 200),
                            LinearGradientMode.Horizontal))
                        using (Pen metal = new Pen(cg, 2.4f * k))
                        {
                            metal.LineJoin = LineJoin.Round;
                            metal.StartCap = LineCap.Round;
                            metal.EndCap = LineCap.Round;
                            g.DrawPath(metal, coil);
                        }
                    }
                }

                // Closed, content eyes: two shallow arcs rather than staring
                // pupils, so the mascot reads as friendly instead of uncanny.
                float eyeW = 16f * k;
                float eyeH = 7f * k;
                float ey = oy + 45f * k;
                using (Pen ep = new Pen(Color.FromArgb(255, 128, 88, 22),
                    Math.Max(1.3f, r.Width * 0.027f)))
                {
                    ep.StartCap = LineCap.Round;
                    ep.EndCap = LineCap.Round;
                    for (int i = 0; i < 2; i++)
                    {
                        float cx = ox + (43f + i * 22f) * k;
                        using (GraphicsPath eye = new GraphicsPath())
                        {
                            eye.AddBezier(
                                cx - eyeW / 2f, ey + 1f * k,
                                cx - eyeW * 0.28f, ey - eyeH,
                                cx + eyeW * 0.28f, ey - eyeH,
                                cx + eyeW / 2f, ey + 1f * k);
                            g.DrawPath(ep, eye);
                        }
                    }
                }

                using (GraphicsPath smile = new GraphicsPath())
                {
                    smile.AddBezier(
                        ox + 45f * k, oy + 60f * k,
                        ox + 48f * k, oy + 67f * k,
                        ox + 60f * k, oy + 67f * k,
                        ox + 63f * k, oy + 60f * k);
                    using (Pen sp = new Pen(Color.FromArgb(255, 141, 95, 26),
                        Math.Max(1f, r.Width * 0.019f)))
                    {
                        sp.StartCap = LineCap.Round;
                        sp.EndCap = LineCap.Round;
                        g.DrawPath(sp, smile);
                    }
                }

                // Count and recording badges sit on the bottom corners, clear
                // of the face and of the folded corner.
                if (noteCount > 0)
                {
                    float d = 27f * k;
                    RectangleF badge = new RectangleF(
                        ox + 84f * k - d / 2f, oy + 82f * k - d / 2f, d, d);
                    using (Brush bb = new SolidBrush(Color.FromArgb(58, 58, 66)))
                    {
                        g.FillEllipse(bb, badge);
                    }
                    using (Pen ring = new Pen(Color.FromArgb(210, 22, 22, 26),
                        Math.Max(1f, d * 0.06f)))
                    {
                        g.DrawEllipse(ring, badge);
                    }
                    string label = noteCount.ToString();
                    if (d < 13f && noteCount > 9) label = "9+";
                    else if (noteCount > 99) label = "99+";
                    float fontSize = Math.Max(4.5f, d * 0.62f);
                    using (Font probeFont = new Font("Segoe UI", fontSize,
                        FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        SizeF measured = g.MeasureString(label, probeFont,
                            PointF.Empty, StringFormat.GenericTypographic);
                        if (measured.Width > d * 0.92f)
                            fontSize = Math.Max(4f, fontSize * (d * 0.92f) / measured.Width);
                    }
                    using (Font f = new Font("Segoe UI", fontSize,
                        FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Brush tb = new SolidBrush(Color.White))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(label, f, tb, badge, sf);
                    }
                }

                if (listening)
                {
                    float d = 27f * k;
                    RectangleF dot = new RectangleF(
                        ox + 16f * k - d / 2f, oy + 82f * k - d / 2f, d, d);
                    using (Brush rb = new SolidBrush(Rec))
                    {
                        g.FillEllipse(rb, dot);
                    }
                    using (Pen ring = new Pen(Color.White, Math.Max(1f, d * 0.14f)))
                    {
                        g.DrawEllipse(ring, dot);
                    }
                }
            }
            finally
            {
                g.SmoothingMode = old;
            }
        }
    }
}
