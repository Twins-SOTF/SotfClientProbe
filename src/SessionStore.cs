// SessionStore.cs -- the two figures that outlive a session (deaths, play time).
// Lives outside the process in a small hand-written JSON file, reloaded on
// startup; a grace window decides whether a reconnect continues the run.
// Z: drive first (C: is rebuilt from an image), then probe_out as fallback.

using System;
using System.Globalization;
using System.IO;

namespace SotfClientProbe
{
    internal static class SessionStore
    {
        public static long DeathsCum;
        public static long PlayMsCum;

        public const long GraceMs = 4L * 60L * 60L * 1000L;
        public const long SaveEveryMs = 5L * 60L * 1000L;
        private const string NAME = "sotf_stats.json";

        private static string _path;
        private static bool _loaded;
        private static long _lastSaveMs;
        private static bool _writeFailed;
        private static int _lastDeaths = -1;

        private static string ResolvePath()
        {
            try
            {
                foreach (string root in new string[] { "Z:\\", "Y:\\", "D:\\" })
                {
                    try
                    {
                        if (!Directory.Exists(root)) continue;
                        string p = Path.Combine(root, NAME);
                        File.WriteAllText(p, "{\"probe\":1}", System.Text.Encoding.UTF8);
                        return p;
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                string dir = Out.Dir;
                if (string.IsNullOrEmpty(dir))
                {
                    string root = Guard.GameRoot;
                    if (string.IsNullOrEmpty(root)) root = ".";
                    dir = Path.Combine(root, "BepInEx", "probe_out");
                    Directory.CreateDirectory(dir);
                }
                return Path.Combine(dir, NAME);
            }
            catch { return NAME; }
        }

        public static string FilePath
        {
            get
            {
                if (_path == null) _path = ResolvePath();
                return _path;
            }
        }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string p = FilePath;
                if (!File.Exists(p)) return;
                string s = File.ReadAllText(p, System.Text.Encoding.UTF8);
                if (string.IsNullOrEmpty(s)) return;

                long last = JsonLong(s, "last_ms");
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

                if (last > 0 && now - last > GraceMs)
                {
                    DeathsCum = 0;
                    PlayMsCum = 0;
                    _lastDeaths = -1;
                    Note = "grace window expired -- counters cleared";
                    return;
                }

                DeathsCum = JsonLong(s, "deaths");
                PlayMsCum = JsonLong(s, "play_ms");
                if (DeathsCum < 0) DeathsCum = 0;
                if (PlayMsCum < 0) PlayMsCum = 0;
                Note = "restored from " + p;
            }
            catch
            {
                Note = "store unreadable -- starting at zero";
            }
        }

        public static string Note = "";

        public static void Tick(long nowMs, long dtMs, int deaths)
        {
            try
            {
                if (dtMs > 0 && dtMs <= 5000L) PlayMsCum += dtMs;

                // Deaths arrive as a per-session counter; feed the cumulative
                // total by DELTA, re-baseline on reset.
                if (deaths >= 0)
                {
                    if (_lastDeaths < 0)
                    {
                        _lastDeaths = deaths;
                    }
                    else if (deaths > _lastDeaths)
                    {
                        DeathsCum += (deaths - _lastDeaths);
                        _lastDeaths = deaths;
                    }
                    else if (deaths < _lastDeaths)
                    {
                        _lastDeaths = deaths;
                    }
                }

                if (_lastSaveMs == 0) _lastSaveMs = nowMs;
                if (nowMs - _lastSaveMs >= SaveEveryMs)
                {
                    _lastSaveMs = nowMs;
                    Save();
                }
            }
            catch { }
        }

        public static void Clear()
        {
            DeathsCum = 0;
            PlayMsCum = 0;
            _lastSaveMs = 0;
            Note = "cleared by hand";
            Save();
        }

        public static void Save()
        {
            if (_writeFailed) return;
            try
            {
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                var sb = new System.Text.StringBuilder(96);
                sb.Append("{\"deaths\":")
                  .Append(DeathsCum.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"play_ms\":")
                  .Append(PlayMsCum.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"last_ms\":")
                  .Append(now.ToString(CultureInfo.InvariantCulture))
                  .Append('}');
                File.WriteAllText(FilePath, sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch
            {
                _writeFailed = true;
            }
        }

        private static long JsonLong(string s, string key)
        {
            try
            {
                int i = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) return 0;
                i = s.IndexOf(':', i);
                if (i < 0) return 0;
                i++;
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
                int j = i;
                while (j < s.Length && (s[j] == '-' || (s[j] >= '0' && s[j] <= '9'))) j++;
                if (j <= i) return 0;
                long v;
                if (long.TryParse(s.Substring(i, j - i), NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out v)) return v;
                return 0;
            }
            catch { return 0; }
        }
    }
}
