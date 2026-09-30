// CrossCheck.cs -- A1: live cross-check between server events and local hits.
//
// The server records a player_hit every time an attack resolves on a damage
// node near a player, but cannot tell who was hit (victim_steamid is always
// null on a dedicated server). So a raw server count is not a hit count: this
// module pairs the two sides and separates real hits from phantoms.
//
// Pairing: 1) server ts shifted onto the client clock using Link.OffsetMs;
// 2) nearest local hit within +/- PairWindowMs; 3) when both sides carry a
// position they must be within PairMaxDistM (damage values repeat constantly,
// so position is what makes a pair trustworthy). The pairing functions are
// pure list-to-list logic so they can move to the database server unchanged.
//
// THREADING: NoteLocal() is called from the patch/main thread; Pull() runs on
// a background thread. All shared state is behind one lock. Nothing here
// touches a Unity object.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SotfClientProbe
{
    internal static class CrossCheck
    {
        public const int PairWindowMs = 1500;
        public const double PairMaxDistM = 5.0;
        public const double BlockRatio = 0.10;
        public const double BlockTol = 0.35;
        public const int PullEveryMs = 10000;
        public const int SrvMax = 400;

        internal struct LocalHit
        {
            public long T;
            public double Hp;
            public double Dmg;
            public bool Block;
            public bool HavePos;
            public double X, Y, Z;
            public bool Used;
        }

        internal struct SrvHit
        {
            public long T;
            public double Raw;
            public bool Blocked;
            public bool Parried;
            public bool HavePos;
            public double X, Y, Z;
            public string Attacker;
            public string Channel;
        }

        internal struct Pair
        {
            public long T;
            public double Raw;
            public double Dmg;
            public bool Blocked;
            public bool Parried;
            public double DistM;
            public double DtMs;
        }

        private static readonly object _lk = new object();
        private static readonly List<LocalHit> _local = new List<LocalHit>();
        private static readonly List<Pair> _pairs = new List<Pair>();
        private static readonly List<SrvHit> _phantom = new List<SrvHit>();

        private static int _srvTotal;
        private static int _matched;
        private static int _phantomN;
        private static int _blockMatched;
        private static int _blockRatioOk;
        private static int _parriedNoDmg;
        private static int _localTotal;
        private static int _localUnmatched;
        private static long _lastPull;
        private static string _lastErr = "";
        private static int _pulls;
        private static bool _ran;

        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static bool Ran { get { lock (_lk) { return _ran; } } }
        public static int SrvTotal { get { lock (_lk) { return _srvTotal; } } }
        public static int Matched { get { lock (_lk) { return _matched; } } }
        public static int Phantom { get { lock (_lk) { return _phantomN; } } }
        public static int LocalTotal { get { lock (_lk) { return _localTotal; } } }
        public static int LocalUnmatched { get { lock (_lk) { return _localUnmatched; } } }
        public static int Pulls { get { lock (_lk) { return _pulls; } } }
        public static string LastErr { get { lock (_lk) { return _lastErr; } } }

        public static void Reset()
        {
            lock (_lk)
            {
                _local.Clear();
                _pairs.Clear();
                _phantom.Clear();
                _srvTotal = _matched = _phantomN = 0;
                _blockMatched = _blockRatioOk = _parriedNoDmg = 0;
                _localTotal = _localUnmatched = 0;
                _lastPull = 0;
                _lastErr = "";
                _pulls = 0;
                _ran = false;
            }
        }

        public static void NoteLocal(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            try
            {
                LocalHit h = new LocalHit();
                string tsu = GetStr(line, "ts_utc");
                if (tsu.Length == 0) tsu = GetStr(line, "ts");
                h.T = ParseClientMs(tsu);
                if (h.T <= 0) return;

                h.Hp = GetNum(line, "hp");
                h.Block = GetBool(line, "block");
                h.Dmg = FirstArgNumber(line);
                string v3 = ArgVector3(line);
                if (v3.Length > 0)
                {
                    double x, y, z;
                    if (ParseVec3(v3, out x, out y, out z))
                    {
                        h.HavePos = true; h.X = x; h.Y = y; h.Z = z;
                    }
                }
                h.Used = false;

                lock (_lk) { _local.Add(h); _localTotal++; _ran = true; }
            }
            catch { }
        }

        public static void Tick()
        {
            if (!Link.Online) return;
            long now = Environment.TickCount64;
            lock (_lk)
            {
                if (_lastPull != 0 && now - _lastPull < PullEveryMs) return;
                _lastPull = now;
            }

            var th = new Thread(PullAndMatch);
            th.IsBackground = true;
            th.Name = "SotfCrossCheck";
            th.Start();
        }

        private static void PullAndMatch()
        {
            try
            {
                string host;
                lock (_lk) { host = Link.Host; }
                if (string.IsNullOrEmpty(host)) return;

                string body = Link.Raw("GET", "/events?max=" + SrvMax, "");
                lock (_lk) { _pulls++; }
                if (string.IsNullOrEmpty(body)) return;

                var srv = new List<SrvHit>();
                foreach (string raw in body.Split('\n'))
                {
                    string ln = raw.Trim();
                    if (ln.Length < 10 || ln[0] != '{') continue;
                    SrvHit s = ParseSrv(ln);
                    if (s.T > 0) srv.Add(s);
                }
                if (srv.Count == 0) return;

                lock (_lk)
                {
                    _srvTotal = srv.Count;
                    MatchLocked(srv);
                }
            }
            catch (Exception ex)
            {
                lock (_lk) { _lastErr = ex.Message; }
            }
        }

        private static void MatchLocked(List<SrvHit> srv)
        {
            _pairs.Clear();
            _phantom.Clear();
            _matched = _phantomN = 0;
            _blockMatched = _blockRatioOk = _parriedNoDmg = 0;

            double off = Link.OffsetMs;

            foreach (SrvHit s in srv)
            {
                long want = s.T - (long)Math.Round(off);

                int best = -1;
                long bestDt = long.MaxValue;
                double bestDist = -1;

                for (int i = 0; i < _local.Count; i++)
                {
                    LocalHit h = _local[i];
                    if (h.Used) continue;

                    long dt = h.T - want;
                    if (dt < 0) dt = -dt;
                    if (dt > PairWindowMs) continue;

                    double dist = -1;
                    if (s.HavePos && h.HavePos)
                    {
                        dist = Dist(s.X, s.Y, s.Z, h.X, h.Y, h.Z);
                        if (dist > PairMaxDistM) continue;
                    }

                    if (dt < bestDt || (dt == bestDt && dist >= 0 && (bestDist < 0 || dist < bestDist)))
                    {
                        best = i; bestDt = dt; bestDist = dist;
                    }
                }

                if (best < 0)
                {
                    _phantom.Add(s);
                    _phantomN++;
                    continue;
                }

                LocalHit m = _local[best];
                _local[best] = new LocalHit
                {
                    T = m.T, Hp = m.Hp, Dmg = m.Dmg, Block = m.Block,
                    HavePos = m.HavePos, X = m.X, Y = m.Y, Z = m.Z, Used = true
                };

                _pairs.Add(new Pair
                {
                    T = m.T, Raw = s.Raw, Dmg = m.Dmg,
                    Blocked = s.Blocked, Parried = s.Parried,
                    DistM = bestDist, DtMs = bestDt
                });
                _matched++;

                if (s.Blocked && m.Dmg > 0 && s.Raw > 0)
                {
                    _blockMatched++;
                    double r = m.Dmg / s.Raw;
                    if (Math.Abs(r - BlockRatio) <= BlockRatio * BlockTol) _blockRatioOk++;
                }
                if (s.Parried && m.Dmg <= 0.001) _parriedNoDmg++;
            }

            _localUnmatched = 0;
            for (int i = 0; i < _local.Count; i++)
                if (!_local[i].Used) _localUnmatched++;
        }

        public static string StatusText()
        {
            lock (_lk)
            {
                if (!Link.Online) return "XCHK OFFLINE";
                if (_srvTotal == 0) return "XCHK --";
                int pct = (int)Math.Round(100.0 * _phantomN / (_srvTotal == 0 ? 1 : _srvTotal));
                return "XCHK real=" + _matched + " phantom=" + _phantomN + " (" + pct + "%)";
            }
        }

        public static string DiagText()
        {
            lock (_lk)
            {
                return "xchk srv=" + _srvTotal + " matched=" + _matched
                     + " phantom=" + _phantomN
                     + " block_ok=" + _blockRatioOk + "/" + _blockMatched
                     + " parry_nodmg=" + _parriedNoDmg
                     + " local=" + _localTotal + " env=" + _localUnmatched
                     + " pulls=" + _pulls
                     + (_lastErr.Length > 0 ? " err=" + _lastErr : "");
            }
        }

        public static void WriteReport(string dir, string tag)
        {
            try
            {
                var sb = new StringBuilder();
                lock (_lk)
                {
                    sb.Append("CrossCheck report  ").Append(tag).Append('\n');
                    sb.Append("offset_ms=").Append(Link.OffsetMs.ToString("0.0", CultureInfo.InvariantCulture))
                      .Append("  rtt_ms=").Append(Link.BestRttMs.ToString("0.0", CultureInfo.InvariantCulture))
                      .Append("  pair_window_ms=").Append(PairWindowMs)
                      .Append("  max_dist_m=").Append(PairMaxDistM.ToString("0.0", CultureInfo.InvariantCulture))
                      .Append('\n');
                    sb.Append("server_hits=").Append(_srvTotal)
                      .Append("  real=").Append(_matched)
                      .Append("  phantom=").Append(_phantomN)
                      .Append("  local_hits=").Append(_localTotal)
                      .Append("  environment=").Append(_localUnmatched)
                      .Append('\n');
                    if (_blockMatched > 0)
                        sb.Append("block_reduction_ok=").Append(_blockRatioOk).Append('/').Append(_blockMatched)
                          .Append("  (expect dmg/raw ~= ").Append(BlockRatio.ToString(CultureInfo.InvariantCulture)).Append(")\n");
                    sb.Append("parry_no_damage=").Append(_parriedNoDmg).Append('\n');

                    sb.Append("\n-- real hits (server raw vs client hp lost) --\n");
                    for (int i = 0; i < _pairs.Count && i < 200; i++)
                    {
                        Pair p = _pairs[i];
                        sb.Append(FromMs(p.T).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                          .Append("  raw=").Append(p.Raw.ToString("0.0", CultureInfo.InvariantCulture))
                          .Append("  lost=").Append(p.Dmg.ToString("0.0", CultureInfo.InvariantCulture))
                          .Append("  blocked=").Append(p.Blocked ? 1 : 0)
                          .Append("  parried=").Append(p.Parried ? 1 : 0)
                          .Append("  dt=").Append(p.DtMs).Append("ms")
                          .Append("  dist=").Append(p.DistM < 0 ? "-" : p.DistM.ToString("0.00", CultureInfo.InvariantCulture))
                          .Append('\n');
                    }

                    sb.Append("\n-- phantom (server recorded a hit, client lost nothing) --\n");
                    for (int i = 0; i < _phantom.Count && i < 200; i++)
                    {
                        SrvHit s = _phantom[i];
                        sb.Append(FromMs(s.T).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                          .Append("  raw=").Append(s.Raw.ToString("0.0", CultureInfo.InvariantCulture))
                          .Append("  by=").Append(s.Attacker.Length == 0 ? "?" : s.Attacker)
                          .Append("  ch=").Append(s.Channel.Length == 0 ? "?" : s.Channel)
                          .Append('\n');
                    }
                }

                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "crosscheck_" + tag + ".txt");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static SrvHit ParseSrv(string ln)
        {
            SrvHit s = new SrvHit();
            s.T = ParseServerMs(GetStr(ln, "ts"));
            if (s.T <= 0) return s;
            s.Raw = GetNum(ln, "raw_damage");
            s.Blocked = GetBool(ln, "was_blocked");
            s.Parried = GetBool(ln, "was_parried");
            s.Attacker = GetStr(ln, "attacker_type");
            s.Channel = GetStr(ln, "channel");

            double x, y, z;
            if (TryPos(ln, "player_pos", out x, out y, out z) ||
                TryPos(ln, "pos", out x, out y, out z))
            {
                s.HavePos = true; s.X = x; s.Y = y; s.Z = z;
            }
            return s;
        }

        private static bool TryPos(string json, string key, out double x, out double y, out double z)
        {
            x = y = z = 0;
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return false;
            i = json.IndexOf('{', i);
            if (i < 0) return false;
            int e = json.IndexOf('}', i);
            if (e < 0) return false;
            string sub = json.Substring(i, e - i + 1);
            x = GetNum(sub, "x"); y = GetNum(sub, "y"); z = GetNum(sub, "z");
            return true;
        }

        private static double Dist(double ax, double ay, double az, double bx, double by, double bz)
        {
            double dx = ax - bx, dy = ay - by, dz = az - bz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static string GetStr(string json, string key)
        {
            try
            {
                string pat = "\"" + key + "\"";
                int i = json.IndexOf(pat, StringComparison.Ordinal);
                if (i < 0) return "";
                i += pat.Length;
                while (i < json.Length && (json[i] == ' ' || json[i] == ':')) i++;
                if (i >= json.Length || json[i] != '"') return "";
                i++;
                int j = json.IndexOf('"', i);
                if (j < 0) return "";
                return json.Substring(i, j - i);
            }
            catch { return ""; }
        }

        private static double GetNum(string json, string key)
        {
            try
            {
                string pat = "\"" + key + "\"";
                int i = json.IndexOf(pat, StringComparison.Ordinal);
                if (i < 0) return 0;
                i += pat.Length;
                while (i < json.Length && (json[i] == ' ' || json[i] == ':')) i++;
                int j = i;
                bool any = false;
                while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-' ||
                       json[j] == '+' || json[j] == '.' || json[j] == 'e' || json[j] == 'E'))
                { j++; any = true; }
                if (!any) return 0;
                double v;
                double.TryParse(json.Substring(i, j - i), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out v);
                return v;
            }
            catch { return 0; }
        }

        private static bool GetBool(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return false;
            i = json.IndexOf(':', i);
            if (i < 0) return false;
            i++;
            while (i < json.Length && json[i] == ' ') i++;
            if (i < json.Length && (json[i] == 't' || json[i] == 'T')) return true;
            if (i < json.Length && json[i] == '1') return true;
            return false;
        }

        private static double FirstArgNumber(string line)
        {
            try
            {
                int i = line.IndexOf("\"args\":[", StringComparison.Ordinal);
                if (i < 0) return 0;
                i += 8;
                int e = line.IndexOf('}', i);
                if (e < 0) return 0;
                string one = line.Substring(i, e - i + 1);
                return GetNum(one, "v");
            }
            catch { return 0; }
        }

        private static string ArgVector3(string line)
        {
            try
            {
                int i = line.IndexOf("UnityEngine.Vector3", StringComparison.Ordinal);
                if (i < 0) return "";
                int v = line.IndexOf("\"v\":\"", i, StringComparison.Ordinal);
                if (v < 0) return "";
                v += 5;
                int e = line.IndexOf('"', v);
                if (e < 0) return "";
                return line.Substring(v, e - v);
            }
            catch { return ""; }
        }

        private static bool ParseVec3(string s, out double x, out double y, out double z)
        {
            x = y = z = 0;
            try
            {
                int a = s.IndexOf('(');
                int b = s.IndexOf(')');
                if (a < 0 || b < a) return false;
                string[] p = s.Substring(a + 1, b - a - 1).Split(',');
                if (p.Length < 3) return false;
                double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y);
                double.TryParse(p[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z);
                return true;
            }
            catch { return false; }
        }

        private static long ParseClientMs(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            DateTime dt;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                return (long)(dt - Epoch).TotalMilliseconds;
            return 0;
        }

        private static long ParseServerMs(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            DateTime dt;
            string[] fmts = { "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:ssZ",
                              "yyyy-MM-ddTHH:mm:ss.fffzzz" };
            if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                return (long)(dt - Epoch).TotalMilliseconds;
            return ParseClientMs(s.Replace('T', ' ').TrimEnd('Z'));
        }

        private static DateTime FromMs(long ms)
        {
            return Epoch.AddMilliseconds(ms);
        }
    }
}
