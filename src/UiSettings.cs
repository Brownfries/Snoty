using System;
using System.Drawing;
using System.IO;

namespace Snoty
{
    /// <summary>Small, human readable settings file for icon placement and mode.</summary>
    internal static class UiSettings
    {
        private static string _path;
        private static bool _firstRun;
        private static bool _dock = true;
        private static string _theme = "auto";

        private static string PathFile
        {
            get
            {
                if (_path == null)
                {
                    AppPaths.Ensure();
                    _path = Path.Combine(AppPaths.DataDir, "ui.ini");
                }
                return _path;
            }
        }

        public static bool FirstRun
        {
            get { return _firstRun; }
        }

        /// <summary>True when the icon should live inside the taskbar.</summary>
        public static bool Dock
        {
            get { return _dock; }
        }

        /// <summary>"auto", "light" or "dark". Auto (the default) follows Windows.</summary>
        public static string ThemeMode
        {
            get { return _theme; }
        }

        public static void Load()
        {
            _firstRun = !File.Exists(PathFile);
            _dock = true;
            _theme = "auto";

            try
            {
                if (!File.Exists(PathFile)) return;

                string[] lines = File.ReadAllLines(PathFile);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (string.Equals(key, "dock", StringComparison.OrdinalIgnoreCase))
                    {
                        int parsed;
                        if (int.TryParse(value, out parsed)) _dock = parsed != 0;
                    }
                    else if (string.Equals(key, "theme", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(value, "light", StringComparison.OrdinalIgnoreCase))
                            _theme = "light";
                        else if (string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase))
                            _theme = "dark";
                        else
                            _theme = "auto";
                    }
                }
            }
            catch
            {
            }
        }

        public static bool TryLoadIconPos(out Point point)
        {
            point = Point.Empty;
            try
            {
                if (!File.Exists(PathFile)) return false;

                string[] lines = File.ReadAllLines(PathFile);
                int x = int.MinValue;
                int y = int.MinValue;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    int parsed;
                    if (!int.TryParse(value, out parsed)) continue;

                    if (string.Equals(key, "iconX", StringComparison.OrdinalIgnoreCase)) x = parsed;
                    else if (string.Equals(key, "iconY", StringComparison.OrdinalIgnoreCase)) y = parsed;
                }

                if (x == int.MinValue || y == int.MinValue) return false;
                point = new Point(x, y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void SaveMode(bool dock, Point floatingPoint)
        {
            _dock = dock;
            Write(floatingPoint.X, floatingPoint.Y, dock);
        }

        /// <summary>Remembers an explicit light/dark choice, or "auto".</summary>
        public static void SaveTheme(string mode)
        {
            _theme = string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase) ? "light"
                   : string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase) ? "dark"
                   : "auto";

            int x = 0;
            int y = 0;
            try
            {
                Point p;
                if (TryLoadIconPos(out p)) { x = p.X; y = p.Y; }
            }
            catch
            {
            }

            Write(x, y, _dock);
        }

        private static void Write(int x, int y, bool dock)
        {
            try
            {
                string dir = Path.GetDirectoryName(PathFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(PathFile,
                    "iconX=" + x + "\r\n" +
                    "iconY=" + y + "\r\n" +
                    "dock=" + (dock ? "1" : "0") + "\r\n" +
                    "theme=" + _theme + "\r\n");
                _firstRun = false;
            }
            catch
            {
            }
        }
    }
}
