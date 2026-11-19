using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Snoty
{
    /// <summary>
    /// Small themed About card. It uses only the chrome palette, so it follows
    /// Windows light/dark mode like the menu and the popup header/footer.
    /// </summary>
    internal sealed class AboutForm : Form
    {
        private const int CardWidth = 400;
        private const int HeaderHeight = 62;
        private const int FooterHeight = 56;

        private Panel _header;
        private Panel _body;
        private Panel _footer;
        private Panel _logo;
        private Label _title;
        private Label _version;
        private Label _tagline;
        private LinkLabel _credits;
        private Label _footerNote;
        private IconButton _closeButton;
        private Button _okButton;
        private int _authorStart;
        private int _coffeeStart;

        public AboutForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Theme.ChromeBorder;
            Text = "About " + AppPaths.Name;
            Icon = IconArt.WindowIcon();
            MinimizeBox = false;
            MaximizeBox = false;

            // DoLayout sizes the card to its content once the labels know how
            // much they wrap. This provisional width is all BuildUi needs.
            ClientSize = new Size(Theme.S(CardWidth), Theme.S(200));

            BuildUi();
            ApplyTheme();
            Theme.Changed += OnThemeChanged;
        }

        private void BuildUi()
        {
            _header = new Panel();
            _header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Separator))
                {
                    e.Graphics.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1);
                }
            };
            Controls.Add(_header);

            _logo = new Panel();
            _logo.BackColor = Color.Transparent;
            _logo.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int inset = Theme.S(2);
                Theme.DrawAppMark(e.Graphics,
                    new RectangleF(inset, inset,
                        _logo.Width - inset * 2, _logo.Height - inset * 2),
                    false, 0);
            };
            _header.Controls.Add(_logo);

            _title = new Label();
            _title.Text = AppPaths.Name;
            _title.AutoSize = false;
            _title.BackColor = Color.Transparent;
            _title.TextAlign = ContentAlignment.MiddleLeft;
            _header.Controls.Add(_title);

            _version = new Label();
            _version.Text = "Version " + AppPaths.Version;
            _version.AutoSize = false;
            _version.BackColor = Color.Transparent;
            _version.TextAlign = ContentAlignment.MiddleLeft;
            _header.Controls.Add(_version);

            _closeButton = new IconButton(GlyphKind.Close, Theme.S(26));
            _closeButton.Click += delegate { Close(); };
            _header.Controls.Add(_closeButton);

            _body = new Panel();
            Controls.Add(_body);

            _tagline = MakeLabel(AppPaths.TagLine, Theme.FontBody, Theme.TextMain, true);
            _body.Controls.Add(_tagline);

            _credits = new LinkLabel();
            _credits.Text = "Made by Brownfries  •  Support on Buy Me a Coffee";
            _credits.Font = Theme.FontSmall;
            _credits.ForeColor = Theme.TextDim;
            _credits.LinkColor = Theme.Accent;
            _credits.ActiveLinkColor = Theme.Mix(Theme.Accent, Color.Black, 0.2f);
            _credits.DisabledLinkColor = Theme.TextDim;
            _credits.BackColor = Color.Transparent;
            _credits.AutoSize = true;
            _credits.TextAlign = ContentAlignment.MiddleLeft;

            _authorStart = _credits.Text.IndexOf("Brownfries", StringComparison.Ordinal);
            _coffeeStart = _credits.Text.IndexOf("Buy Me a Coffee", StringComparison.Ordinal);

            LinkLabel.Link authorLink = new LinkLabel.Link();
            authorLink.Start = _authorStart;
            authorLink.Length = "Brownfries".Length;
            authorLink.Description = "Brownfries on GitHub";
            _credits.Links.Add(authorLink);

            LinkLabel.Link coffeeLink = new LinkLabel.Link();
            coffeeLink.Start = _coffeeStart;
            coffeeLink.Length = "Buy Me a Coffee".Length;
            coffeeLink.Description = "Support on Buy Me a Coffee";
            _credits.Links.Add(coffeeLink);

            _credits.LinkClicked += OnCreditClicked;
            _body.Controls.Add(_credits);

            _footer = new Panel();
            _footer.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Separator))
                {
                    e.Graphics.DrawLine(p, 0, 0, _footer.Width, 0);
                }
            };
            Controls.Add(_footer);

            _footerNote = MakeLabel(
                "Windows 10 / 11  |  .NET Framework 4.8",
                Theme.FontSmall, Theme.TextDim, false);
            _footer.Controls.Add(_footerNote);

            _okButton = new Button();
            _okButton.Text = "Close";
            _okButton.Font = Theme.FontSmall;
            _okButton.FlatStyle = FlatStyle.Flat;
            _okButton.UseVisualStyleBackColor = false;
            _okButton.Cursor = Cursors.Hand;
            _okButton.TabIndex = 0;
            _okButton.Click += delegate { Close(); };
            _footer.Controls.Add(_okButton);

            AcceptButton = _okButton;
            CancelButton = _okButton;

            DoLayout();
        }

        private static Label MakeLabel(string text, Font font, Color color, bool wrap)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = font;
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.AutoSize = true;
            if (wrap)
                label.MaximumSize = new Size(Theme.S(CardWidth) - Theme.S(34), 0);
            return label;
        }

        /// <summary>Opens the project page or the tip jar in the default browser.</summary>
        private void OnCreditClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (e.Link == null) return;

            string url = e.Link.Start == _coffeeStart
                ? "https://buymeacoffee.com/Brownfries"
                : "https://github.com/Brownfries";
            try { Process.Start(url); }
            catch { }
        }

        private void DoLayout()
        {
            int width = ClientSize.Width;
            int pad = Theme.S(16);
            int headerH = Theme.S(HeaderHeight);
            int footerH = Theme.S(FooterHeight);

            _header.SetBounds(1, 1, width - 2, headerH);

            int logo = Theme.S(42);
            _logo.SetBounds(pad, (headerH - logo) / 2, logo, logo);

            _title.Font = new Font("Segoe UI", Theme.SF(19f), FontStyle.Bold, GraphicsUnit.Pixel);
            _title.SetBounds(pad + logo + Theme.S(12), Theme.S(7), width - pad * 3 - logo - Theme.S(30), Theme.S(27));
            _version.SetBounds(_title.Left, Theme.S(35), _title.Width, Theme.S(18));

            int close = Theme.S(26);
            _closeButton.SetBounds(width - pad - close, (headerH - close) / 2, close, close);

            int inner = width - 2 - pad * 2;

            _tagline.MaximumSize = new Size(inner, 0);
            _tagline.Location = new Point(pad, pad);

            int y = _tagline.Bottom + Theme.S(16);
            _credits.MaximumSize = new Size(inner, 0);
            _credits.Location = new Point(pad, y);

            // End the body just below the last line instead of at a fixed
            // height. This removes the empty strip and avoids clipping text
            // when label wrapping changes with DPI or font metrics.
            int bodyH = _credits.Bottom + pad;
            int height = 1 + headerH + bodyH + footerH + 1;

            ClientSize = new Size(width, height);
            _body.SetBounds(1, 1 + headerH, width - 2, bodyH);
            _footer.SetBounds(1, height - footerH - 1, width - 2, footerH);

            int buttonW = Theme.S(86);
            int buttonH = Theme.S(28);
            _okButton.SetBounds(width - pad - buttonW, (footerH - buttonH) / 2, buttonW, buttonH);

            _footerNote.Location = new Point(pad, (footerH - _footerNote.Height) / 2);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Native.RemoveTaskbarTab(Handle);
            base.OnFormClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void ApplyTheme()
        {
            if (IsDisposed) return;

            BackColor = Theme.ChromeBorder;
            _header.BackColor = Theme.ChromeHeader;
            _body.BackColor = Theme.ChromeBack;
            _footer.BackColor = Theme.ChromeHeader;

            _title.ForeColor = Theme.TextMain;
            _version.ForeColor = Theme.TextDim;
            _tagline.ForeColor = Theme.TextMain;
            _credits.ForeColor = Theme.TextDim;
            _credits.LinkColor = Theme.Accent;
            _credits.ActiveLinkColor = Theme.Mix(Theme.Accent, Color.Black, 0.2f);
            _footerNote.ForeColor = Theme.TextDim;

            _closeButton.BackColor = Theme.ChromeHeader;
            _closeButton.GlyphColor = Theme.TextMain;
            _closeButton.HoverBack = Theme.HoverBack;
            _closeButton.PressBack = Theme.PressBack;

            _okButton.BackColor = Theme.Accent;
            _okButton.ForeColor = Theme.OnAccent;
            _okButton.FlatAppearance.BorderSize = 0;
            _okButton.FlatAppearance.MouseOverBackColor = Theme.Mix(Theme.Accent, Color.Black, 0.08f);
            _okButton.FlatAppearance.MouseDownBackColor = Theme.Mix(Theme.Accent, Color.Black, 0.16f);

            _logo.Invalidate();
            _header.Invalidate();
            _footer.Invalidate();
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke((MethodInvoker)ApplyTheme);
                else ApplyTheme();
            }
            catch
            {
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Theme.Changed -= OnThemeChanged;
            base.Dispose(disposing);
        }
    }
}
