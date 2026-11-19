using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Snoty
{
    /// <summary>
    /// A tiny native child window of Shell_TrayWnd. Living inside the taskbar
    /// is what keeps it visible while Start, Search and the tray flyout are
    /// open - they raise the taskbar's window band, which a top-level window
    /// cannot follow.
    /// </summary>
    internal sealed class DockIconWindow : IDisposable
    {
        private const string WindowClass = "SnotyDockIcon";

        private const uint WS_CHILD = 0x40000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_CLIPSIBLINGS = 0x04000000;

        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOPARENTNOTIFY = 0x00000004;
        private const uint WS_EX_NOACTIVATE = 0x08000000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private const uint WM_PAINT = 0x000F;
        private const uint WM_SETCURSOR = 0x0020;
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_NCHITTEST = 0x0084;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_MOVE = 0x0003;
        private const uint WM_WINDOWPOSCHANGED = 0x0047;
        private const uint WM_CAPTURECHANGED = 0x0215;
        private const uint WM_DESTROY = 0x0002;

        private const int HTCLIENT = 1;
        private const int MA_NOACTIVATE = 3;

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
            IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y,
            int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr cursor);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCapture(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);

        private static WndProcDelegate _proc;
        private static DockIconWindow _current;
        private static bool _registered;
        private static IntPtr _handCursor;
        private static IntPtr _instance;

        private IntPtr _hwnd;
        private int _width;
        private int _height;
        private bool _listening;
        private int _count = -1;

        private bool _down;
        private bool _dragged;
        private Point _downPoint;

        public event Action Clicked;
        public event Action MenuRequested;
        public event Action DragStarted;
        public event Action<Point> DragMoved;
        public event Action<Point> DragEnded;

        /// <summary>Raised when the taskbar moves the icon, including live drags along the bar.</summary>
        public event Action Moved;

        public IntPtr Handle
        {
            get { return _hwnd; }
        }

        public bool IsValid
        {
            get { return _hwnd != IntPtr.Zero && Native.IsWindowAlive(_hwnd); }
        }

        public bool IsDragging
        {
            get { return _down && _dragged; }
        }

        /// <summary>Screen point of the last left-button press, used to keep the icon under the cursor.</summary>
        public Point PressedScreenPoint
        {
            get { return _downPoint; }
        }

        public bool Create(IntPtr taskbar, Rectangle bar, Rectangle screenRect)
        {
            if (taskbar == IntPtr.Zero) return false;
            if (!EnsureClass()) return false;

            _current = this;
            _width = screenRect.Width;
            _height = screenRect.Height;

            int x = screenRect.Left - bar.Left;
            int y = screenRect.Top - bar.Top;

            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOPARENTNOTIFY,
                WindowClass, "Snoty",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                x, y, _width, _height,
                taskbar, IntPtr.Zero, _instance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _current = null;
                return false;
            }

            _count = -1;
            Render();
            BringToFront();
            return true;
        }

        /// <summary>
        /// The taskbar hosts a full-width XAML bridge as one of its children.
        /// A newly created child is inserted at the bottom of the z-order and
        /// would be painted behind that bridge, so lift the icon to the top.
        /// </summary>
        public void BringToFront()
        {
            if (!IsValid) return;
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public void SetState(bool listening, int noteCount)
        {
            if (_listening == listening && _count == noteCount) return;
            _listening = listening;
            _count = noteCount;
            Render();
        }

        public void Place(Rectangle bar, Rectangle screenRect)
        {
            if (!IsValid) return;
            _width = screenRect.Width;
            _height = screenRect.Height;
            // Keep the z-order stable here. Re-raising the window mid-drag can
            // make Windows drop our mouse capture, which ends the drag while
            // the cursor is still on the bar.
            SetWindowPos(_hwnd, IntPtr.Zero,
                screenRect.Left - bar.Left,
                screenRect.Top - bar.Top,
                screenRect.Width, screenRect.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public bool TryGetScreenBounds(out Rectangle rect)
        {
            return Native.WindowBounds(_hwnd, out rect);
        }

        private void Render()
        {
            if (!IsValid) return;
            using (Bitmap bmp = IconArt.Render(_width, _height, _listening, _count))
            {
                if (bmp != null) Native.Blit(_hwnd, bmp);
            }
        }

        private static bool EnsureClass()
        {
            if (_registered) return true;

            _instance = GetModuleHandle(null);
            _handCursor = LoadCursor(IntPtr.Zero, new IntPtr(32649)); // IDC_HAND
            _proc = WndProc;

            WNDCLASS wc = new WNDCLASS();
            wc.style = 0;
            wc.lpfnWndProc = _proc;
            wc.hInstance = _instance;
            wc.hCursor = _handCursor;
            wc.lpszClassName = WindowClass;

            ushort atom = RegisterClass(ref wc);
            _registered = atom != 0;
            return _registered;
        }

        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            DockIconWindow self = _current;
            if (self != null && self._hwnd == hwnd)
            {
                try
                {
                    return self.HandleMessage(msg, wParam, lParam);
                }
                catch
                {
                    return IntPtr.Zero;
                }
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private IntPtr HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    return new IntPtr(MA_NOACTIVATE);

                case WM_SETCURSOR:
                    if (_handCursor != IntPtr.Zero) SetCursor(_handCursor);
                    return new IntPtr(1);

                case WM_NCHITTEST:
                    return new IntPtr(HTCLIENT);

                case WM_ERASEBKGND:
                    return new IntPtr(1);

                case WM_PAINT:
                    PAINTSTRUCT ps;
                    BeginPaint(_hwnd, out ps);
                    EndPaint(_hwnd, ref ps);
                    return IntPtr.Zero;

                case WM_MOVE:
                case WM_WINDOWPOSCHANGED:
                    Action positionChanged = Moved;
                    if (positionChanged != null) positionChanged();
                    break;

                case WM_LBUTTONDOWN:
                    _down = true;
                    _dragged = false;
                    _downPoint = CursorPosition();
                    SetCapture(_hwnd);
                    return IntPtr.Zero;

                case WM_MOUSEMOVE:
                    if (_down)
                    {
                        Point p = CursorPosition();
                        if (!_dragged)
                        {
                            int dx = p.X - _downPoint.X;
                            int dy = p.Y - _downPoint.Y;
                            if (dx * dx + dy * dy > Theme.S(4) * Theme.S(4))
                            {
                                _dragged = true;
                                Action started = DragStarted;
                                if (started != null) started();
                            }
                        }

                        if (_dragged)
                        {
                            Action<Point> moved = DragMoved;
                            if (moved != null) moved(p);
                        }
                    }
                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    if (_down)
                    {
                        Point p = CursorPosition();
                        bool dragged = _dragged;
                        _down = false;
                        _dragged = false;
                        ReleaseCapture();

                        if (dragged)
                        {
                            Action<Point> ended = DragEnded;
                            if (ended != null) ended(p);
                        }
                        else
                        {
                            Action clicked = Clicked;
                            if (clicked != null) clicked();
                        }
                    }
                    return IntPtr.Zero;

                case WM_RBUTTONUP:
                    Action menu = MenuRequested;
                    if (menu != null) menu();
                    return IntPtr.Zero;

                case WM_CAPTURECHANGED:
                    if (_down)
                    {
                        bool dragged = _dragged;
                        _down = false;
                        _dragged = false;
                        if (dragged)
                        {
                            Action<Point> ended = DragEnded;
                            if (ended != null) ended(CursorPosition());
                        }
                    }
                    return IntPtr.Zero;

                case WM_DESTROY:
                    _hwnd = IntPtr.Zero;
                    return IntPtr.Zero;
            }

            return DefWindowProc(_hwnd, msg, wParam, lParam);
        }

        private static Point CursorPosition()
        {
            POINT p;
            if (GetCursorPos(out p)) return new Point(p.x, p.y);
            return Point.Empty;
        }

        public void Dispose()
        {
            IntPtr hwnd = _hwnd;
            _hwnd = IntPtr.Zero;
            _down = false;
            _dragged = false;
            if (_current == this) _current = null;

            if (hwnd != IntPtr.Zero && Native.IsWindowAlive(hwnd))
            {
                try { DestroyWindow(hwnd); }
                catch { }
            }
        }
    }
}
