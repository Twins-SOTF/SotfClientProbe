// IntegrityProbe.cs -- directory-level Merkle hash of the BepInEx tree.
//
// A file list is self-reported (a tampered client omits what it does not want
// us to see); hashing the tree binds every file into one value, so adding an
// extra plugin changes the root hash even if the client says nothing.
// BepInEx writes files while the game runs (logs, interop cache), so some
// paths are excluded by rule -- the centre MUST use the same RULES_VERSION or
// the two sides will never agree. This is a local computation only; no file
// content ever leaves the machine.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SotfClientProbe
{
    internal static class IntegrityProbe
    {
        public const int RULES_VERSION = 1;

        private static readonly string[] Targets = { "plugins", "patcher", "config", "core" };

        private static readonly string[] ExcludeDirs =
            { "cache", "logs", "log", "obj", "temp", "tmp", "unpacked", "shaders" };

        private static readonly string[] ExcludeExt =
            { ".log", ".tmp", ".old", ".bak", ".md5", ".pid", ".swp", ".lock" };

        private static readonly string[] ExcludeNames =
            { "LogOutput.log", "player.log", "output_log.txt", "thumbs.db", "desktop.ini" };

        private const int MaxFiles = 4000;
        private const long MaxBytesPerFile = 256L * 1024 * 1024;

        public static string Report = "";
        public static string RootHash = "";
        public static volatile bool Done;

        private static Thread _worker;

        public static void RunAsync()
        {
            try
            {
                _worker = new Thread(Worker);
                _worker.IsBackground = true;
                _worker.Name = "sotf-integrity";
                _worker.Start();
            }
            catch
            {
                try { Worker(); } catch { }
            }
        }

        private static void Worker()
        {
            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();
            try { Report = Build(); }
            catch (Exception e)
            {
                Report = "INTEGRITY v1\nerror: " + U.Short(e) + "\n";
            }
            sw.Stop();

            Report += "scan_ms: " + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "\n";
            Done = true;

            try { Out.WriteReport("integrity_probe.txt", Report); }
            catch { }
        }

        private sealed class Entry
        {
            public string Rel;
            public string Top;
            public long Size;
            public string Hash;
        }

        private static bool ExcludedDir(string name)
        {
            foreach (var d in ExcludeDirs)
                if (string.Equals(name, d, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool ExcludedFile(string name)
        {
            foreach (var n in ExcludeNames)
                if (string.Equals(name, n, StringComparison.OrdinalIgnoreCase)) return true;
            string ext = Path.GetExtension(name);
            if (!string.IsNullOrEmpty(ext))
                foreach (var e in ExcludeExt)
                    if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Walk(string bepRoot, string top, List<Entry> into, StringBuilder notes)
        {
            string full = Path.Combine(bepRoot, top);
            if (!Directory.Exists(full))
            {
                notes.Append("missing_dir: ").Append(top).Append('\n');
                return;
            }

            Stack<string> dirs = new Stack<string>();
            dirs.Push(full);
            int visited = 0;

            while (dirs.Count > 0)
            {
                string dir = dirs.Pop();
                if (++visited > 2000) { notes.Append("dir_cap: ").Append(top).Append('\n'); break; }

                string[] sub;
                try { sub = Directory.GetDirectories(dir); }
                catch { sub = new string[0]; }
                foreach (var s in sub)
                {
                    string nm = Path.GetFileName(s);
                    if (string.IsNullOrEmpty(nm)) continue;
                    if (ExcludedDir(nm)) continue;
                    dirs.Push(s);
                }

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }

                foreach (var f in files)
                {
                    string nm = Path.GetFileName(f);
                    if (string.IsNullOrEmpty(nm)) continue;
                    if (ExcludedFile(nm)) continue;

                    var e = new Entry { Top = top };
                    e.Rel = Norm(f.Substring(bepRoot.Length));

                    FileInfo fi = null;
                    try { fi = new FileInfo(f); e.Size = fi.Length; }
                    catch { e.Size = -1; }

                    if (e.Size > MaxBytesPerFile)
                    {
                        e.Hash = "too_large";
                    }
                    else
                    {
                        e.Hash = HashFile(f);
                    }
                    into.Add(e);

                    if (into.Count >= MaxFiles)
                    {
                        notes.Append("file_cap_reached: ").Append(MaxFiles).Append('\n');
                        return;
                    }
                }
            }
        }

        private static string Norm(string p)
        {
            return p.Replace('\\', '/').TrimStart('/');
        }

        private static string HashFile(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite, 1 << 16))
                using (var sha = SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(fs);
                    var sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            }
            catch (IOException)
            {
                return "io_error";
            }
            catch (UnauthorizedAccessException)
            {
                return "access_denied";
            }
            catch (Exception e)
            {
                return "err:" + U.Short(e);
            }
        }

        private static string HashText(string text)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                    var sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            }
            catch
            {
                return "";
            }
        }

        private static string Build()
        {
            string root = Guard.GameRoot;
            string bepRoot = string.IsNullOrEmpty(root)
                ? "BepInEx"
                : Path.Combine(root, "BepInEx");

            var all = new List<Entry>();
            var notes = new StringBuilder();

            foreach (var t in Targets)
                Walk(bepRoot, t, all, notes);

            all.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));

            var sb = new StringBuilder(1 << 16);
            sb.Append("INTEGRITY v1\n");
            sb.Append("rules_version: ").Append(RULES_VERSION).Append('\n');
            sb.Append("bepinex_root: ").Append(U.Esc(bepRoot)).Append('\n');
            sb.Append("file_count: ").Append(all.Count).Append('\n');

            foreach (var t in Targets)
            {
                var per = new StringBuilder();
                int n = 0;
                foreach (var e in all)
                {
                    if (!string.Equals(e.Top, t, StringComparison.Ordinal)) continue;
                    per.Append(e.Rel).Append('|').Append(e.Size).Append('|').Append(e.Hash).Append('\n');
                    n++;
                }
                string body = per.ToString();
                sb.Append("dir_").Append(t).Append(": files=").Append(n)
                  .Append(" sha256=").Append(HashText(body)).Append('\n');
            }

            var man = new StringBuilder();
            foreach (var e in all)
                man.Append(e.Rel).Append('|').Append(e.Size).Append('|').Append(e.Hash).Append('\n');

            string manifest = man.ToString();
            RootHash = HashText(manifest);
            sb.Append("root_sha256: ").Append(RootHash).Append('\n');

            if (notes.Length > 0)
            {
                sb.Append("notes:\n");
                sb.Append(notes);
            }

            sb.Append("manifest:\n");
            sb.Append(manifest);
            return sb.ToString();
        }
    }
}
