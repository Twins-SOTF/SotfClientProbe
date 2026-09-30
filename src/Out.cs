// Out.cs -- file output for the client probe.
//
// Layout (all under <gameroot>\BepInEx\probe_out\):
//   identity_probe.txt   who am I
//   vitals_probe.txt     how health was found (the make-or-break one)
//   network_probe.txt    which server am I on
//   guard_report.txt     environment / anti-tamper state
//   client_<ts>.jsonl    the event stream
//
// Sampling is ~2Hz, so a plain StreamWriter with periodic flush is ample --
// no queue, no background thread. Fewer moving parts means fewer ways for
// this mod to destabilise the game.

using System;
using System.IO;
using System.Text;

namespace SotfClientProbe
{
    internal static class Out
    {
        private static StreamWriter _jsonl;
        private static string _dir = "";
        private static int _lines;

        public static string Dir { get { return _dir; } }
        public static string JsonlPath;

        public static bool Init()
        {
            try
            {
                string root = Guard.GameRoot;
                if (string.IsNullOrEmpty(root)) root = ".";
                _dir = Path.Combine(root, "BepInEx", "probe_out");
                Directory.CreateDirectory(_dir);

                JsonlPath = Path.Combine(_dir, "client_" + U.FileStamp() + ".jsonl");
                _jsonl = new StreamWriter(new FileStream(
                    JsonlPath, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
                _jsonl.AutoFlush = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void WriteReport(string name, string text)
        {
            try
            {
                if (string.IsNullOrEmpty(_dir)) return;
                File.WriteAllText(Path.Combine(_dir, name), text ?? "", Encoding.UTF8);
            }
            catch { }
        }

        /// Appends one line to a plain-text log. Used for the per-session
        /// summary: one line per server the player joined, newest at the
        /// bottom, small enough to open and read on the spot.
        public static void Append(string name, string text)
        {
            try
            {
                if (string.IsNullOrEmpty(_dir)) return;
                File.AppendAllText(Path.Combine(_dir, name),
                    (text ?? "") + "\n", Encoding.UTF8);
            }
            catch { }
        }

        /// Appends one JSON object per line. Caller passes an already-built
        /// object body (without braces).
        public static void Event(string body)
        {
            try
            {
                if (_jsonl == null) return;
                _jsonl.Write("{");
                _jsonl.Write(body);
                _jsonl.Write("}\n");
                _lines++;
                if ((_lines & 15) == 0) _jsonl.Flush();
            }
            catch { }
        }

        public static void Flush()
        {
            try { if (_jsonl != null) _jsonl.Flush(); }
            catch { }
        }

        public static void Close()
        {
            try
            {
                Flush();
                if (_jsonl != null) { _jsonl.Dispose(); _jsonl = null; }
            }
            catch { }
        }
    }
}
