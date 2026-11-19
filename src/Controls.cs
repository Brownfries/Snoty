using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Snoty
{
    /// <summary>Compact custom-drawn button used for the header, mic and theme controls.</summary>
    internal sealed class IconButton : Control
    {
        private GlyphKind _kind;
        private bool _hover;
        private bool _pressed;
        private bool _toggled;
        private bool _round;
        private Color _glyphColor = Theme.TextMain;
        private Color _toggleBack = Theme.Accent;
        private Color _hoverBack = Color.Empty;
        private Color _pressBack = Color.Empty;

        public IconButton(GlyphKind kind, int size)
        {
            _kind = kind;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Size = new Size(size, size);
            Cursor = Cursors.Hand;
            BackColor = Theme.ChromeBack;
        }

        public GlyphKind Kind
        {
            get { return _kind; }
            set { _kind = value; Invalidate(); }
        }

        public bool Toggled
        {
            get { return _toggled; }
            set { _toggled = value; Invalidate(); }
        }

        /// <summary>Draws a circular button with a ring instead of a rounded square.</summary>
        public bool Round
        {
            get { return _round; }
            set { _round = value; Invalidate(); }
        }

        public Color GlyphColor
        {
            get { return _glyphColor; }
            set { _glyphColor = value; Invalidate(); }
        }

        public Color ToggleBack
        {
            get { return _toggleBack; }
            set { _toggleBack = value; Invalidate(); }
        }

        public Color HoverBack
        {
            get { return _hoverBack; }
            set { _hoverBack = value; Invalidate(); }
        }

        public Color PressBack
        {
            get { return _pressBack; }
            set { _pressBack = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressed = false;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(BackColor);

            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Color hover = _hoverBack.IsEmpty ? Theme.HoverBack : _hoverBack;
            Color press = _pressBack.IsEmpty ? Theme.PressBack : _pressBack;

            Color fill = Color.Empty;
            if (_toggled) fill = _toggleBack;
            if (_pressed)
                fill = _toggled ? Theme.Mix(_toggleBack, Color.Black, 0.16f)
                                : Theme.Mix(press, Color.Black, 0.06f);
            else if (_hover && !_toggled)
                fill = hover;

            if (fill != Color.Empty && fill.A > 0)
            {
                if (_round)
                {
                    using (Brush b = new SolidBrush(fill))
                    {
                        g.FillEllipse(b, box);
                    }
                }
                else
                {
                    using (GraphicsPath path = Theme.RoundedRect(box, Theme.SF(6f)))
                    using (Brush b = new SolidBrush(fill))
                    {
                        g.FillPath(b, path);
                    }
                }
            }

            if (_round)
            {
                Color ring = _toggled ? _toggleBack
                           : (_hover ? Theme.Accent : Theme.MicRing);
                float thickness = Math.Max(1f, Theme.SF(_toggled ? 1.7f : 1.3f));
                using (Pen p = new Pen(ring, thickness))
                {
                    float inset = Math.Max(1f, Theme.SF(1f));
                    float diameter = Width - 1f - inset * 2f;
                    float origin = (Width - diameter) / 2f;
                    g.DrawEllipse(p, origin, origin, diameter, diameter);
                }
            }

            Color glyph = _glyphColor;
            if (_toggled)
                glyph = _toggleBack.GetBrightness() > 0.62f ? Theme.OnAccent : Color.White;

            float pad = Math.Max(2.2f, Width * (_kind == GlyphKind.Pin ? 0.15f : 0.22f));
            RectangleF area = new RectangleF(pad, pad, Width - pad * 2f, Height - pad * 2f);
            float glyphThickness = Math.Max(1.6f, Theme.SF(1.8f));
            if (_kind == GlyphKind.Moon)
            {
                Color behind = fill != Color.Empty && fill.A > 0 ? fill : BackColor;
                Theme.DrawMoonGlyph(g, area, glyph, behind);
            }
            else
            {
                Theme.DrawGlyph(g, _kind, area, glyph, glyphThickness);
            }
        }
    }

    /// <summary>A bookmark-shaped delete tab that hangs from the note's top edge.</summary>
    internal sealed class BookmarkButton : Control
    {
        private readonly Note _note;
        private bool _hover;
        private bool _pressed;

        public BookmarkButton(Note note)
        {
            _note = note;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Size = new Size(Theme.S(20), Theme.S(26));
            Cursor = Cursors.Hand;
            BackColor = Theme.BackFor(note.ColorIndex);
            UpdateRegion();
        }

        public void ApplyColor()
        {
            BackColor = Theme.BackFor(_note.ColorIndex);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressed = false;
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            using (GraphicsPath path = Shape(new RectangleF(0, 0, Width, Height)))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        private static GraphicsPath Shape(RectangleF rect)
        {
            float radius = Math.Min(Theme.SF(3f),
                Math.Min(rect.Width, rect.Height) / 3f);
            float notch = Math.Min(Theme.SF(6.5f), rect.Height * 0.26f);

            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, radius * 2f, radius * 2f, 180f, 90f);
            path.AddArc(rect.Right - radius * 2f, rect.Top, radius * 2f, radius * 2f, 270f, 90f);
            path.AddLine(rect.Right, rect.Top + radius, rect.Right, rect.Bottom);
            path.AddLine(rect.Right, rect.Bottom,
                rect.Left + rect.Width / 2f, rect.Bottom - notch);
            path.AddLine(rect.Left + rect.Width / 2f, rect.Bottom - notch,
                rect.Left, rect.Bottom);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            RectangleF rect = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
            using (GraphicsPath path = Shape(rect))
            {
                if (_hover || _pressed)                {
                    Color fill = _pressed ? Theme.PressOver(BackColor)
                                          : Theme.HoverOver(BackColor);
                    using (Brush b = new SolidBrush(fill))
                    {
                        g.FillPath(b, path);
                    }
                }

                Color edge = Theme.Mix(Theme.EdgeFor(_note.ColorIndex),
                    Color.Black, 0.4f);
                using (Pen p = new Pen(Color.FromArgb(205, edge),
                    Math.Max(1.1f, Theme.SF(1.4f))))
                {
                    p.LineJoin = LineJoin.Round;
                    g.DrawPath(p, path);
                }
            }

            float glyph = Math.Min(Width * 0.52f, Height * 0.38f);
            RectangleF area = new RectangleF((Width - glyph) / 2f, Theme.SF(4.8f),
                glyph, glyph);
            Theme.DrawGlyph(g, GlyphKind.Close, area,
                Color.FromArgb(240, 74, 62, 32), Math.Max(1.4f, Theme.SF(1.5f)));
        }
    }

    /// <summary>Small translucent colour bar at the bottom centre of a note.</summary>
    internal sealed class ColorBarButton : Control
    {
        private readonly Note _note;
        private bool _hover;
        private bool _pressed;

        public ColorBarButton(Note note)
        {
            _note = note;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Size = new Size(Theme.S(26), Theme.S(7));
            Cursor = Cursors.Hand;
            BackColor = Theme.BackFor(note.ColorIndex);
        }

        public void ApplyColor()
        {
            BackColor = Theme.BackFor(_note.ColorIndex);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressed = false;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int alpha = _pressed ? 250 : (_hover ? 235 : 205);
            Color color = Theme.Mix(Theme.EdgeFor(_note.ColorIndex), Color.Black, 0.18f);
            RectangleF rect = new RectangleF(0, 0, Width - 1f, Height - 1f);
            using (GraphicsPath path = Theme.RoundedRect(rect, Height / 2f))
            using (Brush b = new SolidBrush(Color.FromArgb(alpha, color)))
            {
                g.FillPath(b, path);
            }
        }
    }

    /// <summary>Thin vertical scrollbar drawn in the chrome theme.</summary>
    internal sealed class NotesScrollBar : Control
    {
        private int _content;
        private int _viewport;
        private int _value;
        private bool _dragging;
        private bool _hover;
        private int _dragStartY;
        private int _dragStartValue;

        public event EventHandler ValueChanged;

        public NotesScrollBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Width = Theme.S(10);
            Visible = false;
            BackColor = Theme.ChromeBack;
        }

        public int Value
        {
            get { return _value; }
        }

        public void ApplyTheme()
        {
            BackColor = Theme.ChromeBack;
            Invalidate();
        }

        /// <summary>Updates the range and returns the clamped scroll offset.</summary>
        public int SetMetrics(int content, int viewport, int value)
        {
            _content = Math.Max(0, content);
            _viewport = Math.Max(1, viewport);

            int max = MaxValue;
            if (value < 0) value = 0;
            if (value > max) value = max;
            _value = value;

            bool need = _content > _viewport + 1;
            if (Visible != need) Visible = need;
            Invalidate();
            return _value;
        }

        public void SetValue(int value)
        {
            int max = MaxValue;
            if (value < 0) value = 0;
            if (value > max) value = max;
            if (value == _value) return;

            _value = value;
            Invalidate();
            EventHandler h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private int MaxValue
        {
            get { return Math.Max(0, _content - _viewport); }
        }

        private RectangleF Track()
        {
            float inset = Math.Max(1.5f, Theme.SF(2f));
            return new RectangleF(inset, inset, Width - inset * 2f, Height - inset * 2f);
        }

        private RectangleF Thumb()
        {
            int max = MaxValue;
            RectangleF track = Track();
            if (max <= 0 || track.Height <= 0) return RectangleF.Empty;

            float thumbH = Math.Max(Theme.SF(18f),
                track.Height * _viewport / (float)_content);
            if (thumbH > track.Height) thumbH = track.Height;
            float y = track.Top + (track.Height - thumbH) * _value / max;
            return new RectangleF(track.Left, y, track.Width, thumbH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(BackColor);

            RectangleF thumb = Thumb();
            if (thumb.Width < 1f || thumb.Height < 1f) return;

            Color color;
            if (Theme.IsDark)
            {
                color = (_dragging || _hover)
                    ? Color.FromArgb(200, 196, 198, 206)
                    : Color.FromArgb(145, 168, 168, 178);
            }
            else
            {
                color = (_dragging || _hover)
                    ? Color.FromArgb(200, 92, 92, 102)
                    : Color.FromArgb(140, 112, 112, 124);
            }

            using (GraphicsPath path = Theme.RoundedRect(thumb, thumb.Width / 2f))
            using (Brush b = new SolidBrush(color))
            {
                g.FillPath(b, path);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            RectangleF thumb = Thumb();
            if (thumb.Height < 1f) return;

            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragStartY = Cursor.Position.Y;
                _dragStartValue = _value;
                Capture = true;
            }
            else
            {
                int direction = e.Y < thumb.Top ? -1 : 1;
                SetValue(_value + direction * Math.Max(Theme.S(40), _viewport * 3 / 4));
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;

            RectangleF track = Track();
            RectangleF thumb = Thumb();
            float travel = track.Height - thumb.Height;
            int max = MaxValue;
            if (travel <= 0f || max <= 0) return;

            int dy = Cursor.Position.Y - _dragStartY;
            SetValue(_dragStartValue + (int)(dy * max / travel));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            Invalidate();
        }
    }

    /// <summary>One sticky note: editable body, bookmark delete and colour bar.</summary>
    internal sealed class NoteCard : Panel
    {
        private readonly Note _note;
        private readonly NotesStore _store;
        private readonly TextBox _box;
        private readonly BookmarkButton _delete;
        private readonly ColorBarButton _color;

        private bool _laying;
        private bool _syncing;
        private bool _draftMode;
        private int _reportedHeight = -1;
        private int _measuredWidth = -1;
        private int _measuredHeight;
        private string _measuredText;
        private int _layoutWidth = -1;
        private int _layoutHeight = -1;
        private string _layoutText;
        private bool _colorDown;
        private bool _colorDragged;
        private int _colorStartY;
        private int _colorAppliedSteps;

        public event EventHandler HeightChanged;
        public event EventHandler TextEdited;
        public event EventHandler DeleteRequested;
        public event EventHandler ColorRequested;
        public event EventHandler FocusedEvent;
        public event Action<NoteCard, int> MoveRequested;

        public Note Model
        {
            get { return _note; }
        }

        public NoteCard(Note note, NotesStore store, ToolTip tips)
        {
            _note = note;
            _store = store;

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BackFor(note.ColorIndex);

            _box = new TextBox();
            _box.Multiline = true;
            _box.BorderStyle = BorderStyle.None;
            _box.WordWrap = true;
            _box.ScrollBars = ScrollBars.None;
            _box.AcceptsReturn = true;
            _box.Font = Theme.FontBody;
            _box.ForeColor = Theme.CardText;
            _box.BackColor = BackColor;
            _box.Text = note.Text == null ? "" : note.Text;
            _box.TextChanged += OnTextChanged;
            _box.Enter += delegate { EventHandler h = FocusedEvent; if (h != null) h(this, EventArgs.Empty); };
            Controls.Add(_box);

            _delete = new BookmarkButton(note);
            _delete.Click += delegate { EventHandler h = DeleteRequested; if (h != null) h(this, EventArgs.Empty); };
            Controls.Add(_delete);

            _color = new ColorBarButton(note);
            _color.MouseDown += OnColorMouseDown;
            _color.MouseMove += OnColorMouseMove;
            _color.MouseUp += OnColorMouseUp;
            Controls.Add(_color);

            if (tips != null)
            {
                tips.SetToolTip(_delete, "Delete this note");
                tips.SetToolTip(_color, "Click to change colour, drag to move the note");
                tips.SetToolTip(_box, "Type here, or use the mic to dictate");
            }

            Relayout();
        }

        public void FocusFirst()
        {
            _box.Focus();
            _box.SelectionStart = 0;
            _box.SelectionLength = 0;
        }

        public void FocusEnd()
        {
            _box.Focus();
            ScrollToEnd();
        }

        /// <summary>Keeps the caret and view at the end without stealing focus.</summary>
        public void ScrollToEnd()
        {
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
        }

        /// <summary>
        /// Shows provisional, while-speaking dictation text. The note is
        /// read-only and slightly muted until the accurate result replaces the
        /// draft, so a keystroke cannot collide with text that is about to be
        /// rewritten.
        /// </summary>
        public void SetDraftMode(bool on)
        {
            if (_draftMode == on)
            {
                if (on) _box.ForeColor = DraftColor();
                return;
            }

            _draftMode = on;
            _box.ReadOnly = on;
            _box.ForeColor = on ? DraftColor() : Theme.CardText;
            _box.BackColor = BackColor;
        }

        private Color DraftColor()
        {
            return Theme.Mix(Theme.CardText, BackColor, 0.38f);
        }

        /// <summary>Sets the text without echoing back into the store.</summary>
        public void SetText(string text)
        {
            if (text == null) text = "";
            if (_box.Text == text) return;

            _syncing = true;
            try { _box.Text = text; }
            finally { _syncing = false; }

            _note.Text = text;
            Relayout();
        }

        public void ApplyColor()
        {
            Color back = Theme.BackFor(_note.ColorIndex);
            BackColor = back;
            _box.BackColor = back;
            if (_draftMode) _box.ForeColor = DraftColor();
            _delete.ApplyColor();
            _color.ApplyColor();
            Invalidate();
            _box.Invalidate();
        }

        private void OnColorMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _colorDown = true;
            _colorDragged = false;
            _colorStartY = Cursor.Position.Y;
            _colorAppliedSteps = 0;
            _color.Capture = true;
        }

        private void OnColorMouseMove(object sender, MouseEventArgs e)
        {
            if (!_colorDown) return;

            int dy = Cursor.Position.Y - _colorStartY;
            if (Math.Abs(dy) > Theme.S(4)) _colorDragged = true;

            // One slot per full slot of vertical travel, so the note moves
            // with the cursor rather than at double speed.
            int threshold = Math.Max(Theme.S(24), Height + Theme.S(10));
            int steps = dy / threshold;
            if (steps == _colorAppliedSteps) return;

            int delta = steps - _colorAppliedSteps;
            _colorAppliedSteps = steps;

            Action<NoteCard, int> h = MoveRequested;
            if (h != null) h(this, delta);
        }

        private void OnColorMouseUp(object sender, MouseEventArgs e)
        {
            if (!_colorDown) return;

            _colorDown = false;
            _color.Capture = false;

            bool dragged = _colorDragged;
            _colorDragged = false;

            if (!dragged)
            {
                EventHandler h = ColorRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private void OnTextChanged(object sender, EventArgs e)
        {
            if (_syncing) return;
            if (_note.Text == _box.Text) return;

            _note.Text = _box.Text;
            _store.Touch(_note);
            Relayout();

            EventHandler h = TextEdited;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) _box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // Drag-free border keeps the sticky note readable on any background.
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.RoundedRect(box, Theme.SF(6f)))
            using (Pen p = new Pen(Theme.EdgeFor(_note.ColorIndex)))
            {
                g.DrawPath(p, path);
            }
        }

        public void RefreshLayout()
        {
            Relayout();
        }

        private void Relayout()
        {
            if (_laying) return;

            string text = _box.Text;

            // Nothing that affects the child layout has changed. This is the
            // common case when a long list is laid out after one note is
            // added, so skip the child-window churn entirely.
            if (_layoutWidth == Width && _layoutHeight == Height
                && string.Equals(_layoutText, text))
                return;

            _laying = true;
            try
            {
                int pad = Theme.S(9);
                int btn = _delete.Width;
                int gap = Theme.S(6);
                int boxW = Width - pad - gap - btn - pad;
                if (boxW < Theme.S(30)) boxW = Theme.S(30);

                if (_box.Width != boxW) _box.Width = boxW;
                _box.Location = new Point(pad, pad);

                // Measuring text is by far the most expensive part of a
                // layout. Only do it when the text or the width changed, so
                // laying out a long list stays cheap.
                int textH;
                if (_measuredWidth != boxW || !string.Equals(_measuredText, text))
                {
                    textH = MeasureHeight(boxW);
                    _measuredWidth = boxW;
                    _measuredHeight = textH;
                    _measuredText = text;
                }
                else
                {
                    textH = _measuredHeight;
                }

                if (_box.Height != textH) _box.Height = textH;

                // The delete control is a bookmark tab on the top edge; the
                // colour bar sits at the bottom centre, far from it.
                int btnX = Width - Theme.S(3) - btn;
                _delete.Location = new Point(btnX, 0);

                int newHeight = textH + pad * 2;
                if (newHeight < Theme.S(46)) newHeight = Theme.S(46);
                if (Height != newHeight) Size = new Size(Width, newHeight);

                _color.Location = new Point((Width - _color.Width) / 2,
                    newHeight - _color.Height - Theme.S(2));

                _layoutWidth = Width;
                _layoutHeight = Height;
                _layoutText = text;
            }
            finally
            {
                _laying = false;
            }

            if (_reportedHeight != Height)
            {
                _reportedHeight = Height;
                EventHandler h = HeightChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private int MeasureHeight(int width)
        {
            string text = _box.Text;
            if (text.Length == 0) text = " ";

            int usable = Math.Max(10, width - Theme.S(6));
            Size size = TextRenderer.MeasureText(
                text,
                _box.Font,
                new Size(usable, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);

            int height = size.Height + Theme.S(8);
            int min = Theme.S(26);
            if (height < min) height = min;
            return height;
        }
    }
}
