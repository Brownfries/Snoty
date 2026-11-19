using System;
using System.Collections.Generic;
using System.Drawing;

namespace Snoty
{
    internal sealed class TaskbarInfo
    {
        public IntPtr Handle;
        public Rectangle Bounds;
    }

    /// <summary>
    /// Finds a genuinely empty stretch of the taskbar for the docked icon.
    /// Every visible child of Shell_TrayWnd counts as occupied, so Pulsar (and
    /// any other docked widget) pushes the icon into the next free gap instead
    /// of being covered by it.
    /// </summary>
    internal static class TaskbarDock
    {
        public static TaskbarInfo Primary()
        {
            IntPtr hwnd = Native.TaskbarWindow();
            if (hwnd == IntPtr.Zero || !Native.IsWindowAlive(hwnd)) return null;

            Rectangle bar;
            if (!Native.WindowBounds(hwnd, out bar)) return null;

            // An auto-hidden taskbar is a 2 px strip; a vertical taskbar needs
            // a different layout than this horizontal one.
            if (bar.Height < Theme.S(24) || bar.Width < Theme.S(80)) return null;
            if (bar.Height >= bar.Width) return null;

            TaskbarInfo info = new TaskbarInfo();
            info.Handle = hwnd;
            info.Bounds = bar;
            return info;
        }

        public static List<Rectangle> Occupied(TaskbarInfo taskbar, IntPtr exclude)
        {
            List<Rectangle> rects = new List<Rectangle>();
            if (taskbar == null) return rects;

            Rectangle bar = taskbar.Bounds;
            Native.ForEachChild(taskbar.Handle, delegate(IntPtr hwnd)
            {
                if (hwnd == exclude) return true;
                if (!Native.IsVisible(hwnd)) return true;

                Rectangle r;
                if (!Native.WindowBounds(hwnd, out r)) return true;

                Rectangle clipped = Rectangle.Intersect(r, bar);
                if (clipped.Width < Theme.S(10) || clipped.Height < Theme.S(10)) return true;

                // The XAML bridge spans the whole bar; so do a couple of other
                // composition hosts. They say nothing about individual widgets.
                if (clipped.Width >= bar.Width * 82 / 100) return true;

                string cls = Native.ClassName(hwnd);
                if (cls == "Windows.UI.Composition.DesktopWindowContentBridge"
                    || cls == "Windows.UI.Core.CoreWindow"
                    || cls == "Microsoft.UI.Content.DesktopChildSiteBridge")
                    return true;

                rects.Add(clipped);
                return true;
            });

            return rects;
        }

        /// <summary>Where the docked icon should sit, or null when no gap is wide enough.</summary>
        public static Rectangle? Place(TaskbarInfo taskbar, Size icon, int? preferredX, IntPtr exclude)
        {
            if (taskbar == null) return null;
            List<Rectangle> occupied = Occupied(taskbar, exclude);
            return PlaceIn(taskbar.Bounds, occupied, icon, preferredX, Theme.S(6));
        }

        /// <summary>
        /// Pure placement: takes the bar and the rectangles already spoken for,
        /// returns a screen rectangle for the icon. Without a preferred X it
        /// sits at the tray end of the rightmost gap that fits.
        /// </summary>
        public static Rectangle? PlaceIn(Rectangle bar, IList<Rectangle> occupied,
            Size icon, int? preferredX, int margin)
        {
            if (icon.Width <= 0 || icon.Height <= 0) return null;
            if (bar.Width < icon.Width || bar.Height < icon.Height) return null;

            List<Rectangle> blocked = new List<Rectangle>();
            for (int i = 0; i < occupied.Count; i++)
            {
                Rectangle r = Rectangle.Intersect(occupied[i], bar);
                if (r.Width > 0 && r.Height > 0) blocked.Add(r);
            }
            blocked.Sort(delegate(Rectangle a, Rectangle b) { return a.Left.CompareTo(b.Left); });

            // Merge touching/overlapping rectangles before carving out gaps.
            List<Rectangle> merged = new List<Rectangle>();
            for (int i = 0; i < blocked.Count; i++)
            {
                Rectangle r = blocked[i];
                if (merged.Count == 0)
                {
                    merged.Add(r);
                    continue;
                }

                Rectangle last = merged[merged.Count - 1];
                if (r.Left <= last.Right)
                {
                    int mergedRight = Math.Max(last.Right, r.Right);
                    merged[merged.Count - 1] = Rectangle.FromLTRB(last.Left, last.Top, mergedRight, last.Bottom);
                }
                else
                {
                    merged.Add(r);
                }
            }

            int needed = icon.Width + margin * 2;
            List<int[]> gaps = new List<int[]>();
            int cursor = bar.Left;
            for (int i = 0; i < merged.Count; i++)
            {
                if (merged[i].Left - cursor >= needed)
                    gaps.Add(new int[] { cursor, merged[i].Left });
                if (merged[i].Right > cursor) cursor = merged[i].Right;
            }
            if (bar.Right - cursor >= needed)
                gaps.Add(new int[] { cursor, bar.Right });

            if (gaps.Count == 0) return null;

            int choose = gaps.Count - 1;
            if (preferredX.HasValue)
            {
                int bestDistance = int.MaxValue;
                int target = preferredX.Value;
                for (int i = 0; i < gaps.Count; i++)
                {
                    int lo = gaps[i][0] + margin;
                    int hi = gaps[i][1] - margin - icon.Width;
                    int x = target - icon.Width / 2;
                    if (x < lo) x = lo;
                    if (x > hi) x = hi;
                    int center = x + icon.Width / 2;
                    int distance = Math.Abs(center - target);
                    if (distance < bestDistance) { bestDistance = distance; choose = i; }
                }
            }

            int left = gaps[choose][0] + margin;
            int right = gaps[choose][1] - margin - icon.Width;
            int placeX;
            if (preferredX.HasValue)
            {
                placeX = preferredX.Value - icon.Width / 2;
                if (placeX < left) placeX = left;
                if (placeX > right) placeX = right;
            }
            else
            {
                placeX = right;
            }

            int top = bar.Top + (bar.Height - icon.Height) / 2;
            return new Rectangle(placeX, top, icon.Width, icon.Height);
        }

        public static bool IsClear(Rectangle icon, Rectangle bar, IList<Rectangle> occupied)
        {
            if (icon.Left < bar.Left || icon.Right > bar.Right) return false;
            if (icon.Top < bar.Top || icon.Bottom > bar.Bottom) return false;

            for (int i = 0; i < occupied.Count; i++)
            {
                Rectangle overlap = Rectangle.Intersect(icon, occupied[i]);
                if (overlap.Width > 0 && overlap.Height > 0) return false;
            }
            return true;
        }

        public static bool IsOverTaskbar(Point screen)
        {
            TaskbarInfo tb = Primary();
            return tb != null && tb.Bounds.Contains(screen);
        }
    }
}
