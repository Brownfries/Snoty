using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Snoty
{
    /// <summary>Owns the tray icon, the floating icon, the popup and the menu.</summary>
    internal sealed class App : ApplicationContext
    {
        private readonly NotesStore _store;
        private readonly SpeechInput _speech;
        private readonly FloatingIcon _icon;
        private readonly NotesPopup _popup;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;

        private ToolStripMenuItem _dictateItem;
        private ToolStripMenuItem _startupItem;
        private ToolStripMenuItem _themeItem;
        private Icon _trayImage;
        private EventWaitHandle _signal;
        private System.Windows.Forms.Timer _themeTimer;
        private bool _exiting;

        public App()
        {
            // A saved light/dark choice overrides the Windows theme; "auto"
            // (the default) keeps following it.
            Theme.SetMode(UiSettings.ThemeMode);

            _store = new NotesStore();
            _store.Load();

            _speech = new SpeechInput();

            _icon = new FloatingIcon(_store, _speech);
            _icon.IsPopupOpen = delegate { return _popup != null && _popup.IsOpen; };

            _popup = new NotesPopup(_store, _speech);

            _icon.IsDictating = delegate { return _popup != null && _popup.IsDictating; };
            _popup.DictationChanged += delegate { _icon.RefreshMark(); };

            _icon.ShowPopupRequested += delegate { TogglePopup(); };
            _icon.HidePopupRequested += delegate { _popup.HidePopup(); };
            _icon.Moved += delegate
            {
                if (_popup != null && _popup.IsOpen)
                    _popup.FollowAnchor(_icon.AnchorBounds());
            };

            _menu = BuildMenu();
            ApplyMenuTheme();
            _menu.Opening += delegate { ApplyMenuTheme(); };
            _icon.ContextMenuStrip = _menu;

            // WinForms does not repaint custom-drawn chrome by itself. Poll the
            // setting, because Windows does not reliably broadcast theme
            // changes to every process.
            _themeTimer = new System.Windows.Forms.Timer();
            _themeTimer.Interval = 1500;
            _themeTimer.Tick += delegate
            {
                Theme.FollowSystem();
                ApplyMenuTheme();
                if (_themeItem != null) _themeItem.Checked = Theme.Auto;
            };
            _themeTimer.Start();

            _trayImage = MakeTrayIcon();
            _tray = new NotifyIcon();
            _tray.Icon = _trayImage;
            _tray.Text = AppPaths.Name;
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) TogglePopup();
            };

            StartSignalListener();

            // Dock without showing the floating window first: a form that is
            // shown and then hidden can leave a taskbar button behind.
            if (!(UiSettings.Dock && _icon.DockToTaskbar()))
                _icon.Show();

            _icon.RefreshMark();

            if (UiSettings.FirstRun)
            {
                try
                {
                    _tray.ShowBalloonTip(5000, AppPaths.Name,
                        "Click the note icon or this tray icon to open your notes. "
                        + "Drag the icon anywhere - even onto the taskbar.",
                        ToolTipIcon.Info);
                }
                catch
                {
                }
            }
        }

        // ------------------------------------------------------------------

        private ContextMenuStrip BuildMenu()
        {
            // SnotyMenu keeps the drop-down out of the taskbar. A plain
            // ContextMenuStrip created a stray, generic-icon taskbar button.
            ContextMenuStrip menu = new SnotyMenu();
            menu.Font = Theme.FontSmall;
            menu.ShowImageMargin = false;
            // Checked items need their own margin, otherwise the check is
            // drawn over the label (or not at all).
            menu.ShowCheckMargin = true;

            ToolStripMenuItem open = new ToolStripMenuItem("Open notes");
            open.Font = new Font(menu.Font, FontStyle.Bold);
            open.Click += delegate { ShowPopup(); };
            menu.Items.Add(open);

            ToolStripMenuItem add = new ToolStripMenuItem("New note");
            add.Click += delegate { NewNote(); };
            menu.Items.Add(add);

            menu.Items.Add(new ToolStripSeparator());

            _dictateItem = new ToolStripMenuItem("Dictate");
            _dictateItem.Click += delegate { ToggleDictation(); };
            menu.Items.Add(_dictateItem);

            _startupItem = new ToolStripMenuItem("Start with Windows");
            _startupItem.Click += delegate
            {
                bool next = !_startupItem.Checked;
                StartupRegistry.SetEnabled(next);
                _startupItem.Checked = StartupRegistry.IsEnabled();
            };
            menu.Items.Add(_startupItem);

            _themeItem = new ToolStripMenuItem("Follow Windows theme");
            _themeItem.Click += delegate
            {
                if (Theme.Auto)
                {
                    // Stop following Windows, but keep the current look: the
                    // item can be unticked and the sun/moon button can then
                    // switch explicitly.
                    bool dark = Theme.IsDark;
                    Theme.UseManual(dark);
                    UiSettings.SaveTheme(dark ? "dark" : "light");
                    _themeItem.Checked = false;
                }
                else
                {
                    Theme.UseAuto();
                    UiSettings.SaveTheme("auto");
                    _themeItem.Checked = true;
                }
                ApplyMenuTheme();
            };
            menu.Items.Add(_themeItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem reset = new ToolStripMenuItem("Reset icon position");
            reset.Click += delegate { _icon.ResetPosition(); };
            menu.Items.Add(reset);

            ToolStripMenuItem about = new ToolStripMenuItem("About " + AppPaths.Name);
            about.Click += delegate { ShowAbout(); };
            menu.Items.Add(about);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(exit);

            menu.Opening += delegate
            {
                _dictateItem.Checked = _popup.IsDictating;
                _startupItem.Checked = StartupRegistry.IsEnabled();
                _themeItem.Checked = Theme.Auto;
            };

            return menu;
        }

        private void ApplyMenuTheme()
        {
            if (_menu == null) return;

            _menu.BackColor = Theme.MenuBack;
            _menu.ForeColor = Theme.TextMain;
            _menu.RenderMode = ToolStripRenderMode.Professional;
            if (!(_menu.Renderer is MenuThemeRenderer))
                _menu.Renderer = new MenuThemeRenderer();

            _menu.Invalidate();
        }

        private static Icon MakeTrayIcon()
        {
            int size = SystemInformation.SmallIconSize.Width;
            if (size < 16) size = 16;
            return IconArt.CreateIcon(size);
        }

        private void StartSignalListener()
        {
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset,
                @"Local\Snoty.Show.v1");

            Thread thread = new Thread(delegate()
            {
                while (true)
                {
                    _signal.WaitOne();
                    if (_exiting) return;

                    try
                    {
                        _icon.BeginInvoke((MethodInvoker)delegate
                        {
                            if (!_exiting) TogglePopup();
                        });
                    }
                    catch
                    {
                        return;
                    }
                }
            });

            thread.IsBackground = true;
            thread.Name = "Snoty.Signal";
            thread.Start();
        }

        // ------------------------------------------------------------------

        private void TogglePopup()
        {
            if (_exiting) return;
            if (_popup.IsOpen) _popup.HidePopup();
            else ShowPopup();
        }

        private void ShowPopup()
        {
            if (_exiting || _popup.IsOpen) return;
            _popup.ShowAt(_icon.AnchorBounds());
        }

        private void NewNote()
        {
            if (_exiting) return;
            if (!_popup.IsOpen) _popup.ShowAt(_icon.AnchorBounds());
            _popup.NewNote();
        }

        private void ToggleDictation()
        {
            if (_exiting) return;
            if (!_popup.IsOpen) _popup.ShowAt(_icon.AnchorBounds());
            _popup.StartOrStopDictation();
        }

        private void ShowAbout()
        {
            if (_exiting) return;
            using (AboutForm about = new AboutForm())
            {
                about.ShowDialog();
            }
        }

        public void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;

            try { if (_themeTimer != null) _themeTimer.Stop(); } catch { }
            try { _popup.HidePopup(); } catch { }
            try { _popup.Shutdown(); } catch { }
            try { _popup.Dispose(); } catch { }
            try { _speech.Dispose(); } catch { }
            try { _store.SaveIfDirty(); } catch { }

            try
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            catch { }

            try { if (_signal != null) _signal.Set(); } catch { }
            try { if (_themeTimer != null) _themeTimer.Dispose(); } catch { }
            try { if (_trayImage != null) _trayImage.Dispose(); } catch { }
            try { _icon.Close(); } catch { }

            ExitThread();
        }
    }
}
