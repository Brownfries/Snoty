using System;
using System.IO;

namespace Snoty
{
    /// <summary>App identity, version and the one place that knows where data lives.</summary>
    internal static class AppPaths
    {
        public const string Name = "Snoty";
        public const string Version = "1.0.0";
        public const string TagLine =
            "Tiny sticky note app that can be pinned anywhere with offline Whisper speech-to-text.";

        private static bool _ready;

        public static string DataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
            }
        }

        public static string LegacyDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "StickyNotes");
            }
        }

        /// <summary>Creates the data folder and copies notes from the pre-rename app once.</summary>
        public static void Ensure()
        {
            if (_ready) return;
            _ready = true;

            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
            }
            catch
            {
                return;
            }

            // The previous name kept its data in %APPDATA%\StickyNotes. Copy it
            // once rather than moving it, so an older build still finds it.
            try
            {
                if (!Directory.Exists(LegacyDir)) return;

                string[] files = { "notes.dat", "notes.dat.bak", "ui.ini" };
                for (int i = 0; i < files.Length; i++)
                {
                    string from = Path.Combine(LegacyDir, files[i]);
                    string to = Path.Combine(DataDir, files[i]);
                    if (System.IO.File.Exists(from) && !System.IO.File.Exists(to))
                        File.Copy(from, to, false);
                }
            }
            catch
            {
            }
        }
    }
}
