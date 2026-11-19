using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Snoty
{
    /// <summary>
    /// Win32 interop. The floating icon uses a layered window so it gets true
    /// per-pixel transparency and a soft shadow on top of the taskbar.
    /// </summary>
    internal static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private const int ULW_ALPHA = 0x02;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
            uint usage, out IntPtr bits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr hdc, int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey,
            ref BLENDFUNCTION pblend, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        private const uint GW_HWNDPREV = 3;
        private const uint GA_ROOT = 2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST_STYLE = 0x00000008;

        public static IntPtr ForegroundHandle()
        {
            try { return GetForegroundWindow(); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>True when the window belongs to the given process.</summary>
        public static bool BelongsToProcess(IntPtr hwnd, uint processId)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                uint owner;
                GetWindowThreadProcessId(hwnd, out owner);
                return owner == processId;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsTopMost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST_STYLE) != 0; }
            catch { return false; }
        }

        /// <summary>
        /// Places <paramref name="hwnd"/> immediately above the top-level window
        /// that covers it. Re-asserting HWND_TOPMOST does nothing for a window
        /// that is already topmost, so a relative insert is what actually
        /// restores the z-order over the taskbar.
        /// </summary>
        public static bool RaiseAbove(IntPtr hwnd, IntPtr covering)
        {
            if (hwnd == IntPtr.Zero || covering == IntPtr.Zero) return false;
            try
            {
                IntPtr root = GetAncestor(covering, GA_ROOT);
                if (root == IntPtr.Zero) root = covering;
                if (root == hwnd) return false;

                IntPtr above = GetWindow(root, GW_HWNDPREV);
                return SetWindowPos(hwnd, above, 0, 0, 0, 0,
                                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True when the foreground window belongs to this process. Used so the
        /// notes popup does not close when focus passes to our own floating icon.
        /// </summary>
        public static bool ForegroundBelongsTo(uint processId)
        {
            return BelongsToProcess(ForegroundHandle(), processId);
        }

        // ------------------------------------------------------------------
        // Z-order helpers: keep the icon above the taskbar whenever the shell
        // does not hold it in front (see FloatingIcon.EnsureOnTop).
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct W32POINT
        {
            public int x;
            public int y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(W32POINT point);

        public static IntPtr WindowAt(int x, int y)
        {
            try
            {
                W32POINT p;
                p.x = x;
                p.y = y;
                return WindowFromPoint(p);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        public delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventDelegate callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);

        /// <summary>Rounded window corners on Windows 11. Silently ignored elsewhere.</summary>
        public static void TryRoundCorners(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            }
            catch
            {
            }
        }

        /// <summary>Pushes a premultiplied-alpha bitmap onto a layered window.</summary>
        public static bool Blit(IntPtr hwnd, Bitmap surface)
        {
            if (hwnd == IntPtr.Zero || surface == null) return false;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                BITMAPINFOHEADER bmi = new BITMAPINFOHEADER();
                bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.biWidth = surface.Width;
                bmi.biHeight = -surface.Height; // top-down
                bmi.biPlanes = 1;
                bmi.biBitCount = 32;
                bmi.biCompression = 0; // BI_RGB

                IntPtr bits;
                hBitmap = CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return false;

                BitmapData data = surface.LockBits(
                    new Rectangle(0, 0, surface.Width, surface.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    uint bytes = (uint)(Math.Abs(data.Stride) * surface.Height);
                    CopyMemory(bits, data.Scan0, bytes);
                }
                finally
                {
                    surface.UnlockBits(data);
                }

                oldBitmap = SelectObject(memDc, hBitmap);

                SIZE size;
                size.cx = surface.Width;
                size.cy = surface.Height;
                POINT src;
                src.x = 0;
                src.y = 0;

                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;

                // pptDst = NULL keeps the current window position; WinForms owns that.
                return UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0,
                                           ref blend, ULW_ALPHA);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public static void KeepOnTop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // ------------------------------------------------------------------
        // Taskbar geometry. The icon must not come to rest inside a taskbar:
        // while the shell owns the foreground it refuses our z-order raises,
        // so an icon covering the taskbar disappears until focus moves away.
        // ------------------------------------------------------------------

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out W32RECT rect);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string className, string windowName);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct W32RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static IntPtr RootOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr root = GetAncestor(hwnd, GA_ROOT);
                return root == IntPtr.Zero ? hwnd : root;
            }
            catch
            {
                return hwnd;
            }
        }

        public static bool IsVisible(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return IsWindowVisible(hwnd); }
            catch { return false; }
        }

        /// <summary>Window rectangle in screen pixels. False for zero-sized windows.</summary>
        public static bool WindowBounds(IntPtr hwnd, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                W32RECT r;
                if (!GetWindowRect(hwnd, out r)) return false;
                rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                return rect.Width > 0 && rect.Height > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads one pixel straight off the composited desktop. Used only to
        /// tell whether our icon is actually visible or hidden behind an
        /// opaque shell surface - hit-testing alone also reports transparent
        /// dismissal overlays.
        /// </summary>
        public static bool ScreenPixel(int x, int y, out int r, out int g, out int b)
        {
            r = g = b = 0;
            IntPtr dc = IntPtr.Zero;
            try
            {
                dc = GetDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) return false;

                uint p = GetPixel(dc, x, y);
                if (p == 0xFFFFFFFF) return false;

                r = (int)(p & 0xFF);
                g = (int)((p >> 8) & 0xFF);
                b = (int)((p >> 16) & 0xFF);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
            }
        }

        /// <summary>Bounds of the primary and secondary taskbars, in screen pixels.</summary>
        public static Rectangle[] TaskbarRects()
        {
            List<Rectangle> bars = new List<Rectangle>();

            // FindWindow keeps working in shell states where EnumWindows can be
            // filtered, so always seed the primary taskbar from it.
            AddWindowRect(bars, FindWindow("Shell_TrayWnd", null));
            AddWindowRect(bars, FindWindow("Shell_SecondaryTrayWnd", null));

            try
            {
                EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
                {
                    if (!IsWindowVisible(hwnd)) return true;

                    StringBuilder name = new StringBuilder(64);
                    GetClassName(hwnd, name, name.Capacity);
                    string cls = name.ToString();
                    if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                        AddWindowRect(bars, hwnd);
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }

            // Work-area fallback: a taskbar (or another appbar) is the strip
            // between a screen's bounds and its working area.
            try
            {
                Screen[] screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    Rectangle b = screens[i].Bounds;
                    Rectangle w = screens[i].WorkingArea;
                    if (w.Top > b.Top)
                        AddRect(bars, Rectangle.FromLTRB(b.Left, b.Top, b.Right, w.Top));
                    if (w.Bottom < b.Bottom)
                        AddRect(bars, Rectangle.FromLTRB(b.Left, w.Bottom, b.Right, b.Bottom));
                    if (w.Left > b.Left)
                        AddRect(bars, Rectangle.FromLTRB(b.Left, b.Top, w.Left, b.Bottom));
                    if (w.Right < b.Right)
                        AddRect(bars, Rectangle.FromLTRB(w.Right, b.Top, b.Right, b.Bottom));
                }
            }
            catch
            {
            }

            return bars.ToArray();
        }

        private static void AddWindowRect(List<Rectangle> bars, IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                W32RECT r;
                if (!GetWindowRect(hwnd, out r)) return;
                AddRect(bars, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            }
            catch
            {
            }
        }

        private static void AddRect(List<Rectangle> bars, Rectangle rect)
        {
            // Ignore degenerate strips (an auto-hidden taskbar is a few pixels)
            // and avoid duplicates from the three detection paths.
            if (rect.Width < 4 || rect.Height < 4) return;
            for (int i = 0; i < bars.Count; i++)
                if (bars[i] == rect) return;
            bars.Add(rect);
        }

        // ------------------------------------------------------------------
        // Docked icon support: the icon is a real child of Shell_TrayWnd, so
        // it shares the taskbar's window band while Start and Search are open.
        // ------------------------------------------------------------------

        public delegate bool ChildWindowProc(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        public static IntPtr TaskbarWindow()
        {
            try { return FindWindow("Shell_TrayWnd", null); }
            catch { return IntPtr.Zero; }
        }

        public static bool IsWindowAlive(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return IsWindow(hwnd); }
            catch { return false; }
        }

        public static string ClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            try
            {
                StringBuilder name = new StringBuilder(128);
                GetClassName(hwnd, name, name.Capacity);
                return name.ToString();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Visits the taskbar's descendants, including third-party docked windows.</summary>
        public static void ForEachChild(IntPtr parent, ChildWindowProc callback)
        {
            if (parent == IntPtr.Zero || callback == null) return;
            try
            {
                EnumChildWindows(parent, delegate(IntPtr hwnd, IntPtr unused)
                {
                    return callback(hwnd);
                }, IntPtr.Zero);
            }
            catch
            {
            }
        }

        public static int RegisterWindowMessageId(string name)
        {
            try { return unchecked((int)RegisterWindowMessage(name)); }
            catch { return 0; }
        }

        public static bool IsLeftButtonDown()
        {
            try { return (GetAsyncKeyState(0x01) & 0x8000) != 0; }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // Taskbar button cleanup
        //
        // WinForms tool windows normally stay out of the taskbar, but the shell
        // can still leave (or later revive) a taskbar button for a window that
        // was briefly shown. ITaskbarList removes it explicitly.
        // ------------------------------------------------------------------

        [ComImport]
        [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class TaskbarList
        {
        }

        /// <summary>Adds WS_EX_TOOLWINDOW, keeping a window out of the taskbar and Alt+Tab.</summary>
        public static void MakeToolWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((style & WS_EX_TOOLWINDOW) == 0)
                    SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
            }
            catch
            {
            }
        }

        /// <summary>Removes any taskbar button the shell may have made for a window.</summary>
        public static void RemoveTaskbarTab(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                ITaskbarList list = (ITaskbarList)new TaskbarList();
                list.HrInit();
                list.DeleteTab(hwnd);
                Marshal.FinalReleaseComObject(list);
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // Low-level mouse hook
        //
        // The notes panel is never the foreground window when it is opened
        // from the taskbar, so Form.Deactivate does not fire for a taskbar
        // click. This hook is installed only while the panel is visible and
        // closes it on a click anywhere outside it.
        // ------------------------------------------------------------------

        public delegate IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public W32POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public const int WH_MOUSE_LL = 14;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, MouseHookProc callback,
            IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after,
            string className, string windowName);

        public static IntPtr HookMouse(MouseHookProc callback)
        {
            try
            {
                return SetWindowsHookEx(WH_MOUSE_LL, callback,
                    GetModuleHandle(null), 0);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        public static void UnhookMouse(IntPtr hook)
        {
            if (hook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(hook); } catch { }
        }

        public static IntPtr ContinueMouseHook(IntPtr hook, int nCode,
            IntPtr wParam, IntPtr lParam)
        {
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        public static Point MouseHookPoint(IntPtr lParam)
        {
            try
            {
                MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                    lParam, typeof(MSLLHOOKSTRUCT));
                return new Point(info.pt.x, info.pt.y);
            }
            catch
            {
                return Point.Empty;
            }
        }

        /// <summary>
        /// True inside the shell's notification area, where a click belongs to
        /// the Snoty tray icon (which toggles the panel by itself).
        /// </summary>
        public static bool InNotificationArea(int x, int y)
        {
            try
            {
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return false;

                IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                Rectangle rect;
                if (notify != IntPtr.Zero && WindowBounds(notify, out rect)
                    && rect.Contains(x, y))
                    return true;

                IntPtr overflow = FindWindow("NotifyIconOverflowWindow", null);
                if (overflow != IntPtr.Zero && IsWindowVisible(overflow)
                    && WindowBounds(overflow, out rect) && rect.Contains(x, y))
                    return true;
            }
            catch
            {
            }
            return false;
        }
    }
}
