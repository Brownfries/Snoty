using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Snoty
{
    /// <summary>
    /// WinForms' drop-down window does not carry WS_EX_TOOLWINDOW, so opening
    /// the context menu gives the process a normal taskbar button (the blank
    /// "non icon exe" that appeared next to the docked icon). Add the style
    /// before the window is created and remove any tab the shell may already
    /// have made.
    /// </summary>
    internal sealed class SnotyMenu : ContextMenuStrip
    {
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            Native.MakeToolWindow(Handle);
            Native.RemoveTaskbarTab(Handle);
        }

        protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
        {
            Native.RemoveTaskbarTab(Handle);
            base.OnClosed(e);
        }
    }

    /// <summary>
    /// WinForms menus ignore the Windows app theme by default. These two small
    /// classes paint the context menu with the same chrome palette as the
    /// popup, so it follows light/dark mode too. Note paper colours are never
    /// touched here.
    /// </summary>
    internal sealed class MenuThemeColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.MenuBack; } }
        public override Color MenuBorder { get { return Theme.MenuBorder; } }
        public override Color MenuItemBorder { get { return Theme.MenuHover; } }
        public override Color MenuItemSelected { get { return Theme.MenuHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.MenuHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.MenuHover; } }
        public override Color ImageMarginGradientBegin { get { return Theme.MenuBack; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.MenuBack; } }
        public override Color ImageMarginGradientEnd { get { return Theme.MenuBack; } }
        public override Color SeparatorDark { get { return Theme.Separator; } }
        public override Color SeparatorLight { get { return Theme.Separator; } }
        public override Color CheckBackground { get { return Theme.MenuHover; } }
        public override Color CheckSelectedBackground { get { return Theme.MenuHover; } }
        public override Color CheckPressedBackground { get { return Theme.PressBack; } }
    }

    internal sealed class MenuThemeRenderer : ToolStripProfessionalRenderer
    {
        public MenuThemeRenderer()
            : base(new MenuThemeColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (Brush b = new SolidBrush(Theme.MenuBack))
            {
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (Pen p = new Pen(Theme.MenuBorder))
            {
                e.Graphics.DrawRectangle(p, r);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;

            Rectangle r = new Rectangle(1, 0, e.Item.Width - 2, e.Item.Height);
            using (Brush b = new SolidBrush(Theme.MenuHover))
            {
                e.Graphics.FillRectangle(b, r);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.TextMain : Theme.TextDim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Theme.Separator))
            {
                e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            if (r.Width <= 0 || r.Height <= 0)
                r = new Rectangle(4, 3, Math.Max(10, e.Item.Height - 6), e.Item.Height - 6);

            SmoothingMode oldSmoothing = e.Graphics.SmoothingMode;
            PixelOffsetMode oldOffset = e.Graphics.PixelOffsetMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            try
            {
                float t = Math.Max(1.4f, Theme.SF(1.6f));
                // The check mark follows the chrome contrast: white on dark
                // menus, black on light ones.
                Color ink = Theme.IsDark ? Color.White : Color.Black;
                using (Pen p = new Pen(ink, t))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;

                    float cx = r.Left + r.Width * 0.45f;
                    float cy = r.Top + r.Height * 0.55f;
                    e.Graphics.DrawLine(p, r.Left + r.Width * 0.20f, cy,
                                           cx, r.Top + r.Height * 0.76f);
                    e.Graphics.DrawLine(p, cx, r.Top + r.Height * 0.76f,
                                           r.Left + r.Width * 0.82f, r.Top + r.Height * 0.24f);
                }
            }
            finally
            {
                e.Graphics.SmoothingMode = oldSmoothing;
                e.Graphics.PixelOffsetMode = oldOffset;
            }
        }
    }
}
