using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Snoty
{
    internal sealed class WhisperResultEventArgs : EventArgs
    {
        public readonly string Text;
        public readonly string Error;
        public readonly bool Cancelled;

        public WhisperResultEventArgs(string text, string error, bool cancelled)
        {
            Text = text == null ? "" : text;
            Error = error == null ? "" : error;
            Cancelled = cancelled;
        }
    }

    /// <summary>
    /// Runs the bundled whisper.cpp command line once per utterance. The
    /// process only exists while a recording is being transcribed, so idle
    /// cost is zero and the ~67 MB runtime is the only price for far better
    /// accuracy than the built-in recognizer.
    /// </summary>
    internal sealed class WhisperInput : IDisposable
    {
        private const int TimeoutMs = 60000;

        private readonly object _gate = new object();
        private Process _process;
        private volatile bool _running;
        private volatile bool _cancelled;
        private volatile bool _disposed;

        private static bool _probed;
        private static bool _available;
        private static string _probeReason = "";

        public event EventHandler<WhisperResultEventArgs> Finished;

        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>Checks for the bundled CLI and model next to the app.</summary>
        public static bool IsAvailable(out string reason)
        {
            if (!_probed)
            {
                _probed = true;
                try
                {
                    string exe = ExePath();
                    string model = ModelPath();
                    if (!File.Exists(exe))
                    {
                        _available = false;
                        _probeReason = "Speech engine files are missing (whisper-cli.exe).";
                    }
                    else if (model == null)
                    {
                        _available = false;
                        _probeReason = "Speech engine files are missing (whisper model).";
                    }
                    else if (IntPtr.Size != 8)
                    {
                        _available = false;
                        _probeReason = "The speech engine needs 64-bit Windows.";
                    }
                    else
                    {
                        _available = true;
                        _probeReason = "";
                    }
                }
                catch (Exception ex)
                {
                    _available = false;
                    _probeReason = ex.Message;
                }
            }
            reason = _probeReason;
            return _available;
        }

        /// <summary>
        /// Starts transcribing a 16 kHz mono WAV on a background thread.
        /// Returns false only when the process could not be launched.
        /// </summary>
        public bool Start(string wavPath, out string error)
        {
            error = "";
            if (_disposed)
            {
                error = "Dictation is shutting down.";
                return false;
            }
            if (_running)
            {
                error = "Already transcribing.";
                return false;
            }

            string exe, model;
            string reason;
            if (!IsAvailable(out reason))
            {
                error = reason;
                return false;
            }
            exe = ExePath();
            model = ModelPath();

            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = exe;
                info.Arguments = "-m \"" + model + "\" -f \"" + wavPath
                    + "\" -nt -np -l en -t " + Threads();
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
                info.WorkingDirectory = Path.GetDirectoryName(exe);

                Process p = new Process();
                p.StartInfo = info;
                p.EnableRaisingEvents = false;

                lock (_gate)
                {
                    _process = p;
                    _cancelled = false;
                    _running = true;
                }

                Thread thread = new Thread(delegate() { Run(wavPath); });
                thread.IsBackground = true;
                thread.Name = "Snoty.Whisper";
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
                return true;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _process = null;
                    _running = false;
                }
                error = "Speech engine could not start: " + ex.Message;
                return false;
            }
        }

        /// <summary>Kills a run in progress. The Finished event still fires.</summary>
        public void Abort()
        {
            Process p;
            lock (_gate) p = _process;
            _cancelled = true;
            if (p == null) return;

            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch
            {
            }
        }

        private void Run(string wavPath)
        {
            Process p;
            lock (_gate) p = _process;

            StringBuilder output = new StringBuilder();
            StringBuilder errors = new StringBuilder();
            object sync = new object();
            string text = "";
            string error = "";
            bool cancelled = false;

            try
            {
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sync) output.AppendLine(e.Data);
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sync)
                    {
                        if (errors.Length < 8000) errors.AppendLine(e.Data);
                    }
                };

                p.Start();

                // Abort may have been requested before the process existed.
                if (_cancelled)
                {
                    try { p.Kill(); } catch { }
                }

                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch { }

                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                bool exited = p.WaitForExit(TimeoutMs);
                bool timedOut = false;

                if (!exited)
                {
                    timedOut = !_cancelled;
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(3000); } catch { }
                }
                else
                {
                    // Drain the asynchronous stdout/stderr handlers.
                    try { p.WaitForExit(); } catch { }
                }

                cancelled = _cancelled;

                string stdout;
                string stderr;
                lock (sync)
                {
                    stdout = output.ToString();
                    stderr = errors.ToString();
                }

                if (!cancelled)
                {
                    if (timedOut)
                    {
                        error = "Transcription timed out. Try a shorter note.";
                    }
                    else
                    {
                        text = Parse(stdout);
                        if (text.Length == 0 && p.ExitCode != 0)
                            error = "Speech engine failed: " + FirstLine(stderr);
                    }
                }
            }
            catch (Exception ex)
            {
                error = "Speech engine failed: " + ex.Message;
            }
            finally
            {
                try { p.Dispose(); } catch { }

                lock (_gate)
                {
                    _process = null;
                    _running = false;
                }

                try
                {
                    EventHandler<WhisperResultEventArgs> h = Finished;
                    if (h != null) h(this, new WhisperResultEventArgs(text, error, cancelled));
                }
                catch
                {
                }
            }
        }

        /// <summary>Turns whisper-cli stdout into plain dictation text.</summary>
        private static string Parse(string stdout)
        {
            if (string.IsNullOrEmpty(stdout)) return "";

            string[] lines = stdout.Replace("\r", "").Split('\n');
            List<string> parts = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                // [BLANK_AUDIO], [MUSIC], [SOUND] and other non-speech markers.
                if (line[0] == '[') continue;

                // (eerie music), (wind blowing) - whisper describes silence
                // with parentheticals instead of emitting nothing.
                if (line[0] == '(' && line[line.Length - 1] == ')') continue;

                // Backend chatter that can slip past -np on some builds.
                if (line.StartsWith("whisper_", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("ggml_", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("load_backend", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                    continue;

                parts.Add(line);
            }

            string text = string.Join(" ", parts.ToArray()).Trim();

            // Strip repeated whitespace and a leading dash some builds print.
            while (text.StartsWith("- ")) text = text.Substring(2).Trim();
            while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
                text = text.Replace("  ", " ");

            return text;
        }

        /// <summary>
        /// Whisper invents short phrases on silence, tones and noise. Filter
        /// the common ones only for short clips, where a one-word result is
        /// almost always a hallucination rather than a real note.
        /// </summary>
        public static bool IsLikelyNoise(string text, int milliseconds)
        {
            if (text == null) return true;
            string t = text.Trim().Trim('.', ',', '!', '?', ';', ':').Trim().ToLowerInvariant();
            if (t.Length == 0) return true;

            if (t.StartsWith("(") && t.EndsWith(")")) return true;
            if (t == "blank audio" || t == "silence") return true;

            if (milliseconds > 2500) return false;
            if (t.IndexOf(' ') < 0)
            {
                switch (t)
                {
                    case "you":
                    case "thanks":
                    case "bye":
                    case "goodbye":
                    case "okay":
                    case "ok":
                    case "hmm":
                    case "uh":
                    case "um":
                    case "yeah":
                    case "hello":
                    case "hi":
                    case "so":
                    case "oh":
                    case "the":
                    case "a":
                    case "i":
                        return true;
                }
                return false;
            }

            return t == "thank you" || t == "thanks for watching"
                || t == "please subscribe" || t == "subscribe to my channel";
        }

        private static string ExePath()
        {
            return Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whisper"),
                "whisper-cli.exe");
        }

        private static string ModelPath()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whisper");
            string q5 = Path.Combine(dir, "ggml-base.en-q5_1.bin");
            if (File.Exists(q5)) return q5;
            string plain = Path.Combine(dir, "ggml-base.en.bin");
            if (File.Exists(plain)) return plain;
            return null;
        }

        private static int Threads()
        {
            int n = Environment.ProcessorCount - 2;
            if (n < 2) n = 2;
            if (n > 6) n = 6;
            return n;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "unknown error";
            string[] lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length > 0) return line;
            }
            return "unknown error";
        }

        public void Dispose()
        {
            _disposed = true;
            Abort();

            Process p;
            lock (_gate) p = _process;
            if (p == null) return;

            try
            {
                if (!p.HasExited) p.WaitForExit(1500);
            }
            catch
            {
            }
        }
    }
}
