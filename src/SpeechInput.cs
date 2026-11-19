using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Speech.Recognition;
using System.Text.RegularExpressions;

namespace Snoty
{
    internal sealed class SpeechTextEventArgs : EventArgs
    {
        public readonly string Text;
        public readonly float Confidence;

        public SpeechTextEventArgs(string text, float confidence)
        {
            Text = text;
            Confidence = confidence;
        }
    }

    /// <summary>
    /// Thin wrapper around the built-in Windows (SAPI) desktop recognizer.
    /// Fully offline, no cloud calls, no extra packages. The engine is only
    /// alive while dictation is running, so idle cost is zero.
    /// </summary>
    internal sealed class SpeechInput : IDisposable
    {
        private const float MinConfidence = 0.15f;

        private SpeechRecognitionEngine _engine;
        private bool _listening;

        private static bool _probed;
        private static bool _available;
        private static string _probeReason = "";

        public event EventHandler<SpeechTextEventArgs> FinalText;
        public event EventHandler<SpeechTextEventArgs> PartialText;
        public event EventHandler Started;
        public event EventHandler Stopped;
        public event EventHandler<SpeechTextEventArgs> Failed;

        public bool IsListening
        {
            get { return _listening; }
        }

        public static bool IsAvailable(out string reason)
        {
            if (!_probed)
            {
                _probed = true;
                try
                {
                    ReadOnlyCollection<RecognizerInfo> list = SpeechRecognitionEngine.InstalledRecognizers();
                    if (list == null || list.Count == 0)
                    {
                        _available = false;
                        _probeReason = "No Windows speech recognizer is installed.";
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

        public void Start()
        {
            if (_listening) return;

            ReadOnlyCollection<RecognizerInfo> list;
            try
            {
                list = SpeechRecognitionEngine.InstalledRecognizers();
            }
            catch (Exception ex)
            {
                RaiseFailed("Speech engine unavailable: " + ex.Message);
                return;
            }

            if (list == null || list.Count == 0)
            {
                RaiseFailed("No speech recognizer found. Turn on Speech Recognition in Windows Settings.");
                return;
            }

            try
            {
                RecognizerInfo pick = list[0];
                for (int i = 0; i < list.Count; i++)
                {
                    string name = list[i].Culture == null ? "" : list[i].Culture.Name;
                    if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase)) { pick = list[i]; break; }
                }

                SpeechRecognitionEngine engine = new SpeechRecognitionEngine(pick);
                engine.LoadGrammar(new DictationGrammar());
                engine.MaxAlternates = 1;

                // Be patient with the speaker. The old 650 ms silence timeout cut
                // sentences into fragments and the dictation grammar then guessed
                // at each fragment - that produced the nonsense transcriptions.
                engine.InitialSilenceTimeout = TimeSpan.FromSeconds(15);
                engine.BabbleTimeout = TimeSpan.FromSeconds(15);
                engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(1400);
                engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(2000);

                // Let the recogniser get used to this speaker's voice over time.
                TrySet(engine, "AdaptationOn", 1);

                engine.SetInputToDefaultAudioDevice();
                engine.SpeechRecognized += OnRecognized;
                engine.SpeechHypothesized += OnHypothesized;
                engine.RecognizeCompleted += OnCompleted;

                _engine = engine;
                _listening = true;

                engine.RecognizeAsync(RecognizeMode.Multiple);
                Raise(Started);
            }
            catch (Exception ex)
            {
                TearDownEngine();
                _listening = false;
                RaiseFailed(Friendly(ex));
            }
        }

        public void Stop()
        {
            bool wasListening = _listening;
            TearDownEngine();
            _listening = false;
            if (wasListening) Raise(Stopped);
        }

        public void Dispose()
        {
            Stop();
        }

        private void TearDownEngine()
        {
            SpeechRecognitionEngine engine = _engine;
            _engine = null;
            if (engine == null) return;

            try { engine.SpeechRecognized -= OnRecognized; } catch { }
            try { engine.SpeechHypothesized -= OnHypothesized; } catch { }
            try { engine.RecognizeCompleted -= OnCompleted; } catch { }
            try { engine.RecognizeAsyncCancel(); } catch { }
            try { engine.Dispose(); } catch { }
        }

        private static string Friendly(Exception ex)
        {
            InvalidOperationException ioe = ex as InvalidOperationException;
            if (ioe != null)
            {
                string m = ioe.Message == null ? "" : ioe.Message.ToLowerInvariant();
                if (m.Contains("audio") || m.Contains("device"))
                    return "No microphone found. Plug one in and try again.";
                return "Speech engine error: " + ioe.Message;
            }
            return "Speech engine error: " + ex.Message;
        }

        private static void TrySet(SpeechRecognitionEngine engine, string name, int value)
        {
            try { engine.UpdateRecognizerSetting(name, value); }
            catch { }
        }

        private void OnRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            if (e.Result == null) return;
            string raw = e.Result.Text;
            if (string.IsNullOrEmpty(raw)) return;
            float confidence = e.Result.Confidence;

            // Drop near-garbage. The desktop engine scores even good short words
            // low (about 0.30), so this stays deliberately permissive.
            if (confidence < MinConfidence) return;

            string clean = DictationText.Clean(raw);
            if (clean.Length == 0) return;

            EventHandler<SpeechTextEventArgs> h = FinalText;
            if (h != null) h(this, new SpeechTextEventArgs(clean, confidence));
        }

        private void OnHypothesized(object sender, SpeechHypothesizedEventArgs e)
        {
            if (e.Result == null) return;
            string raw = e.Result.Text;
            if (string.IsNullOrEmpty(raw)) return;

            EventHandler<SpeechTextEventArgs> h = PartialText;
            if (h != null) h(this, new SpeechTextEventArgs(raw.Trim(), e.Result.Confidence));
        }

        private void OnCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            // "No speech" completions are normal and should stay silent.
            if (e.Error != null)
            {
                _listening = false;
                RaiseFailed("Dictation stopped: " + e.Error.Message);
            }
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseFailed(string message)
        {
            EventHandler<SpeechTextEventArgs> h = Failed;
            if (h != null) h(this, new SpeechTextEventArgs(message, 0f));
        }
    }

    /// <summary>
    /// Converts raw dictation into tidy note text: spoken punctuation becomes
    /// real symbols and spacing is normalised.
    /// </summary>
    internal static class DictationText
    {
        private static readonly string[,] Spoken =
        {
            { "new paragraph", "\n\n" },
            { "new line", "\n" },
            { "exclamation mark", "!" },
            { "exclamation point", "!" },
            { "question mark", "?" },
            { "semicolon", ";" },
            { "semi colon", ";" },
            { "full stop", "." },
            { "open parenthesis", "(" },
            { "open paren", "(" },
            { "close parenthesis", ")" },
            { "close paren", ")" },
            { "quotation mark", "\"" },
            { "hyphen", "-" },
            { "comma", "," },
            { "colon", ":" },
            { "period", "." }
        };

        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();

            for (int i = 0; i < Spoken.GetLength(0); i++)
            {
                string word = Spoken[i, 0];
                string symbol = Spoken[i, 1];
                string replacement = symbol.Replace("$", "$$");
                try
                {
                    s = Regex.Replace(s, @"\b" + Regex.Escape(word) + @"\b", replacement,
                                      RegexOptions.IgnoreCase);
                }
                catch { }
            }

            // Tidy up the spacing around the punctuation we just inserted.
            s = Regex.Replace(s, @"[ \t]*\n[ \t]*", "\n");
            s = Regex.Replace(s, @" +([,.;:!?])", "$1");
            s = Regex.Replace(s, @"([,.;:!?])(?=[A-Za-z])", "$1 ");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>
        /// Appends freshly dictated text to a note. Handles spacing and only
        /// starts a capital letter when a sentence really ended, so pausing
        /// mid-sentence no longer creates stray capitals.
        /// </summary>
        public static string Join(string existing, string addition)
        {
            if (string.IsNullOrEmpty(addition)) return existing == null ? "" : existing;
            if (string.IsNullOrEmpty(existing)) return Capitalize(addition);

            string text = existing;
            bool newSentence = SentenceEnded(text);

            if (!EndsOpen(text) && !StartsClosed(addition)) text += " ";

            text += newSentence ? Capitalize(addition) : addition;
            return text;
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0], CultureInfo.CurrentCulture) + s.Substring(1);
        }

        private static bool EndsOpen(string s)
        {
            if (s.Length == 0) return true;
            char c = s[s.Length - 1];
            return c == ' ' || c == '\n' || c == '\t';
        }

        private static bool StartsClosed(string s)
        {
            if (s.Length == 0) return true;
            char c = s[0];
            return c == ',' || c == '.' || c == ';' || c == ':' || c == '!'
                || c == '?' || c == ')' || c == '\n';
        }

        private static bool SentenceEnded(string s)
        {
            for (int i = s.Length - 1; i >= 0; i--)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r') continue;
                return c == '.' || c == '!' || c == '?' || c == ':' || c == '\n';
            }
            return true;
        }
    }
}
