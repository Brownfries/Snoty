using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Snoty
{
    internal enum StatusKind { Idle, Busy, Success, Warn, Error }

    /// <summary>
    /// The notes panel that pops up next to the icon. Borderless and always on
    /// top; an outside click closes it unless the pin is on.
    /// </summary>
    internal sealed class NotesPopup : Form, IMessageFilter
    {
        private const int PopupWidth = 344;
        private const int HeaderHeight = 40;
        private const int FooterHeight = 44;
        private const int Pad = 10;

        private readonly NotesStore _store;
        private readonly SpeechInput _speech;
        private readonly WhisperInput _whisper;
        private readonly ToolTip _tips;

        private Panel _header;
        private Panel _body;
        private Panel _footer;
        private IconButton _addButton;
        private IconButton _closeButton;
        private IconButton _pinButton;
        private IconButton _micButton;
        private IconButton _themeButton;
        private bool _pinned;
        private Label _title;
        private Label _status;
        private Label _empty;
        private Timer _saveTimer;
        private Timer _statusTimer;
        private StatusKind _statusKind = StatusKind.Idle;

        // Live (while-speaking) dictation preview.
        private readonly WhisperInput _live;
        private Timer _liveTimer;
        private int _liveLastBytes;
        private int _liveMs;
        private bool _liveRunning;
        private string _liveWav;
        private Note _liveNote;
        private string _liveBase;
        private string _livePreview;
        private bool _liveBlocked;

        private readonly List<NoteCard> _cards = new List<NoteCard>();
        private Note _dictTarget;
        private MicRecorder _mic;
        private string _dictWav;
        private int _dictMs;
        private bool _transcribing;
        private Rectangle _anchor;
        private bool _suppressDeactivate;
        private bool _layoutQueued;
        private int _layoutCardW;
        private bool _wheelFilter;
        private NotesScrollBar _scroll;
        private int _scrollOffset;
        private bool _disposing;
        private readonly uint _processId;
        private Native.MouseHookProc _mouseHookProc;
        private IntPtr _mouseHook;

        public event EventHandler Hidden;
        public event EventHandler DictationChanged;

        public NotesPopup(NotesStore store, SpeechInput speech)
        {
            _store = store;
            _speech = speech;
            _whisper = new WhisperInput();
            _live = new WhisperInput();
            _live.Finished += OnLiveFinished;
            _processId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            _tips = new ToolTip();
            _tips.InitialDelay = 450;
            _tips.ReshowDelay = 100;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Theme.ChromeBorder;
            Text = AppPaths.Name;
            Icon = IconArt.WindowIcon();
            MinimizeBox = false;
            MaximizeBox = false;

            _saveTimer = new Timer();
            _saveTimer.Interval = 450;
            _saveTimer.Tick += delegate
            {
                _saveTimer.Stop();
                _store.SaveIfDirty();
            };

            _statusTimer = new Timer();
            _statusTimer.Interval = 3800;
            _statusTimer.Tick += delegate
            {
                _statusTimer.Stop();
                if (IsDictating) return;
                ShowIdleStatus();
            };

            _liveTimer = new Timer();
            _liveTimer.Interval = 700;
            _liveTimer.Tick += delegate { OnLiveTick(); };

            BuildUi();
            RebuildCards();
            HookSpeech();
            ApplyTheme();

            Theme.Changed += OnThemeChanged;
        }

        public bool IsOpen
        {
            get { return Visible; }
        }

        /// <summary>True while recording, transcribing or using SAPI.</summary>
        public bool IsDictating
        {
            get { return _mic != null || _transcribing || _speech.IsListening; }
        }

        /// <summary>Stops everything immediately; used when the app exits.</summary>
        public void Shutdown()
        {
            RemoveOutsideClickHook();
            RemoveWheelFilter();

            // Restore the text from before the utterance instead of saving a
            // half-finished live draft when the app exits.
            ClearLivePreview();

            MicRecorder mic = _mic;
            _mic = null;
            if (mic != null)
            {
                try { mic.Cancel(); }
                catch { }
                try { mic.Dispose(); }
                catch { }
            }

            try { _whisper.Abort(); }
            catch { }
            DeleteTempWav();
        }

        /// <summary>Adds a note and focuses it (used by the tray menu).</summary>
        public void NewNote()
        {
            AddNote();
        }

        /// <summary>Starts or stops dictation (used by the tray menu).</summary>
        public void StartOrStopDictation()
        {
            ToggleDictation();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.TryRoundCorners(Handle);
        }

        // ------------------------------------------------------------------
        // UI construction
        // ------------------------------------------------------------------

        private void BuildUi()
        {
            _header = new Panel();
            _header.BackColor = Theme.ChromeHeader;
            _header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Separator))
                {
                    e.Graphics.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1);
                }
            };
            Controls.Add(_header);

            _title = new Label();
            _title.Text = AppPaths.Name;
            _title.Font = Theme.FontTitle;
            _title.ForeColor = Theme.TextMain;
            _title.AutoSize = false;
            _title.BackColor = Color.Transparent;
            _title.TextAlign = ContentAlignment.MiddleLeft;
            _header.Controls.Add(_title);

            int headerBtn = Theme.S(26);

            _addButton = new IconButton(GlyphKind.Plus, headerBtn);
            _addButton.BackColor = Theme.ChromeHeader;
            _addButton.GlyphColor = Theme.TextMain;
            _tips.SetToolTip(_addButton, "New note (Ctrl+N)");
            _addButton.Click += delegate { AddNote(); };
            _header.Controls.Add(_addButton);

            _closeButton = new IconButton(GlyphKind.Close, headerBtn);
            _closeButton.BackColor = Theme.ChromeHeader;
            _closeButton.GlyphColor = Theme.TextDim;
            _tips.SetToolTip(_closeButton, "Close (Esc)");
            _closeButton.Click += delegate { HidePopup(); };
            _header.Controls.Add(_closeButton);

            _pinButton = new IconButton(GlyphKind.Pin, headerBtn);
            _pinButton.BackColor = Theme.ChromeHeader;
            _pinButton.GlyphColor = Theme.TextMain;
            _pinButton.ToggleBack = Theme.Accent;
            _tips.SetToolTip(_pinButton, "Keep the panel open");
            _pinButton.Click += delegate { TogglePinned(); };
            _header.Controls.Add(_pinButton);
            _pinButton.BringToFront();

            _body = new Panel();
            _body.BackColor = Theme.ChromeBack;
            _body.AutoScroll = false;
            Controls.Add(_body);

            _scroll = new NotesScrollBar();
            _scroll.ValueChanged += delegate
            {
                if (_scroll == null) return;
                _scrollOffset = _scroll.Value;
                ApplyScrollPositions();
            };
            Controls.Add(_scroll);
            _scroll.BringToFront();

            _empty = new Label();
            _empty.AutoSize = false;
            _empty.TextAlign = ContentAlignment.MiddleCenter;
            _empty.ForeColor = Theme.TextDim;
            _empty.Font = Theme.FontSmall;
            _empty.BackColor = Color.Transparent;
            _empty.Text = "No notes yet.\r\nTap + to add one,\r\nor use the mic to dictate.";
            _body.Controls.Add(_empty);

            _footer = new Panel();
            _footer.BackColor = Theme.ChromeHeader;
            _footer.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Separator))
                {
                    e.Graphics.DrawLine(p, 0, 0, _footer.Width, 0);
                }
            };
            Controls.Add(_footer);

            _micButton = new IconButton(GlyphKind.Mic, Theme.S(30));
            _micButton.Round = true;
            _micButton.BackColor = Theme.ChromeHeader;
            _micButton.GlyphColor = Theme.TextMain;
            _micButton.ToggleBack = Theme.Rec;
            _tips.SetToolTip(_micButton,
                "Dictate into the selected note. Pause to finish; click again to stop early.");
            _micButton.Click += delegate { ToggleDictation(); };
            _footer.Controls.Add(_micButton);

            _status = new Label();
            _status.Font = Theme.FontSmall;
            _status.ForeColor = Theme.TextDim;
            _status.AutoSize = false;
            _status.AutoEllipsis = true;
            _status.BackColor = Color.Transparent;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Text = "Tap the mic to dictate";
            _footer.Controls.Add(_status);

            _themeButton = new IconButton(GlyphKind.Sun, Theme.S(28));
            _themeButton.Round = true;
            _themeButton.BackColor = Theme.ChromeHeader;
            _themeButton.GlyphColor = Theme.TextMain;
            _themeButton.Click += delegate { ToggleTheme(); };
            _footer.Controls.Add(_themeButton);
            UpdateThemeButton();
        }

        /// <summary>Switches between light and dark chrome, and remembers the choice.</summary>
        private void ToggleTheme()
        {
            bool dark = !Theme.IsDark;
            Theme.UseManual(dark);
            UiSettings.SaveTheme(dark ? "dark" : "light");
            UpdateThemeButton();
        }

        private void UpdateThemeButton()
        {
            if (_themeButton == null) return;
            _themeButton.Kind = Theme.IsDark ? GlyphKind.Sun : GlyphKind.Moon;
            _tips.SetToolTip(_themeButton,
                Theme.IsDark ? "Switch to light theme" : "Switch to dark theme");
        }

        /// <summary>Pins the panel open, so outside clicks no longer close it.</summary>
        private void TogglePinned()
        {
            _pinned = !_pinned;
            if (_pinButton != null)
            {
                _pinButton.Toggled = _pinned;
                _pinButton.GlyphColor = Theme.TextMain;
                _tips.SetToolTip(_pinButton,
                    _pinned ? "Unpin the panel" : "Keep the panel open");
            }
        }

        /// <summary>Re-paints the chrome when Windows switches light/dark.</summary>
        private void ApplyTheme()
        {
            if (_disposing || IsDisposed) return;

            BackColor = Theme.ChromeBorder;
            _header.BackColor = Theme.ChromeHeader;
            _body.BackColor = Theme.ChromeBack;
            _footer.BackColor = Theme.ChromeHeader;

            _title.ForeColor = Theme.TextMain;
            _empty.ForeColor = Theme.TextDim;

            _addButton.BackColor = Theme.ChromeHeader;
            _addButton.GlyphColor = Theme.TextMain;
            _closeButton.BackColor = Theme.ChromeHeader;
            _closeButton.GlyphColor = Theme.TextMain;
            _pinButton.BackColor = Theme.ChromeHeader;
            _pinButton.GlyphColor = Theme.TextMain;
            _micButton.BackColor = Theme.ChromeHeader;
            _micButton.GlyphColor = Theme.TextMain;
            _themeButton.BackColor = Theme.ChromeHeader;
            _themeButton.GlyphColor = Theme.TextMain;
            UpdateThemeButton();

            if (_statusKind == StatusKind.Warn) _status.ForeColor = Theme.Warn;
            else if (_statusKind == StatusKind.Error) _status.ForeColor = Theme.Rec;
            else _status.ForeColor = Theme.TextDim;

            _header.Invalidate();
            _footer.Invalidate();
            if (_scroll != null) _scroll.ApplyTheme();
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            SafeUi(ApplyTheme);
        }

        private void ShowIdleStatus()
        {
            SetStatus("Tap the mic to dictate", Theme.TextDim, StatusKind.Idle);
        }

        private void SetStatus(string text, Color color, StatusKind kind)
        {
            _statusKind = kind;
            _status.Text = text == null ? "" : text;
            _status.ForeColor = color;
        }

        /// <summary>Shows a short-lived note and returns to the idle hint.</summary>
        private void SetTransientStatus(string text, Color color, StatusKind kind, int milliseconds)
        {
            SetStatus(text, color, kind);
            _statusTimer.Stop();
            _statusTimer.Interval = milliseconds;
            _statusTimer.Start();
        }

        private void HookSpeech()
        {
            _speech.Started += delegate { SafeUi(OnSpeechStarted); };
            _speech.Stopped += delegate { SafeUi(OnSpeechStopped); };
            _whisper.Finished += OnWhisperFinished;
            _speech.FinalText += delegate(object s, SpeechTextEventArgs e)
            {
                string text = e.Text;
                SafeUi(delegate { OnFinalText(text); });
            };
            _speech.PartialText += delegate(object s, SpeechTextEventArgs e)
            {
                string text = e.Text;
                SafeUi(delegate { OnPartialText(text); });
            };
            _speech.Failed += delegate(object s, SpeechTextEventArgs e)
            {
                string text = e.Text;
                SafeUi(delegate { OnSpeechFailed(text); });
            };
        }

        private void SafeUi(Action action)
        {
            if (_disposing || IsDisposed) return;
            try
            {
                if (InvokeRequired)
                    BeginInvoke(action);
                else
                    action();
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // Show / hide
        // ------------------------------------------------------------------

        public void ShowAt(Rectangle anchor)
        {
            _anchor = anchor;
            _scrollOffset = 0;
            DoLayout();
            PositionNear(anchor);

            _suppressDeactivate = true;
            Show();
            Activate();
            Native.RemoveTaskbarTab(Handle);
            InstallOutsideClickHook();
            InstallWheelFilter();

            if (_cards.Count > 0) _cards[0].FocusFirst();

            BeginInvoke((MethodInvoker)delegate { _suppressDeactivate = false; });
        }

        /// <summary>Moves the open panel with the icon it is anchored to.</summary>
        public void FollowAnchor(Rectangle anchor)
        {
            if (!Visible) return;
            _anchor = anchor;
            PositionNear(anchor);
        }

        public void HidePopup()
        {
            if (!Visible) return;
            RemoveOutsideClickHook();
            RemoveWheelFilter();
            StopDictation();
            _saveTimer.Stop();
            _store.SaveIfDirty();
            Hide();
            Native.RemoveTaskbarTab(Handle);

            EventHandler h = Hidden;
            if (h != null) h(this, EventArgs.Empty);
        }

        // The panel is not the foreground window when it is opened from the
        // taskbar, so Deactivate is unreliable for taskbar clicks. A mouse
        // hook installed only while the panel is visible closes it on any
        // click outside - except our own icon, the tray icon and the panel
        // itself, which keep their normal toggle behaviour.
        private void InstallOutsideClickHook()
        {
            if (_mouseHook != IntPtr.Zero || _disposing || IsDisposed) return;
            _mouseHookProc = OnMouseDownAnywhere;
            _mouseHook = Native.HookMouse(_mouseHookProc);
        }

        private void RemoveOutsideClickHook()
        {
            if (_mouseHook != IntPtr.Zero)
            {
                try { Native.UnhookMouse(_mouseHook); } catch { }
                _mouseHook = IntPtr.Zero;
            }
            _mouseHookProc = null;
        }

        private IntPtr OnMouseDownAnywhere(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && Visible && !_disposing && !IsDisposed && !_pinned)
                {
                    int message = wParam.ToInt32();
                    if (message == 0x0201 || message == 0x0204 || message == 0x0207)
                    {
                        Point point = Native.MouseHookPoint(lParam);
                        if (!Bounds.Contains(point))
                        {
                            IntPtr target = Native.WindowAt(point.X, point.Y);
                            bool ours = Native.BelongsToProcess(target, _processId);
                            if (!ours && !Native.InNotificationArea(point.X, point.Y))
                            {
                                try
                                {
                                    BeginInvoke((MethodInvoker)delegate
                                    {
                                        if (Visible && !_disposing) HidePopup();
                                    });
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return Native.ContinueMouseHook(_mouseHook, nCode, wParam, lParam);
        }

        // The wheel normally goes to the focused note textbox, so the panel
        // would not scroll while the pointer is over a note. This filter
        // scrolls the body whenever the wheel is used inside it.
        private void InstallWheelFilter()
        {
            if (_wheelFilter) return;
            _wheelFilter = true;
            try { Application.AddMessageFilter(this); } catch { }
        }

        private void RemoveWheelFilter()
        {
            if (!_wheelFilter) return;
            _wheelFilter = false;
            try { Application.RemoveMessageFilter(this); } catch { }
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (!Visible || m.Msg != 0x020A) return false;
            try
            {
                long wp = m.WParam.ToInt64();
                int delta = (short)((wp >> 16) & 0xFFFF);
                int notches = delta / 120;
                if (notches == 0) return false;

                long lp = m.LParam.ToInt64();
                int x = (short)(lp & 0xFFFF);
                int y = (short)((lp >> 16) & 0xFFFF);
                if (!_body.RectangleToScreen(_body.ClientRectangle).Contains(x, y))
                    return false;

                if (_scroll != null)
                    _scroll.SetValue(_scroll.Value - notches * Theme.S(60));
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_suppressDeactivate || _pinned) return;

            // Clicking our own floating icon (or its menu) must not close the
            // panel - only switching to another application should.
            if (Native.ForegroundBelongsTo(_processId)) return;

            HidePopup();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HidePopup();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                HidePopup();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.N)
            {
                AddNote();
                e.Handled = true;
            }
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------

        private void LayoutSoon()
        {
            if (_disposing || !IsHandleCreated) return;
            if (_layoutQueued) return;
            _layoutQueued = true;

            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _layoutQueued = false;
                    if (_disposing) return;
                    DoLayout();
                    PositionNear(_anchor);
                });
            }
            catch
            {
                _layoutQueued = false;
            }
        }

        private void DoLayout()
        {
            int width = Theme.S(PopupWidth);
            int headerH = Theme.S(HeaderHeight);
            int footerH = Theme.S(FooterHeight);
            int pad = Theme.S(Pad);
            int innerW = width - 2;
            int barW = _scroll != null ? _scroll.Width : Theme.S(10);

            int fullW = innerW - pad * 2;
            if (fullW < Theme.S(120)) fullW = Theme.S(120);

            int emptyH = Theme.S(74);

            Rectangle work = Screen.FromPoint(
                new Point(_anchor.Left + _anchor.Width / 2, _anchor.Top + _anchor.Height / 2)).WorkingArea;
            int maxBody = Math.Min(Theme.S(520), work.Height - Theme.S(170));
            if (maxBody < Theme.S(110)) maxBody = Theme.S(110);

            // Keep the chosen width between layouts: switching to the
            // scrollbar width and back would re-measure every note on every
            // layout. Only cross the threshold once.
            int cardW = _layoutCardW > 0 ? _layoutCardW : fullW;
            if (cardW > fullW) cardW = fullW;

            int contentH;
            if (_cards.Count == 0)
            {
                cardW = fullW;
                contentH = emptyH + pad * 2;
            }
            else
            {
                contentH = MeasureCards(cardW, pad);

                int desired = contentH > maxBody ? fullW - barW : fullW;
                if (desired < Theme.S(120)) desired = Theme.S(120);
                if (desired != cardW)
                {
                    cardW = desired;
                    contentH = MeasureCards(cardW, pad);
                }
            }
            _layoutCardW = cardW;

            int bodyH = Math.Min(contentH, maxBody);
            if (bodyH < Theme.S(74)) bodyH = Theme.S(74);

            int maxOffset = Math.Max(0, contentH - bodyH);
            if (_scrollOffset < 0) _scrollOffset = 0;
            if (_scrollOffset > maxOffset) _scrollOffset = maxOffset;

            _header.SetBounds(1, 1, innerW, headerH);
            _body.SetBounds(1, 1 + headerH, innerW, bodyH);
            _footer.SetBounds(1, 1 + headerH + bodyH, innerW, footerH);

            if (_scroll != null)
            {
                _scroll.SetBounds(1 + innerW - barW, 1 + headerH, barW, bodyH);
                _scrollOffset = _scroll.SetMetrics(contentH, bodyH, _scrollOffset);
                _scroll.BringToFront();
            }

            if (_cards.Count == 0)
            {
                _empty.Visible = true;
                _empty.SetBounds(pad, pad - _scrollOffset,
                    innerW - pad * 2, emptyH);
            }
            else
            {
                _empty.Visible = false;
            }

            ClientSize = new Size(width, headerH + bodyH + footerH + 2);

            // header children
            int hb = Theme.S(26);
            _closeButton.SetBounds(innerW - Theme.S(8) - hb, (headerH - hb) / 2, hb, hb);
            _addButton.SetBounds(innerW - Theme.S(8) - hb - Theme.S(14) - hb,
                (headerH - hb) / 2, hb, hb);
            _pinButton.SetBounds((innerW - hb) / 2, (headerH - hb) / 2, hb, hb);
            _title.SetBounds(Theme.S(14), 0, innerW - Theme.S(100), headerH);

            // footer children
            int mb = Theme.S(28);
            _micButton.SetBounds(Theme.S(9), (footerH - mb) / 2, mb, mb);

            _themeButton.SetBounds(innerW - Theme.S(9) - mb, (footerH - mb) / 2, mb, mb);

            int statusX = Theme.S(9) + mb + Theme.S(9);
            int statusRight = innerW - Theme.S(9) - mb - Theme.S(8);
            int statusW = statusRight - statusX;
            if (statusW < Theme.S(30)) statusW = Theme.S(30);
            _status.SetBounds(statusX, 0, statusW, footerH);

            ApplyScrollPositions();
        }

        /// <summary>Places the cards at their content position minus the scroll offset.</summary>
        private void ApplyScrollPositions()
        {
            int pad = Theme.S(Pad);

            if (_cards.Count == 0)
            {
                _empty.Visible = true;
                _empty.Location = new Point(pad, pad - _scrollOffset);
                return;
            }

            _empty.Visible = false;
            int y = pad - _scrollOffset;
            for (int i = 0; i < _cards.Count; i++)
            {
                NoteCard card = _cards[i];
                Point at = new Point(pad, y);
                if (card.Location != at) card.Location = at;
                y += card.Height + pad;
            }
        }

        private int MeasureCards(int cardW, int pad)
        {
            int height = pad;
            for (int i = 0; i < _cards.Count; i++)
            {
                NoteCard card = _cards[i];
                card.Width = cardW;
                card.RefreshLayout();
                height += card.Height + pad;
            }
            return height;
        }

        private void PositionNear(Rectangle anchor)
        {
            Size size = Size;
            Screen screen = Screen.FromRectangle(anchor);
            Rectangle bounds = screen.Bounds;
            Rectangle work = screen.WorkingArea;
            int gap = Theme.S(3);

            int x = anchor.Left + anchor.Width / 2 - size.Width / 2;

            // Prefer above the icon; fall back to below.
            int y = anchor.Top - gap - size.Height;
            if (y < work.Top)
            {
                y = anchor.Bottom + gap;
                if (y + size.Height > bounds.Bottom)
                    y = Math.Max(bounds.Top, bounds.Bottom - gap - size.Height);
            }

            if (x + size.Width > bounds.Right) x = bounds.Right - size.Width;
            if (x < bounds.Left) x = bounds.Left;
            if (y < bounds.Top) y = bounds.Top;

            Location = new Point(x, y);
        }

        // ------------------------------------------------------------------
        // Notes
        // ------------------------------------------------------------------

        private NoteCard CreateCard(Note note)
        {
            NoteCard card = new NoteCard(note, _store, _tips);
            card.HeightChanged += OnCardHeightChanged;
            card.TextEdited += OnCardTextEdited;
            card.DeleteRequested += OnCardDelete;
            card.ColorRequested += OnCardColor;
            card.MoveRequested += OnCardMoveRequested;
            card.FocusedEvent += OnCardFocused;
            card.RefreshLayout();
            _body.Controls.Add(card);
            return card;
        }

        private void RebuildCards()
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                NoteCard card = _cards[i];
                _body.Controls.Remove(card);
                card.Dispose();
            }
            _cards.Clear();

            IList<Note> notes = _store.Notes;
            for (int i = 0; i < notes.Count; i++)
                _cards.Add(CreateCard(notes[i]));

            _empty.BringToFront();
        }

        private NoteCard FindCard(Note note)
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                if (object.ReferenceEquals(_cards[i].Model, note)) return _cards[i];
            }
            return null;
        }

        private void AddNote()
        {
            Note note = _store.Add("");
            if (_mic == null && !_transcribing) _dictTarget = note;

            // Insert one card instead of rebuilding the whole list; adding a
            // note stays fast even when the list is long.
            NoteCard card = CreateCard(note);
            _cards.Insert(0, card);
            _empty.BringToFront();

            _scrollOffset = 0;
            DoLayout();
            PositionNear(_anchor);
            ScheduleSave();

            card.FocusFirst();
        }

        private void OnCardHeightChanged(object sender, EventArgs e)
        {
            LayoutSoon();
        }

        private void OnCardTextEdited(object sender, EventArgs e)
        {
            // Editing the note that is being dictated into stops the drafts.
            // Editing a different note is unrelated and must not.
            NoteCard card = sender as NoteCard;
            if (_mic != null && card != null
                && object.ReferenceEquals(card.Model, _liveNote))
                _liveBlocked = true;

            ScheduleSave();
        }

        private void OnCardFocused(object sender, EventArgs e)
        {
            // Keep the utterance pointed at one note even if the user clicks
            // around while it is being recorded or transcribed.
            if (_mic != null || _transcribing) return;

            NoteCard card = sender as NoteCard;
            if (card != null) _dictTarget = card.Model;
        }

        private void OnCardDelete(object sender, EventArgs e)
        {
            NoteCard card = sender as NoteCard;
            if (card == null) return;

            Note note = card.Model;
            if (object.ReferenceEquals(_dictTarget, note)) _dictTarget = null;

            // Drop any live draft reference to this note; the preview path
            // already ignores notes that are no longer in the store.
            if (object.ReferenceEquals(_liveNote, note))
            {
                _liveNote = null;
                _liveBase = null;
                _livePreview = null;
            }

            _store.Remove(note);

            _cards.Remove(card);
            _body.Controls.Remove(card);
            card.Dispose();

            DoLayout();
            PositionNear(_anchor);
            ScheduleSave();
        }

        private void OnCardColor(object sender, EventArgs e)
        {
            NoteCard card = sender as NoteCard;
            if (card == null) return;

            card.Model.ColorIndex = Theme.NextColor(card.Model.ColorIndex);
            card.ApplyColor();
            _store.Touch(card.Model);
            ScheduleSave();
        }

        /// <summary>Reorders a note up or down (drag the colour bar).</summary>
        private void OnCardMoveRequested(NoteCard card, int steps)
        {
            if (card == null || steps == 0) return;

            int from = _cards.IndexOf(card);
            if (from < 0) return;

            int to = from + steps;
            if (to < 0) to = 0;
            if (to >= _cards.Count) to = _cards.Count - 1;
            if (to == from) return;

            _cards.RemoveAt(from);
            _cards.Insert(to, card);

            IList<Note> notes = _store.Notes;
            int noteIndex = notes.IndexOf(card.Model);
            if (noteIndex >= 0)
            {
                notes.RemoveAt(noteIndex);
                notes.Insert(to, card.Model);
            }

            _store.Touch(card.Model);
            ScheduleSave();
            DoLayout();
            PositionNear(_anchor);
        }

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // ------------------------------------------------------------------
        // Dictation
        // ------------------------------------------------------------------

        private void ToggleDictation()
        {
            if (_mic != null)
            {
                // Clicking the mic again stops early and transcribes what was
                // captured; otherwise the recorder auto-stops after a pause.
                FinishWhisperRecording();
                return;
            }

            if (_transcribing)
            {
                SetStatus("Cancelling…", Theme.TextDim, StatusKind.Busy);
                _whisper.Abort();
                return;
            }

            if (_speech.IsListening)
            {
                StopDictation();
                return;
            }

            string reason;
            if (WhisperInput.IsAvailable(out reason))
            {
                StartWhisperDictation();
                return;
            }

            if (!SpeechInput.IsAvailable(out reason))
            {
                SetStatus(reason, Theme.Rec, StatusKind.Error);
                return;
            }

            Note target = TargetNote();
            if (target == null) return;

            _speech.Start();
        }

        private Note TargetNote()
        {
            if (_store.Count == 0) AddNote();

            Note target = _dictTarget;
            if (target == null) target = _store.MostRecent();
            if (target == null) return null;
            _dictTarget = target;

            NoteCard card = FindCard(target);
            if (card != null) card.FocusEnd();
            return target;
        }

        // ------------------------------------------------------------------
        // Whisper dictation
        // ------------------------------------------------------------------

        private void StartWhisperDictation()
        {
            Note target = TargetNote();
            if (target == null) return;

            MicRecorder mic = new MicRecorder();
            mic.AutoStop += delegate
            {
                SafeUi(delegate
                {
                    if (_mic != mic) return;
                    BeginInvoke((MethodInvoker)delegate { FinishWhisperRecording(); });
                });
            };

            if (!mic.Start())
            {
                string error = mic.LastError;
                try { mic.Dispose(); } catch { }
                SetDictationVisual(false, error, Theme.Rec, StatusKind.Error);
                return;
            }

            _mic = mic;
            SetDictationVisual(true, "Listening… speak, then pause", Theme.Rec, StatusKind.Busy);
            StartLivePreview(target);
        }

        /// <summary>Stops capturing and sends the audio to whisper.cpp.</summary>
        private void FinishWhisperRecording()
        {
            MicRecorder mic = _mic;
            if (mic == null) return;
            _mic = null;

            // No more preview updates; keep the draft text around until the
            // accurate result arrives, then it is replaced.
            StopLivePreview(false);

            byte[] pcm = mic.Stop();
            int speechMs = mic.SpeechMilliseconds;
            try { mic.Dispose(); } catch { }

            if (pcm == null || pcm.Length < 12800 || speechMs == 0)
            {
                ClearLivePreview();
                SetDictationVisual(false, "Didn't catch that", Theme.Warn, StatusKind.Warn);
                return;
            }

            string wav;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), AppPaths.Name);
                Directory.CreateDirectory(dir);
                wav = Path.Combine(dir,
                    "dict-" + _processId + "-" + Environment.TickCount + ".wav");
                MicRecorder.SaveWav(wav, pcm);
            }
            catch (Exception ex)
            {
                ClearLivePreview();
                SetDictationVisual(false, "Could not save audio: " + ex.Message, Theme.Rec, StatusKind.Error);
                return;
            }

            DeleteTempWav();
            _dictWav = wav;
            _dictMs = pcm.Length / 32;

            // Set this before starting the worker so a very fast completion
            // cannot be mistaken for a stale result.
            _transcribing = true;

            string error;
            if (!_whisper.Start(wav, out error))
            {
                _transcribing = false;
                DeleteTempWav();
                ClearLivePreview();
                SetDictationVisual(false, error, Theme.Rec, StatusKind.Error);
                return;
            }

            SetDictationVisual(true, "Transcribing…", Theme.Accent, StatusKind.Busy);
        }

        private void OnWhisperFinished(object sender, WhisperResultEventArgs e)
        {
            SafeUi(delegate { CompleteTranscription(e); });
        }

        private void CompleteTranscription(WhisperResultEventArgs e)
        {
            if (!_transcribing) return; // stale result from a previous run
            _transcribing = false;
            DeleteTempWav();

            if (e.Cancelled)
            {
                ClearLivePreview();
                SetDictationVisual(false, "Dictation cancelled", Theme.TextDim, StatusKind.Idle);
                return;
            }

            if (!string.IsNullOrEmpty(e.Error))
            {
                ClearLivePreview();
                SetDictationVisual(false, e.Error, Theme.Rec, StatusKind.Error);
                return;
            }

            string text = DictationText.Clean(e.Text);
            if (text.Length == 0 || WhisperInput.IsLikelyNoise(text, _dictMs))
            {
                ClearLivePreview();
                SetDictationVisual(false, "Didn't catch that", Theme.Warn, StatusKind.Warn);
                return;
            }

            SetDictationVisual(false, "", Theme.TextDim, StatusKind.Idle);
            OnFinalText(text);
        }

        private void DeleteTempWav()
        {
            string path = _dictWav;
            _dictWav = null;
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch { }
        }

        private void SetDictationVisual(bool active, string status, Color color, StatusKind kind)
        {
            _micButton.Toggled = active;
            _micButton.ToggleBack = color == Theme.Accent ? Theme.Accent : Theme.Rec;
            _micButton.Invalidate();

            if (string.IsNullOrEmpty(status))
            {
                _statusTimer.Stop();
                _statusKind = kind;
            }
            else if (kind == StatusKind.Warn)
            {
                SetTransientStatus(status, color, kind, 4200);
            }
            else if (kind == StatusKind.Error)
            {
                SetTransientStatus(status, color, kind, 5200);
            }
            else
            {
                _statusTimer.Stop();
                SetStatus(status, color, kind);
            }

            EventHandler h = DictationChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void StopDictation()
        {
            if (_mic != null)
            {
                FinishWhisperRecording();
                return;
            }
            if (_speech.IsListening) _speech.Stop();

            // A running transcription is left alone so the text is not lost
            // when the panel hides.
        }

        private void OnSpeechStarted()
        {
            _micButton.Kind = GlyphKind.Mic;
            _micButton.ToggleBack = Theme.Rec;
            _micButton.Toggled = true;
            _micButton.Invalidate();
            _statusTimer.Stop();
            SetStatus("Listening… speak now", Theme.Rec, StatusKind.Busy);
            RaiseDictationChanged();
        }

        private void OnSpeechStopped()
        {
            _micButton.Toggled = false;
            _micButton.Invalidate();
            ShowIdleStatus();
            RaiseDictationChanged();
        }

        private void OnSpeechFailed(string message)
        {
            _speech.Stop();
            _micButton.Toggled = false;
            _micButton.Invalidate();
            SetTransientStatus(message, Theme.Rec, StatusKind.Error, 5200);
            RaiseDictationChanged();
        }

        private void RaiseDictationChanged()
        {
            EventHandler h = DictationChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void OnPartialText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _statusTimer.Stop();
            SetStatus("… " + text, Theme.TextDim, StatusKind.Busy);
        }

        private void OnFinalText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            Note target = _dictTarget;
            if (target == null) target = _store.MostRecent();
            if (target == null)
            {
                target = _store.Add("");
                RebuildCards();
            }
            _dictTarget = target;

            string current = target.Text == null ? "" : target.Text;

            // The note may hold a live draft from this utterance. Replace it
            // with the accurate result, joined to the text that was there
            // before the utterance started.
            if (_livePreview != null && object.ReferenceEquals(target, _liveNote))
            {
                current = _liveBase == null ? "" : _liveBase;
                NoteCard card = FindCard(target);
                if (card != null) card.SetText(current);
            }

            _liveNote = null;
            _liveBase = null;
            _livePreview = null;
            _liveLastBytes = 0;
            _liveMs = 0;
            _liveBlocked = false;

            // The whole result lands in one go: no letter-by-letter reveal.
            ApplyFullText(target, DictationText.Join(current, text));
            SetTransientStatus("Added to note", Theme.TextDim, StatusKind.Success, 2400);
        }

        // ------------------------------------------------------------------
        // Live preview while speaking
        //
        // whisper.cpp transcribes a whole file, so the live text is a draft:
        // every second or so the audio captured so far is transcribed and the
        // draft is replaced. The final result then corrects it. Previews are
        // best-effort - any failure just falls back to the old behaviour.
        // ------------------------------------------------------------------

        private void StartLivePreview(Note target)
        {
            if (_live == null || target == null) return;

            _liveNote = target;
            _liveBase = target.Text == null ? "" : target.Text;
            _livePreview = null;
            _liveLastBytes = 0;
            _liveMs = 0;
            _liveBlocked = false;
            _liveTimer.Start();
        }

        private void OnLiveTick()
        {
            MicRecorder mic = _mic;
            if (mic == null || _liveBlocked || _liveRunning) return;

            byte[] pcm;
            try { pcm = mic.Snapshot(); }
            catch { return; }

            // Short clips ending mid-word are where whisper mishears most.
            // Wait for a little more context before the first pass and leave
            // more new audio between passes.
            if (pcm == null || pcm.Length < 38400) return;     // ~1.2 s of audio
            if (pcm.Length - _liveLastBytes < 25600) return;   // >= 0.8 s new
            if (_liveNote == null || !_store.Notes.Contains(_liveNote)) return;

            string wav;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), AppPaths.Name);
                Directory.CreateDirectory(dir);
                wav = Path.Combine(dir,
                    "live-" + _processId + "-" + Environment.TickCount + ".wav");
                MicRecorder.SaveWav(wav, pcm);
            }
            catch
            {
                return;
            }

            string error;
            if (!_live.Start(wav, out error))
            {
                try { File.Delete(wav); } catch { }
                return;
            }

            _liveLastBytes = pcm.Length;
            _liveMs = pcm.Length / 32;
            _liveWav = wav;
            _liveRunning = true;
        }

        private void OnLiveFinished(object sender, WhisperResultEventArgs e)
        {
            string wav = _liveWav;
            _liveWav = null;
            if (!string.IsNullOrEmpty(wav))
            {
                try { File.Delete(wav); } catch { }
            }
            _liveRunning = false;

            if (e == null || e.Cancelled || !string.IsNullOrEmpty(e.Error)) return;

            string text = DictationText.Clean(e.Text);
            if (text.Length == 0 || WhisperInput.IsLikelyNoise(text, _liveMs)) return;

            SafeUi(delegate { ShowLivePreview(text); });
        }

        private void ShowLivePreview(string preview)
        {
            if (_liveBlocked || _mic == null || _liveNote == null) return;
            if (!_store.Notes.Contains(_liveNote)) return;

            _livePreview = preview;

            // A clip that ends mid-word regularly decodes its last word
            // wrongly. The accurate final pass supplies it, so hold the last
            // word out of the draft and let the next pass confirm it.
            string shown = preview;
            int cut = preview.LastIndexOf(' ');
            if (cut > 0) shown = preview.Substring(0, cut);

            string full = DictationText.Join(_liveBase, shown);

            NoteCard card = FindCard(_liveNote);
            if (card != null)
            {
                card.SetText(full);
                card.SetDraftMode(true);
                card.ScrollToEnd();
            }
            else
            {
                _liveNote.Text = full;
            }
        }

        private void StopLivePreview(bool clear)
        {
            if (_liveTimer != null) _liveTimer.Stop();

            if (_live != null && _live.IsRunning)
            {
                try { _live.Abort(); } catch { }
            }
            _liveRunning = false;

            if (!string.IsNullOrEmpty(_liveWav))
            {
                try { File.Delete(_liveWav); } catch { }
                _liveWav = null;
            }

            if (clear)
            {
                _liveNote = null;
                _liveBase = null;
                _livePreview = null;
                _liveLastBytes = 0;
                _liveMs = 0;
                _liveBlocked = false;
            }
        }

        /// <summary>Drops a live draft and restores the note text it started from.</summary>
        private void ClearLivePreview()
        {
            if (_livePreview != null && _liveNote != null && _store.Notes.Contains(_liveNote))
            {
                // Only restore when a draft actually replaced the note text;
                // otherwise this would undo an edit made before the first
                // preview arrived.
                string restore = _liveBase == null ? "" : _liveBase;
                NoteCard card = FindCard(_liveNote);
                if (card != null)
                {
                    card.SetText(restore);
                    card.SetDraftMode(false);
                }
                else
                {
                    _liveNote.Text = restore;
                }
            }

            StopLivePreview(true);
        }

        // ------------------------------------------------------------------
        // Applying dictation results
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes the completed text into the note and saves it. Dictation
        /// results are applied whole, in a single update.
        /// </summary>
        private void ApplyFullText(Note target, string full)
        {
            if (target == null) return;

            target.Text = full == null ? "" : full;
            _store.Touch(target);

            NoteCard card = FindCard(target);
            if (card != null)
            {
                card.SetText(target.Text);
                card.SetDraftMode(false);
                card.FocusEnd();
            }

            DoLayout();
            PositionNear(_anchor);
            ScheduleSave();
        }

        // ------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposing = true;
                Theme.Changed -= OnThemeChanged;
                try { Shutdown(); } catch { }
                if (_whisper != null)
                {
                    try { _whisper.Dispose(); } catch { }
                }
                if (_live != null)
                {
                    try { _live.Finished -= OnLiveFinished; } catch { }
                    try { _live.Dispose(); } catch { }
                }
                if (_liveTimer != null) _liveTimer.Dispose();
                if (_saveTimer != null) _saveTimer.Dispose();
                if (_statusTimer != null) _statusTimer.Dispose();
                if (_tips != null) _tips.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
