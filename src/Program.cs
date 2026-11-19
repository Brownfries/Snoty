using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Snoty
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, @"Local\Snoty.SingleInstance.v1", out createdNew))
            {
                if (!createdNew)
                {
                    // Already running: ask the live instance to open its notes.
                    SignalRunningInstance();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                UiSettings.Load();
                Theme.Init();

                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    LogError(e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    LogError(e.ExceptionObject as Exception);
                };

                App app = new App();
                Application.Run(app);

                GC.KeepAlive(mutex);
            }
        }

        private static void SignalRunningInstance()
        {
            try
            {
                EventWaitHandle handle;
                if (EventWaitHandle.TryOpenExisting(@"Local\Snoty.Show.v1", out handle))
                {
                    handle.Set();
                    handle.Close();
                }
            }
            catch
            {
            }
        }

        private static void LogError(Exception ex)
        {
            try
            {
                if (ex == null) return;

                AppPaths.Ensure();
                string dir = AppPaths.DataDir;
                Directory.CreateDirectory(dir);

                File.AppendAllText(
                    Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("u") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
