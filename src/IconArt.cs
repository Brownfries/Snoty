using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Snoty
{
    /// <summary>Draws the little sticky note once, for both the floating window and the docked taskbar child.</summary>
    internal static class IconArt
    {
        private static Icon _windowIcon;

        /// <summary>A shared window icon so any stray taskbar/Alt-Tab entry is branded.</summary>
        public static Icon WindowIcon()
        {
            if (_windowIcon == null) _windowIcon = CreateIcon(32);
            return _windowIcon;
        }

        public static Icon CreateIcon(int size)
        {
            if (size < 16) size = 16;

            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    Theme.DrawAppMark(g, new RectangleF(0.5f, 0.5f, size - 1f, size - 1f), false, 0);
                }

                IntPtr handle = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }

        public static Bitmap Render(int w, int h, bool listening, int noteCount)
        {
            if (w <= 0 || h <= 0) return null;

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                int inset = Theme.S(4);
                RectangleF body = new RectangleF(inset, inset, w - inset * 2, h - inset * 2);
                float radius = body.Width * 0.26f;

                // Cheap layered drop shadow.
                for (int i = 4; i >= 1; i--)
                {
                    float grow = i * Theme.SF(0.8f);
                    RectangleF sr = new RectangleF(
                        body.X - grow,
                        body.Y - grow + Theme.SF(1.2f),
                        body.Width + grow * 2f,
                        body.Height + grow * 2f);
                    using (GraphicsPath path = Theme.RoundedRect(sr, radius + grow))
                    using (Brush b = new SolidBrush(Color.FromArgb(7 - i, 0, 0, 0)))
                    {
                        g.FillPath(b, path);
                    }
                }

                Theme.DrawAppMark(g, body, listening, noteCount);
            }
            return bmp;
        }
    }
}
