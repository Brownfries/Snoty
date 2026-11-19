using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Snoty
{
    internal sealed class Note
    {
        public Guid Id { get; set; }
        public string Text { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int ColorIndex { get; set; }

        public Note()
        {
            Id = Guid.NewGuid();
            Text = "";
            CreatedUtc = DateTime.UtcNow;
            UpdatedUtc = CreatedUtc;
            ColorIndex = 0;
        }
    }

    /// <summary>
    /// Plain file storage in %APPDATA%\Snoty\notes.dat.
    /// Format is line based; the note body is Base64 so any character is safe.
    /// </summary>
    internal sealed class NotesStore
    {
        private const string Header = "STICKYNOTES1";
        private const int MaxNotes = 500;

        private readonly List<Note> _notes = new List<Note>();
        private bool _dirty;

        public event EventHandler Changed;

        public string DataDir { get; private set; }
        public string DataFile { get; private set; }

        public NotesStore()
        {
            AppPaths.Ensure();
            DataDir = AppPaths.DataDir;
            DataFile = Path.Combine(DataDir, "notes.dat");
        }

        /// <summary>Notes in display order: newest first.</summary>
        public IList<Note> Notes
        {
            get { return _notes; }
        }

        public int Count
        {
            get { return _notes.Count; }
        }

        public Note Add(string text)
        {
            Note n = new Note();
            n.Text = text == null ? "" : text;
            n.ColorIndex = _notes.Count == 0 ? 0 : Theme.NextColor(_notes[0].ColorIndex);
            _notes.Insert(0, n);
            _dirty = true;
            RaiseChanged();
            return n;
        }

        public void Remove(Note n)
        {
            if (n == null) return;
            if (_notes.Remove(n))
            {
                _dirty = true;
                RaiseChanged();
            }
        }

        /// <summary>Marks the note as edited. Cheap: raises no event.</summary>
        public void Touch(Note n)
        {
            if (n == null) return;
            n.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
        }

        /// <summary>Returns the note with the most recent edit, or null.</summary>
        public Note MostRecent()
        {
            Note best = null;
            for (int i = 0; i < _notes.Count; i++)
            {
                if (best == null || _notes[i].UpdatedUtc > best.UpdatedUtc) best = _notes[i];
            }
            return best;
        }

        private void RaiseChanged()
        {
            EventHandler h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------

        public void Load()
        {
            _notes.Clear();
            try
            {
                if (!File.Exists(DataFile)) { _dirty = false; return; }

                string[] lines = File.ReadAllLines(DataFile, Encoding.UTF8);
                if (lines.Length == 0 || lines[0].Trim() != Header)
                {
                    QuarantineCorrupt();
                    _dirty = false;
                    return;
                }

                for (int i = 1; i < lines.Length && _notes.Count < MaxNotes; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0) continue;

                    string[] parts = line.Split('|');
                    if (parts.Length < 5) continue;

                    Note n = new Note();
                    Guid id;
                    if (Guid.TryParse(parts[0], out id)) n.Id = id;

                    long created, updated;
                    if (long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out created))
                        n.CreatedUtc = SafeFromTicks(created);
                    if (long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out updated))
                        n.UpdatedUtc = SafeFromTicks(updated);

                    int color;
                    if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out color))
                        n.ColorIndex = color;

                    try
                    {
                        n.Text = Encoding.UTF8.GetString(Convert.FromBase64String(parts[4]));
                    }
                    catch
                    {
                        n.Text = "";
                    }

                    _notes.Add(n);
                }
            }
            catch
            {
                QuarantineCorrupt();
            }
            _dirty = false;
        }

        private static DateTime SafeFromTicks(long ticks)
        {
            try
            {
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return DateTime.UtcNow;
                return new DateTime(ticks, DateTimeKind.Utc);
            }
            catch
            {
                return DateTime.UtcNow;
            }
        }

        private void QuarantineCorrupt()
        {
            try
            {
                if (File.Exists(DataFile))
                {
                    string bad = DataFile + ".bad";
                    if (File.Exists(bad)) File.Delete(bad);
                    File.Move(DataFile, bad);
                }
            }
            catch { }
        }

        public void SaveIfDirty()
        {
            if (_dirty) Save();
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);

                StringBuilder sb = new StringBuilder();
                sb.Append(Header).Append('\n');
                for (int i = 0; i < _notes.Count; i++)
                {
                    Note n = _notes[i];
                    sb.Append(n.Id.ToString("D")).Append('|')
                      .Append(n.CreatedUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append(n.UpdatedUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append(n.ColorIndex.ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(n.Text == null ? "" : n.Text)))
                      .Append('\n');
                }

                string tmp = DataFile + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));

                if (File.Exists(DataFile))
                {
                    string bak = DataFile + ".bak";
                    // Replace is atomic-ish; keep a backup copy for safety.
                    try
                    {
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Copy(DataFile, bak, true);
                    }
                    catch { }
                    try
                    {
                        File.Replace(tmp, DataFile, null);
                    }
                    catch
                    {
                        File.Copy(tmp, DataFile, true);
                        File.Delete(tmp);
                    }
                }
                else
                {
                    File.Move(tmp, DataFile);
                }

                _dirty = false;
            }
            catch
            {
                // Storage failures must never take the UI down.
            }
        }
    }
}
