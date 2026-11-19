using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Snoty
{
    /// <summary>
    /// Captures 16 kHz / 16-bit / mono PCM from the default microphone using
    /// winmm. Buffers are only alive while the user is dictating; everything is
    /// released as soon as the WAV has been handed to Whisper.
    /// </summary>
    internal sealed class MicRecorder : IDisposable
    {
        private const int SampleRate = 16000;
        private const int BlockBytes = 3200;   // 100 ms
        private const int BlockCount = 12;     // 1.2 s of driver slack
        private const int SilenceMs = 1200;
        private const int InitialWaitMs = 8000;
        private const int MaxRecordMs = 30000;

        private const uint WAVE_MAPPER = 0xFFFFFFFF;
        private const uint CALLBACK_NULL = 0;
        private const uint WHDR_DONE = 0x00000001;

        private readonly Timer _timer;
        private readonly List<Buffer> _buffers = new List<Buffer>();

        private IntPtr _waveIn = IntPtr.Zero;
        private MemoryStream _pcm;
        private byte[] _finalPcm;

        private int _startedAt;
        private int _lastVoiceAt;
        private int _blocks;
        private int _speechBlocks;
        private double _noiseMin = double.MaxValue;
        private bool _recording;
        private string _error = "";

        private sealed class Buffer
        {
            public IntPtr Header;
            public IntPtr Data;
            public bool Collected;
        }

        public event EventHandler AutoStop;

        public MicRecorder()
        {
            _timer = new Timer();
            _timer.Interval = 50;
            _timer.Tick += OnTick;
        }

        public bool IsRecording
        {
            get { return _recording; }
        }

        /// <summary>Approximate amount of speech found by the silence detector.</summary>
        public int SpeechMilliseconds
        {
            get { return _speechBlocks * 100; }
        }

        public string LastError
        {
            get { return _error; }
        }

        public bool Start()
        {
            if (_recording) return true;
            _error = "";
            _finalPcm = null;
            _speechBlocks = 0;
            _blocks = 0;
            _noiseMin = double.MaxValue;

            try
            {
                if (waveInGetNumDevs() == 0)
                {
                    _error = "No microphone found. Plug one in and try again.";
                    return false;
                }

                WAVEFORMATEX format = new WAVEFORMATEX();
                format.wFormatTag = 1; // PCM
                format.nChannels = 1;
                format.nSamplesPerSec = SampleRate;
                format.wBitsPerSample = 16;
                format.nBlockAlign = (ushort)(format.nChannels * format.wBitsPerSample / 8);
                format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;

                int rc = waveInOpen(out _waveIn, new UIntPtr(WAVE_MAPPER), ref format,
                    IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                if (rc != 0 || _waveIn == IntPtr.Zero)
                {
                    _waveIn = IntPtr.Zero;
                    _error = "Microphone unavailable (" + WaveError(rc) + ").";
                    return false;
                }

                _pcm = new MemoryStream();

                int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                for (int i = 0; i < BlockCount; i++)
                {
                    Buffer b = new Buffer();
                    b.Header = Marshal.AllocHGlobal(headerSize);
                    b.Data = Marshal.AllocHGlobal(BlockBytes);

                    byte[] zero = new byte[headerSize];
                    Marshal.Copy(zero, 0, b.Header, headerSize);

                    WAVEHDR h = new WAVEHDR();
                    h.lpData = b.Data;
                    h.dwBufferLength = BlockBytes;
                    Marshal.StructureToPtr(h, b.Header, false);

                    rc = waveInPrepareHeader(_waveIn, b.Header, headerSize);
                    if (rc != 0) throw new InvalidOperationException("prepare " + WaveError(rc));

                    rc = waveInAddBuffer(_waveIn, b.Header, headerSize);
                    if (rc != 0) throw new InvalidOperationException("queue " + WaveError(rc));

                    _buffers.Add(b);
                }

                rc = waveInStart(_waveIn);
                if (rc != 0) throw new InvalidOperationException("start " + WaveError(rc));

                _startedAt = Environment.TickCount;
                _lastVoiceAt = _startedAt;
                _recording = true;
                _timer.Start();
                return true;
            }
            catch (Exception ex)
            {
                _error = "Microphone unavailable: " + ex.Message;
                StopInternal();
                return false;
            }
        }

        /// <summary>
        /// A copy of the PCM captured so far. Used for the while-speaking
        /// preview; safe to call between ticks on the UI thread.
        /// </summary>
        public byte[] Snapshot()
        {
            if (_pcm == null) return new byte[0];
            try { return _pcm.ToArray(); }
            catch { return new byte[0]; }
        }

        /// <summary>Stops capturing and returns the raw PCM. Safe to call twice.</summary>
        public byte[] Stop()
        {
            if (_recording) _finalPcm = StopInternal();
            return _finalPcm == null ? new byte[0] : _finalPcm;
        }

        public void Cancel()
        {
            StopInternal();
            _finalPcm = null;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_recording) return;

            int now = Environment.TickCount;
            int elapsed = now - _startedAt;
            if (elapsed < 0) elapsed = 0;

            bool anyDone = false;
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));

            for (int i = 0; i < _buffers.Count; i++)
            {
                Buffer b = _buffers[i];
                if (b.Collected) continue;

                WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(b.Header, typeof(WAVEHDR));
                if ((h.dwFlags & WHDR_DONE) == 0) continue;

                b.Collected = true;
                anyDone = true;

                int bytes = (int)Math.Min(h.dwBytesRecorded, (uint)BlockBytes);
                if (bytes > 0)
                {
                    byte[] chunk = new byte[bytes];
                    Marshal.Copy(h.lpData, chunk, 0, bytes);
                    _pcm.Write(chunk, 0, bytes);
                    Analyze(chunk, bytes, now);
                }
            }

            bool stop = false;
            if (_speechBlocks > 0 && now - _lastVoiceAt >= SilenceMs) stop = true;
            else if (_speechBlocks == 0 && elapsed >= InitialWaitMs) stop = true;
            else if (elapsed >= MaxRecordMs) stop = true;

            if (stop)
            {
                _finalPcm = StopInternal();

                EventHandler h = AutoStop;
                if (h != null) h(this, EventArgs.Empty);
                return;
            }

            if (!anyDone) return;

            // Put every buffer the driver returned back into the queue.
            for (int i = 0; i < _buffers.Count; i++)
            {
                Buffer b = _buffers[i];
                if (!b.Collected) continue;

                // A completed header must be unprepared and prepared again
                // before it can be queued; some drivers reject a plain re-add
                // with MMSYSERR_INVALPARAM.
                try { waveInUnprepareHeader(_waveIn, b.Header, headerSize); }
                catch { }

                WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(b.Header, typeof(WAVEHDR));
                h.dwFlags = 0;
                h.dwBytesRecorded = 0;
                Marshal.StructureToPtr(h, b.Header, false);

                int rc = waveInPrepareHeader(_waveIn, b.Header, headerSize);
                if (rc == 0) rc = waveInAddBuffer(_waveIn, b.Header, headerSize);
                b.Collected = rc != 0;
            }
        }

        private void Analyze(byte[] chunk, int bytes, int now)
        {
            int samples = bytes / 2;
            if (samples == 0) return;

            double sum = 0;
            for (int i = 0; i < samples; i++)
            {
                short s = BitConverter.ToInt16(chunk, i * 2);
                sum += (double)s * s;
            }

            double rms = Math.Sqrt(sum / samples);

            _blocks++;
            if (_blocks <= 4 && rms < _noiseMin) _noiseMin = rms;

            double threshold = 220.0;
            if (_blocks > 4 && _noiseMin < 3000.0)
                threshold = Math.Max(220.0, Math.Min(700.0, _noiseMin * 2.5));

            if (rms > threshold)
            {
                _speechBlocks++;
                _lastVoiceAt = now;
            }
        }

        private byte[] StopInternal()
        {
            _timer.Stop();
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));

            if (_waveIn != IntPtr.Zero)
            {
                try { waveInStop(_waveIn); }
                catch { }
                try { waveInReset(_waveIn); }
                catch { }

                // Reset returns whatever the driver had already recorded.
                if (_pcm != null)
                {
                    for (int i = 0; i < _buffers.Count; i++)
                    {
                        Buffer b = _buffers[i];
                        if (b.Collected) continue;
                        try
                        {
                            WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(b.Header, typeof(WAVEHDR));
                            int bytes = (int)Math.Min(h.dwBytesRecorded, (uint)BlockBytes);
                            if (bytes > 0)
                            {
                                byte[] chunk = new byte[bytes];
                                Marshal.Copy(h.lpData, chunk, 0, bytes);
                                _pcm.Write(chunk, 0, bytes);
                            }
                        }
                        catch { }
                    }
                }

                for (int i = 0; i < _buffers.Count; i++)
                {
                    Buffer b = _buffers[i];
                    try { waveInUnprepareHeader(_waveIn, b.Header, headerSize); }
                    catch { }
                    if (b.Data != IntPtr.Zero) Marshal.FreeHGlobal(b.Data);
                    if (b.Header != IntPtr.Zero) Marshal.FreeHGlobal(b.Header);
                }
                _buffers.Clear();

                try { waveInClose(_waveIn); }
                catch { }
                _waveIn = IntPtr.Zero;
            }

            _recording = false;

            byte[] data = new byte[0];
            if (_pcm != null)
            {
                data = _pcm.ToArray();
                _pcm.Dispose();
                _pcm = null;
            }
            return data;
        }

        /// <summary>Writes mono 16-bit 16 kHz PCM to a standard WAV file.</summary>
        public static void SaveWav(string path, byte[] pcm)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + pcm.Length);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));

                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(SampleRate);
                w.Write(SampleRate * 2);
                w.Write((short)2);
                w.Write((short)16);

                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(pcm.Length);
                w.Write(pcm);
            }
        }

        private static string WaveError(int code)
        {
            try
            {
                StringBuilder text = new StringBuilder(256);
                if (waveInGetErrorText(code, text, text.Capacity) == 0 && text.Length > 0)
                    return text.ToString();
            }
            catch { }
            return "error " + code;
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            StopInternal();
        }

        // ------------------------------------------------------------------
        // winmm
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr hwi, UIntPtr deviceId,
            ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hwi, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hwi);

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int waveInGetErrorText(int error, StringBuilder text, int size);
    }
}
