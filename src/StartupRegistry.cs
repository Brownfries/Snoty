using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Snoty
{
    /// <summary>Optional "start with Windows" toggle (HKCU Run key, no admin needed).</summary>
    internal static class StartupRegistry
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Snoty";
        private const string LegacyValueName = "StickyNotes";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    object v = key.GetValue(ValueName);
                    if (v != null) return true;
                    return key.GetValue(LegacyValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                        // The pre-rename entry would launch the old executable.
                        if (key.GetValue(LegacyValueName) != null)
                            key.DeleteValue(LegacyValueName, false);
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                        if (key.GetValue(LegacyValueName) != null)
                            key.DeleteValue(LegacyValueName, false);
                    }
                }
            }
            catch
            {
                // Non-fatal: the menu simply stays unchecked.
            }
        }
    }
}
