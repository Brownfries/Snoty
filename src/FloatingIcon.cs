using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Snoty
{
    /// <summary>
    /// The sticky-note icon. It normally lives inside the taskbar as a native
    /// child window, and turns into the draggable always-on-top window as soon
    /// as it is pulled off the bar.
    /// </summary>
    internal sealed class FloatingIcon : Form
    {
        private readonly NotesStore _store;
        private readonly SpeechInput _speech;
        private readonly uint _processId;

        private Bitmap _surface;
        private int _count = -1;
        private bool _listening;

        private bool _down;
        private bool _dragging;
        private Point _dragMouseStart;
        private Point _dragFormStart;
        private bool _popupWasOpen;
        private Point _lastFloatLocation;

        private bool _dodged;
        private Point _dodgeHome;
        private IntPtr _dodgeCover = IntPtr.Zero;

        private readonly Timer _topTimer;
        private int _lastClampCheck;
        private int _dockCheckTick;

        private DockIconWindow _dock;
        private TaskbarInfo _dockTaskbar;
        private Rectangle _dockRect;
        private Point _dockGrab;
        private Rectangle _lastDockScreen;

        private readonly Timer _dragPoll;
        private bool _pollDragging;
        private Point _pollGrab;

        private readonly Timer _recoverTimer;
        private int _recovering;
        private int _taskbarMessage;
        private bool _recoverQueued;
        private bool _started;
        private bool _runtimeStarted;

        /// <summary>Reports dictation state (whisper or SAPI) for the app mark.</summary>
        public Func<bool> IsDictating;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint EVENT_OBJECT_HIDE = 0x8003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        private Native.WinEventDelegate _winEventCallback;
        private IntPtr _hookForeground;
        private IntPtr _hookShowHide;
        private bool _topQueued;

        /// <summary>Supplied by the app so a click on the icon can toggle the popup.</summary>
        public Func<bool> IsPopupOpen;

        public event EventHandler ShowPopupRequested;
        public event EventHandler HidePopupRequested;

        /// <summary>Raised whenever the icon moves, including while dragging.</summary>
        public event EventHandler Moved;

        public FloatingIcon(NotesStore store, SpeechInput speech)
        {
            _store = store;
            _speech = speech;
            _processId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            Size = new Size(Theme.S(44), Theme.S(44));
            Cursor = Cursors.Hand;
            Text = AppPaths.Name;
            Icon = IconArt.WindowIcon();
            DoubleBuffered = true;

            Point saved;
            if (UiSettings.TryLoadIconPos(out saved) && IsOnSomeScreen(saved, Size))
                Location = saved;
            else
                Location = DefaultLocation();

            _lastFloatLocation = Location;

            _store.Changed += delegate { RefreshMark(); };
            _speech.Started += delegate { SafeRefresh(); };
            _speech.Stopped += delegate { SafeRefresh(); };

            _topTimer = new Timer();
            _topTimer.Interval = 250;
            _topTimer.Tick += delegate { OnTopTick(); };

            _dragPoll = new Timer();
            _dragPoll.Interval = 15;
            _dragPoll.Tick += delegate { OnDragPollTick(); };

            _recoverTimer = new Timer();
            _recoverTimer.Interval = 600;
            _recoverTimer.Tick += delegate { OnRecoverTick(); };

            _taskbarMessage = Native.RegisterWindowMessageId("TaskbarCreated");
        }

        public bool IsDocked
        {
            get { return _dock != null && _dock.IsValid; }
        }

        public Rectangle AnchorBounds()
        {
            if (_dock != null)
            {
                Rectangle docked;
                if (_dock.TryGetScreenBounds(out docked)) return docked;
            }

            Rectangle rect;
            if (Native.WindowBounds(Handle, out rect)) return rect;
            return Bounds;
        }

        /// <summary>Puts the icon back where it belongs: docked, or the default corner.</summary>
        public void ResetPosition()
        {
            _dodged = false;
            _dodgeCover = IntPtr.Zero;

            if (_dock != null)
            {
                TaskbarInfo tb = TaskbarDock.Primary();
                Rectangle? slot = tb == null ? null : TaskbarDock.Place(tb, Size, null, _dock.Handle);
                if (slot.HasValue)
                {
                    _dock.Place(tb.Bounds, slot.Value);
                    _dockTaskbar = tb;
                    _dockRect = slot.Value;
                    _lastDockScreen = slot.Value;
                    UiSettings.SaveMode(true, _lastFloatLocation);
                    return;
                }

                FloatFromDock();
                return;
            }

            if (UiSettings.Dock && TryDock(null)) return;

            Location = DefaultLocation();
            ClampOffTaskbar();
            _lastFloatLocation = Location;
            UiSettings.SaveMode(false, Location);
        }

        /// <summary>Brings the floating icon back onto the taskbar.</summary>
        public bool DockToTaskbar()
        {
            if (_dock != null) return true;

            // Force the handle so this works before the window is ever shown.
            // Showing a form only to hide it again is what can leave a stray
            // taskbar button behind.
            if (!IsHandleCreated)
            {
                IntPtr unused = Handle;
            }

            return TryDock(null);
        }

        /// <summary>Takes the icon out of the taskbar and shows it just above it.</summary>
        public void FloatFromDock()
        {
            if (_dock == null) return;

            Rectangle rect;
            if (!_dock.TryGetScreenBounds(out rect)) rect = AnchorBounds();

            Rectangle work = Screen.FromRectangle(rect).WorkingArea;
            int x = rect.Left + rect.Width / 2 - Width / 2;
            int y = rect.Top - Height - Theme.S(8);
            if (y < work.Top) y = work.Top;
            if (x < work.Left) x = work.Left;
            if (x + Width > work.Right) x = work.Right - Width;

            Undock(new Point(x, y), false);
            _dodged = false;
            _dodgeCover = IntPtr.Zero;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Render();
            StartRuntime();
        }

        private void StartRuntime()
        {
            if (_runtimeStarted) return;
            _runtimeStarted = true;
            InstallTopHooks();
            _topTimer.Start();
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            EventHandler moved = Moved;
            if (moved != null) moved(this, EventArgs.Empty);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            Render();

            // Only the very first show auto-docks; later shows are a pop-out
            // or a recovery and must not snap straight back onto the bar.
            if (!_started)
            {
                _started = true;
                if (_dock != null)
                {
                    if (Visible) Hide();
                    return;
                }
                if (UiSettings.Dock && TryDock(null)) return;
            }

            EnsureVisible();
            Render();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _topTimer.Stop();
            _dragPoll.Stop();
            _recoverTimer.Stop();
            RemoveTopHooks();
            DisposeDock();
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (_taskbarMessage != 0 && m.Msg == _taskbarMessage)
                QueueTaskbarRecovery();

            base.WndProc(ref m);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Layered window: content comes from UpdateLayeredWindow.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Layered window: content comes from UpdateLayeredWindow.
        }

        // ------------------------------------------------------------------

        private Point DefaultLocation()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int margin = Theme.S(6);
            return new Point(wa.Right - Width - margin, wa.Bottom - Height - margin);
        }

        private static bool IsOnSomeScreen(Point location, Size size)
        {
            Rectangle r = new Rectangle(location, size);
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle overlap = Rectangle.Intersect(screens[i].Bounds, r);
                if (overlap.Width * overlap.Height > 0) return true;
            }
            return false;
        }

        private void EnsureVisible()
        {
            if (!IsOnSomeScreen(Location, Size))
            {
                Location = DefaultLocation();
                return;
            }
            SnapIntoView();
            ClampOffTaskbar();
            _lastFloatLocation = Location;
        }

        public void RefreshMark()
        {
            int count = _store.Count;
            bool listening = _speech.IsListening
                || (IsDictating != null && IsDictating());
            if (count == _count && listening == _listening) return;
            _count = count;
            _listening = listening;
            Render();
            if (_dock != null) _dock.SetState(_listening, _count);
        }

        private void SafeRefresh()
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired)
                    BeginInvoke((MethodInvoker)RefreshMark);
                else
                    RefreshMark();
            }
            catch
            {
            }
        }

        private void Render()
        {
            if (!IsHandleCreated) return;

            int w = Width;
            int h = Height;
            if (w <= 0 || h <= 0) return;

            Bitmap bmp = IconArt.Render(w, h, _listening, _count);
            if (bmp == null) return;

            Bitmap old = _surface;
            _surface = bmp;
            Native.Blit(Handle, _surface);
            if (old != null) old.Dispose();
        }

        // ------------------------------------------------------------------
        // Docking inside Shell_TrayWnd
        // ------------------------------------------------------------------

        private bool TryDock(int? nearX)
        {
            if (!IsHandleCreated || IsDisposed) return false;

            TaskbarInfo tb = TaskbarDock.Primary();
            if (tb == null) return false;

            Rectangle? slot = TaskbarDock.Place(tb, Size, nearX, IntPtr.Zero);
            if (!slot.HasValue) return false;

            return DockAt(tb, slot.Value);
        }

        private bool DockAt(TaskbarInfo taskbar, Rectangle screenRect)
        {
            // Remember a floating position the user can return to. If the icon
            // was dragged onto the bar, its current location is on the taskbar
            // and a poor restore point.
            if (Visible && !TaskbarDock.IsOverTaskbar(Location))
                _lastFloatLocation = Location;

            DockIconWindow dock = new DockIconWindow();
            if (!dock.Create(taskbar.Handle, taskbar.Bounds, screenRect))
            {
                dock.Dispose();
                return false;
            }

            dock.Clicked += OnDockClicked;
            dock.MenuRequested += OnDockMenu;
            dock.DragStarted += OnDockDragStarted;
            dock.DragMoved += OnDockDragMoved;
            dock.DragEnded += OnDockDragEnded;
            dock.Moved += OnDockMoved;
            dock.SetState(_listening, _count);

            DockIconWindow old = _dock;
            _dock = dock;
            _dockTaskbar = taskbar;
            _dockRect = screenRect;
            _lastDockScreen = screenRect;

            _dodged = false;
            _dodgeCover = IntPtr.Zero;

            if (old != null) old.Dispose();
            if (Visible) Hide();

            // A hidden tool window should not own a taskbar button, but the
            // shell can keep one from when the form was visible. Remove it.
            Native.RemoveTaskbarTab(Handle);

            UiSettings.SaveMode(true, _lastFloatLocation);
            OnDockMoved();
            return true;
        }

        private void Undock(Point screenLocation, bool keepDragging)
        {
            DisposeDock();

            _lastFloatLocation = screenLocation;
            Location = screenLocation;
            TopMost = true;
            Show();
            Native.KeepOnTop(Handle);
            Native.RemoveTaskbarTab(Handle);
            Render();

            UiSettings.SaveMode(false, screenLocation);

            if (keepDragging)
            {
                _pollGrab = _dockGrab;
                _pollDragging = true;
                _dragPoll.Start();
            }
        }

        private void DisposeDock()
        {
            DockIconWindow dock = _dock;
            _dock = null;
            _dockTaskbar = null;
            if (dock != null)
            {
                try { dock.Dispose(); }
                catch { }
            }
        }

        private void OnDockClicked()
        {
            if (IsPopupOpen != null && IsPopupOpen())
            {
                EventHandler hide = HidePopupRequested;
                if (hide != null) hide(this, EventArgs.Empty);
            }
            else
            {
                EventHandler show = ShowPopupRequested;
                if (show != null) show(this, EventArgs.Empty);
            }
        }

        private void OnDockMenu()
        {
            ContextMenuStrip menu = ContextMenuStrip;
            if (menu == null) return;
            try { menu.Show(Cursor.Position); }
            catch { }
        }

        private void OnDockMoved()
        {
            EventHandler moved = Moved;
            if (moved != null) moved(this, EventArgs.Empty);
        }

        private void OnDockDragStarted()
        {
            // Keep an open panel with the icon: the app repositions it on
            // every icon move, including the undock/dock transition.
            Rectangle rect;
            Point pressed = _dock != null ? _dock.PressedScreenPoint : Cursor.Position;
            if (_dock != null && _dock.TryGetScreenBounds(out rect))
                _dockGrab = new Point(pressed.X - rect.Left, pressed.Y - rect.Top);
            else
                _dockGrab = new Point(Width / 2, Height / 2);
        }

        private void OnDockDragMoved(Point cursor)
        {
            if (_dock == null) return;
            if (!_dock.IsValid)
            {
                DisposeDock();
                ShowFloatingAfterLostDock();
                return;
            }

            TaskbarInfo tb = TaskbarDock.Primary();
            if (tb == null) return;

            if (!tb.Bounds.Contains(cursor))
            {
                // Pulled off the bar: become the floating window and keep the
                // icon under the cursor at the same relative spot.
                Undock(new Point(cursor.X - _dockGrab.X, cursor.Y - _dockGrab.Y), true);
                return;
            }

            Rectangle? slot = TaskbarDock.Place(tb, Size, cursor.X, _dock.Handle);
            if (slot.HasValue)
            {
                _dock.Place(tb.Bounds, slot.Value);
                _dockTaskbar = tb;
                _dockRect = slot.Value;
                _lastDockScreen = slot.Value;
            }
        }

        private void OnDockDragEnded(Point cursor)
        {
            if (_dock == null) return;

            TaskbarInfo tb = TaskbarDock.Primary();
            if (tb != null && tb.Bounds.Contains(cursor))
            {
                Rectangle? slot = TaskbarDock.Place(tb, Size, cursor.X, _dock.Handle);
                if (slot.HasValue)
                {
                    _dock.Place(tb.Bounds, slot.Value);
                    _dockTaskbar = tb;
                    _dockRect = slot.Value;
                    _lastDockScreen = slot.Value;
                    UiSettings.SaveMode(true, _lastFloatLocation);
                    return;
                }
            }

            Point at = new Point(cursor.X - _dockGrab.X, cursor.Y - _dockGrab.Y);
            Undock(at, false);
            SnapIntoView();
            ClampOffTaskbar();
            _lastFloatLocation = Location;
            UiSettings.SaveMode(false, Location);
        }

        private void OnDragPollTick()
        {
            if (!_pollDragging)
            {
                _dragPoll.Stop();
                return;
            }

            if (!Native.IsLeftButtonDown())
            {
                _pollDragging = false;
                _dragPoll.Stop();
                FinishFloatingDrop();
                return;
            }

            Point p = Cursor.Position;
            Location = new Point(p.X - _pollGrab.X, p.Y - _pollGrab.Y);
        }

        private void FinishFloatingDrop()
        {
            Point p = Cursor.Position;
            if (TaskbarDock.IsOverTaskbar(p) && TryDock(p.X)) return;

            SnapIntoView();
            ClampOffTaskbar();
            _lastFloatLocation = Location;
            UiSettings.SaveMode(false, Location);
        }

        private void ShowFloatingAfterLostDock()
        {
            Rectangle target = _lastDockScreen;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            if (target.Width > 0)
                work = Screen.FromRectangle(target).WorkingArea;

            int x = target.Width > 0 ? target.Left : work.Right - Width - Theme.S(6);
            int y = target.Width > 0 ? target.Top - Height - Theme.S(8)
                                     : work.Bottom - Height - Theme.S(6);
            if (x + Width > work.Right) x = work.Right - Width;
            if (x < work.Left) x = work.Left;
            if (y < work.Top) y = work.Top;

            Location = new Point(x, y);
            if (!Visible) Show();
            TopMost = true;
            _lastFloatLocation = Location;
            Render();
        }

        private void OnTopTick()
        {
            if (_dock != null)
            {
                // Never restack the child window while the user is dragging it:
                // that can cost us mouse capture halfway through the gesture.
                if (_dock.IsDragging) return;

                _dockCheckTick++;
                if (_dockCheckTick >= 4)
                {
                    _dockCheckTick = 0;
                    ValidateDock();
                }
                return;
            }

            EnsureOnTop();
        }

        /// <summary>
        /// Moves the docked icon if another app (Pulsar, for example) has taken
        /// its spot, or if the taskbar moved. Docked, it never needs z-order
        /// games, because it shares the taskbar's own window band.
        /// </summary>
        private void ValidateDock()
        {
            if (_dock == null) return;

            if (!_dock.IsValid)
            {
                DisposeDock();
                ShowFloatingAfterLostDock();
                return;
            }

            TaskbarInfo tb = TaskbarDock.Primary();
            if (tb == null)
            {
                Rectangle rect;
                if (_dock.TryGetScreenBounds(out rect)) _lastDockScreen = rect;
                DisposeDock();
                ShowFloatingAfterLostDock();
                return;
            }

            Rectangle current;
            if (!_dock.TryGetScreenBounds(out current))
            {
                DisposeDock();
                ShowFloatingAfterLostDock();
                return;
            }

            bool barChanged = _dockTaskbar == null || _dockTaskbar.Handle != tb.Handle
                || _dockTaskbar.Bounds != tb.Bounds;

            List<Rectangle> occupied = TaskbarDock.Occupied(tb, _dock.Handle);
            bool clear = TaskbarDock.IsClear(current, tb.Bounds, occupied);

            if (!barChanged && clear)
            {
                _dockTaskbar = tb;
                _dockRect = current;
                _lastDockScreen = current;
                _dock.BringToFront();
                return;
            }

            Rectangle? slot = TaskbarDock.Place(tb, Size,
                barChanged ? (int?)null : current.Left + current.Width / 2,
                _dock.Handle);
            if (slot.HasValue)
            {
                _dock.Place(tb.Bounds, slot.Value);
                _dock.BringToFront();
                _dockTaskbar = tb;
                _dockRect = slot.Value;
                _lastDockScreen = slot.Value;
                return;
            }

            // No empty gap remains: sit just above the bar instead of covering
            // another app's taskbar widget.
            int x = current.Left;
            int y = tb.Bounds.Top - Height - Theme.S(6);
            if (x < tb.Bounds.Left) x = tb.Bounds.Left;
            if (x + Width > tb.Bounds.Right) x = tb.Bounds.Right - Width;
            if (y < 0) y = 0;

            Undock(new Point(x, y), false);
        }

        // ------------------------------------------------------------------
        // Recovery after Explorer restarts, which destroys its child windows
        // ------------------------------------------------------------------

        private void QueueTaskbarRecovery()
        {
            if (_recoverQueued || IsDisposed || !IsHandleCreated) return;
            _recoverQueued = true;

            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _recoverQueued = false;
                    BeginTaskbarRecovery();
                });
            }
            catch
            {
                _recoverQueued = false;
            }
        }

        private void BeginTaskbarRecovery()
        {
            if (_recovering > 0) return;

            Rectangle lost = _dockRect;
            if (lost.Width > 0) _lastDockScreen = lost;

            DisposeDock();

            // Make sure the user still sees the icon while Explorer rebuilds.
            if (!Visible)
            {
                ShowFloatingAfterLostDock();
            }

            _recovering = 12;
            _recoverTimer.Start();
        }

        private void OnRecoverTick()
        {
            if (_recovering > 0) _recovering--;

            if (UiSettings.Dock && TryDock(null))
            {
                _recovering = 0;
                _recoverTimer.Stop();
                return;
            }

            if (_recovering <= 0) _recoverTimer.Stop();
        }

        // ------------------------------------------------------------------
        // Keeping the floating icon above the taskbar without fighting flyouts
        // ------------------------------------------------------------------

        private void InstallTopHooks()
        {
            if (_winEventCallback != null) return;
            try
            {
                _winEventCallback = OnWinEvent;
                uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;

                _hookForeground = Native.SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, _winEventCallback, 0, 0, flags);

                _hookShowHide = Native.SetWinEventHook(
                    EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE,
                    IntPtr.Zero, _winEventCallback, 0, 0, flags);
            }
            catch
            {
            }
        }

        private void RemoveTopHooks()
        {
            try { if (_hookForeground != IntPtr.Zero) Native.UnhookWinEvent(_hookForeground); }
            catch { }
            try { if (_hookShowHide != IntPtr.Zero) Native.UnhookWinEvent(_hookShowHide); }
            catch { }

            _hookForeground = IntPtr.Zero;
            _hookShowHide = IntPtr.Zero;
            _winEventCallback = null;
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint thread, uint time)
        {
            if (_dock != null) return;
            QueueEnsureOnTop();
        }

        private void QueueEnsureOnTop()
        {
            if (_topQueued || IsDisposed || !IsHandleCreated) return;

            _topQueued = true;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _topQueued = false;
                    if (!IsDisposed && _dock == null) EnsureOnTop();
                });
            }
            catch
            {
                _topQueued = false;
            }
        }

        /// <summary>
        /// Puts the floating icon back above the taskbar. Both the taskbar and
        /// the icon live in the always-on-top band, so whenever the shell raises
        /// itself the icon can sink below it. Re-asserting plain topmost is a
        /// no-op for an already-topmost window, so insert directly above the
        /// window that is covering us instead.
        /// </summary>
        private void EnsureOnTop()
        {
            if (!IsHandleCreated || IsDisposed) return;
            if (_dock != null) return;
            if (_down || _pollDragging) return; // never fight an in-progress drag

            int now = Environment.TickCount;
            if (now - _lastClampCheck > 2000 || now < _lastClampCheck)
            {
                _lastClampCheck = now;
                ClampOffTaskbar();
            }

            if (IsPopupOpen != null && IsPopupOpen()) return;

            if (_dodged)
            {
                if (DodgeCoverGone())
                {
                    _dodged = false;
                    _dodgeCover = IntPtr.Zero;
                    Location = _dodgeHome;
                    ClampOffTaskbar();
                }
                return;
            }

            IntPtr foreground = Native.ForegroundHandle();
            if (foreground != IntPtr.Zero && foreground != Handle
                && Native.BelongsToProcess(foreground, _processId))
                return;

            IntPtr under = Native.WindowAt(Left + Width / 2, Top + Height / 2);
            if (under == Handle || under == IntPtr.Zero) return;

            if (!IsIconVisible())
            {
                DodgeFrom(under);
                return;
            }

            IntPtr root = Native.RootOf(under);
            if (Native.IsTopMost(root) && Native.RaiseAbove(Handle, root)) return;

            Native.KeepOnTop(Handle);
        }

        // ------------------------------------------------------------------
        // Dodging opaque shell surfaces that refuse to be raised above
        // ------------------------------------------------------------------

        private bool DodgeCoverGone()
        {
            if (_dodgeCover == IntPtr.Zero || !Native.IsVisible(_dodgeCover)) return true;

            IntPtr under = Native.WindowAt(
                _dodgeHome.X + Width / 2, _dodgeHome.Y + Height / 2);
            if (under == IntPtr.Zero || under == Handle) return true;
            if (under == _dodgeCover) return false;

            return Native.RootOf(under) != Native.RootOf(_dodgeCover);
        }

        private static readonly int[] VisibleX = { 12, 20, 12, 22, 14, 24 };
        private static readonly int[] VisibleY = { 10, 10, 15, 15, 34, 34 };

        private bool IsIconVisible()
        {
            if (_surface == null || !IsHandleCreated) return true;

            int tested = 0;
            int matches = 0;

            for (int i = 0; i < VisibleX.Length; i++)
            {
                int x = VisibleX[i];
                int y = VisibleY[i];
                if (x >= _surface.Width || y >= _surface.Height) continue;

                Color expected = _surface.GetPixel(x, y);
                if (expected.A < 200) continue;
                tested++;

                int r, g, b;
                if (!Native.ScreenPixel(Left + x, Top + y, out r, out g, out b)) continue;

                if (Math.Abs(r - expected.R) <= 30
                    && Math.Abs(g - expected.G) <= 30
                    && Math.Abs(b - expected.B) <= 30)
                    matches++;
            }

            if (tested == 0) return true;
            return matches >= tested - 1;
        }

        private void DodgeFrom(IntPtr cover)
        {
            IntPtr root = Native.RootOf(cover);
            Rectangle coverRect;
            if (!Native.WindowBounds(root, out coverRect)) return;

            Point center = new Point(Left + Width / 2, Top + Height / 2);

            IntPtr fg = Native.ForegroundHandle();
            Rectangle fgRect;
            if (fg != IntPtr.Zero && fg != Handle
                && !Native.BelongsToProcess(fg, _processId)
                && Native.WindowBounds(fg, out fgRect)
                && fgRect.Width * fgRect.Height < coverRect.Width * coverRect.Height
                && fgRect.Left <= center.X && fgRect.Right >= center.X
                && fgRect.Top <= center.Y && fgRect.Bottom >= center.Y)
            {
                coverRect = fgRect;
            }

            Rectangle screen = Screen.FromPoint(center).Bounds;
            Rectangle icon = Bounds;
            Rectangle[] bars = Native.TaskbarRects();

            Point best = Location;
            int bestDistance = int.MaxValue;

            TryDodge(new Point(icon.Left, coverRect.Top - icon.Height), screen, icon, coverRect, bars, ref best, ref bestDistance);
            TryDodge(new Point(icon.Left, coverRect.Bottom), screen, icon, coverRect, bars, ref best, ref bestDistance);
            TryDodge(new Point(coverRect.Left - icon.Width, icon.Top), screen, icon, coverRect, bars, ref best, ref bestDistance);
            TryDodge(new Point(coverRect.Right, icon.Top), screen, icon, coverRect, bars, ref best, ref bestDistance);
            TryDodge(new Point(screen.Right - icon.Width, screen.Top), screen, icon, coverRect, bars, ref best, ref bestDistance);
            TryDodge(new Point(screen.Left, screen.Top), screen, icon, coverRect, bars, ref best, ref bestDistance);

            if (bestDistance == int.MaxValue) return;

            _dodgeHome = Location;
            _dodgeCover = cover;
            _dodged = true;
            Location = best;
        }

        private static void TryDodge(Point candidate, Rectangle screen, Rectangle icon,
            Rectangle cover, Rectangle[] bars, ref Point best, ref int bestDistance)
        {
            int x = candidate.X;
            int y = candidate.Y;
            if (x < screen.Left) x = screen.Left;
            if (y < screen.Top) y = screen.Top;
            if (x + icon.Width > screen.Right) x = screen.Right - icon.Width;
            if (y + icon.Height > screen.Bottom) y = screen.Bottom - icon.Height;

            Rectangle test = new Rectangle(x, y, icon.Width, icon.Height);
            if (IntersectsAny(test, new Rectangle[] { cover })) return;
            if (IntersectsAny(test, bars)) return;

            int dx = x - icon.Left;
            int dy = y - icon.Top;
            int distance = (dx < 0 ? -dx : dx) + (dy < 0 ? -dy : dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = new Point(x, y);
            }
        }

        // ------------------------------------------------------------------
        // Dragging and clicking the floating icon
        // ------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            _down = true;
            _dragging = false;
            _dragMouseStart = Cursor.Position;
            _dragFormStart = Location;
            _popupWasOpen = IsPopupOpen != null && IsPopupOpen();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_down) return;

            Point now = Cursor.Position;
            int dx = now.X - _dragMouseStart.X;
            int dy = now.Y - _dragMouseStart.Y;

            if (!_dragging && (Math.Abs(dx) > Theme.S(4) || Math.Abs(dy) > Theme.S(4)))
                _dragging = true;

            if (_dragging)
                Location = new Point(_dragFormStart.X + dx, _dragFormStart.Y + dy);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (!_down) return;

            bool dragged = _dragging;
            _down = false;
            _dragging = false;

            if (dragged)
            {
                _dodged = false;
                _dodgeCover = IntPtr.Zero;

                if (TaskbarDock.IsOverTaskbar(Cursor.Position) && TryDock(Cursor.Position.X))
                    return;

                SnapIntoView();
                ClampOffTaskbar();
                _lastFloatLocation = Location;
                UiSettings.SaveMode(false, Location);
                return;
            }

            if (_popupWasOpen)
            {
                EventHandler hide = HidePopupRequested;
                if (hide != null) hide(this, EventArgs.Empty);
            }
            else
            {
                EventHandler show = ShowPopupRequested;
                if (show != null) show(this, EventArgs.Empty);
            }
        }

        private void SnapIntoView()
        {
            Rectangle bounds = Screen.FromRectangle(Bounds).Bounds;
            Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
            int snap = Theme.S(12);

            int x = Left;
            int y = Top;

            if (Math.Abs(x - bounds.Left) <= snap) x = bounds.Left;
            if (Math.Abs((x + Width) - bounds.Right) <= snap) x = bounds.Right - Width;
            if (Math.Abs(y - bounds.Top) <= snap) y = bounds.Top;
            if (Math.Abs((y + Height) - bounds.Bottom) <= snap) y = bounds.Bottom - Height;
            if (Math.Abs((y + Height) - work.Bottom) <= snap) y = work.Bottom - Height;

            if (x < bounds.Left) x = bounds.Left;
            if (y < bounds.Top) y = bounds.Top;
            if (x + Width > bounds.Right) x = bounds.Right - Width;
            if (y + Height > bounds.Bottom) y = bounds.Bottom - Height;

            Location = new Point(x, y);
        }

        /// <summary>Nudges a floating icon out of any taskbar rectangle.</summary>
        private void ClampOffTaskbar()
        {
            Rectangle icon = Bounds;
            Rectangle[] bars = Native.TaskbarRects();
            if (!IntersectsAny(icon, bars)) return;

            Rectangle screen = Screen.FromPoint(
                new Point(icon.Left + icon.Width / 2, icon.Top + icon.Height / 2)).Bounds;

            Point best = Location;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < bars.Length; i++)
            {
                Rectangle bar = bars[i];

                TryClamp(new Point(icon.Left, bar.Top - icon.Height), screen, icon, bars, ref best, ref bestDistance);
                TryClamp(new Point(icon.Left, bar.Bottom), screen, icon, bars, ref best, ref bestDistance);
                TryClamp(new Point(bar.Left - icon.Width, icon.Top), screen, icon, bars, ref best, ref bestDistance);
                TryClamp(new Point(bar.Right, icon.Top), screen, icon, bars, ref best, ref bestDistance);
            }

            if (bestDistance != int.MaxValue) Location = best;
        }

        private static void TryClamp(Point candidate, Rectangle screen, Rectangle icon,
            Rectangle[] bars, ref Point best, ref int bestDistance)
        {
            int x = candidate.X;
            int y = candidate.Y;
            if (x < screen.Left) x = screen.Left;
            if (y < screen.Top) y = screen.Top;
            if (x + icon.Width > screen.Right) x = screen.Right - icon.Width;
            if (y + icon.Height > screen.Bottom) y = screen.Bottom - icon.Height;

            Rectangle test = new Rectangle(x, y, icon.Width, icon.Height);
            if (IntersectsAny(test, bars)) return;

            int dx = x - icon.Left;
            int dy = y - icon.Top;
            int distance = (dx < 0 ? -dx : dx) + (dy < 0 ? -dy : dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = new Point(x, y);
            }
        }

        private static bool IntersectsAny(Rectangle rect, Rectangle[] bars)
        {
            for (int i = 0; i < bars.Length; i++)
            {
                Rectangle overlap = Rectangle.Intersect(rect, bars[i]);
                if (overlap.Width > 0 && overlap.Height > 0) return true;
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _topTimer.Stop();
                _dragPoll.Stop();
                _recoverTimer.Stop();
                RemoveTopHooks();
                DisposeDock();
                if (_topTimer != null) _topTimer.Dispose();
                if (_dragPoll != null) _dragPoll.Dispose();
                if (_recoverTimer != null) _recoverTimer.Dispose();
                if (_surface != null)
                {
                    _surface.Dispose();
                    _surface = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
