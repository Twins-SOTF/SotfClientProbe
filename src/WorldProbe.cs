// WorldProbe.cs -- the world's own clock and population.
//
// Sons.Ai.Vail.VailWorldSimulation carries a static _instance exposing
// DaysPassed / TimeInHours / IsNight / CurrentSeason / CurrentWetness /
// ActorCount. One resolution, then a throttled read (10s). Members are read
// as a property first, then as a method. Actor list walking is deliberately
// NOT done -- GetAllActors is an unbounded main-thread walk; get_ActorCount
// is O(1). Unreadable stays absent.

using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SotfClientProbe
{
    internal static class WorldProbe
    {
        private const long MIN_GAP_MS = 10000;
        private const long RETRY_GAP_MS = 15000;
        private static readonly TimeSpan InstTtl = TimeSpan.FromSeconds(30);

        private static bool _resolved;
        private static long _lastAttemptMs;
        private static Type _type;
        private static object _inst;
        private static DateTime _instAt;

        private static string _mDays, _mHour, _mNight, _mSeason, _mWet, _mActors;

        public static bool HaveData { get { return _resolved; } }

        public static float Days = float.NaN;
        public static float Hour = float.NaN;
        public static int Night = -1;
        public static float Wetness = float.NaN;
        public static float Actors = float.NaN;
        public static string Season = "";

        public static string Report;

        public static void Reset()
        {
            _inst = null;
            _instAt = default(DateTime);
        }

        private static bool Resolve()
        {
            if (_resolved) return true;

            long now = Environment.TickCount64;
            if (_lastAttemptMs != 0 && now - _lastAttemptMs < RETRY_GAP_MS) return false;
            _lastAttemptMs = now;

            Type t = null;
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (Type c in types)
                    {
                        string fn;
                        try { fn = c.FullName; } catch { continue; }
                        if (string.IsNullOrEmpty(fn)) continue;
                        if (fn.IndexOf("VailWorldSimulation", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            t = c;
                            break;
                        }
                    }
                    if (t != null) break;
                }
            }
            catch { }

            if (t == null) return false;

            object inst; string err;
            if (!U.TryGetStatic(t, "_instance", out inst, out err) &&
                !U.TryGetStatic(t, "Instance", out inst, out err) &&
                !U.TryCallStatic(t, "get_Instance", out inst, out err))
            {
                return false;
            }
            if (inst == null) return false;

            _type = t;
            _inst = inst;
            _instAt = DateTime.UtcNow;

            _mDays = FirstOf(inst, new string[] { "get_DaysPassed", "DaysPassed", "get_Days", "Days" });
            _mHour = FirstOf(inst, new string[] { "get_TimeInHours", "TimeInHours", "get_HourOfDay", "HourOfDay" });
            _mNight = FirstOf(inst, new string[] { "get_IsNight", "IsNight" });
            _mSeason = FirstOf(inst, new string[] { "get_CurrentSeason", "CurrentSeason" });
            _mWet = FirstOf(inst, new string[] { "get_CurrentWetness", "CurrentWetness" });
            _mActors = FirstOf(inst, new string[] { "get_ActorCount", "ActorCount" });

            _resolved = true;
            Report = BuildReport();
            return true;
        }

        private static string FirstOf(object o, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                object v; string err;
                if (U.TryGet(o, names[i], out v, out err) && v != null) return names[i];
                if (U.TryCall(o, names[i], out v, out err) && v != null) return names[i];
            }
            return null;
        }

        private static bool ReadNum(object o, string member, out float v)
        {
            v = float.NaN;
            if (o == null || string.IsNullOrEmpty(member)) return false;

            object raw; string err;
            if (!U.TryGet(o, member, out raw, out err) &&
                !U.TryCall(o, member, out raw, out err))
                return false;
            if (raw == null) return false;

            if (raw is bool) { v = (bool)raw ? 1f : 0f; return true; }
            return U.AsFloat(raw, out v);
        }

        private static bool ReadText(object o, string member, out string s)
        {
            s = "";
            if (o == null || string.IsNullOrEmpty(member)) return false;

            object raw; string err;
            if (!U.TryGet(o, member, out raw, out err) &&
                !U.TryCall(o, member, out raw, out err))
                return false;
            if (raw == null) return false;
            try { s = raw.ToString(); } catch { return false; }
            return s.Length > 0;
        }

        public static string Sample(long nowMs)
        {
            if (!Resolve()) return null;

            if (lastSampleMs != 0 && nowMs - lastSampleMs < MIN_GAP_MS) return null;

            if (_inst == null || DateTime.UtcNow - _instAt > InstTtl)
            {
                object inst; string err;
                if (U.TryGetStatic(_type, "_instance", out inst, out err) ||
                    U.TryGetStatic(_type, "Instance", out inst, out err))
                {
                    if (inst != null) { _inst = inst; _instAt = DateTime.UtcNow; }
                }
            }
            if (_inst == null) return null;

            lastSampleMs = nowMs;

            float d, h, w, a;
            bool any = false;

            var sb = new StringBuilder(96);

            if (ReadNum(_inst, _mDays, out d)) { Days = d; any = true; }
            if (ReadNum(_inst, _mHour, out h)) { Hour = h; any = true; }
            if (ReadNum(_inst, _mNight, out w)) { Night = w >= 0.5f ? 1 : 0; any = true; }
            if (ReadNum(_inst, _mWet, out w)) { Wetness = w; any = true; }
            if (ReadNum(_inst, _mActors, out a)) { Actors = a; any = true; }

            string season;
            if (ReadText(_inst, _mSeason, out season) && Season != season)
            {
                Season = season;
                any = true;
            }

            if (!any) return null;

            bool first = true;
            if (!float.IsNaN(Days)) { AppendNum(sb, ref first, "day", Days); }
            if (!float.IsNaN(Hour)) { AppendNum(sb, ref first, "hour", Hour); }
            if (Night >= 0) { AppendNum(sb, ref first, "night", Night); }
            if (!float.IsNaN(Wetness)) { AppendNum(sb, ref first, "wet", Wetness); }
            if (!float.IsNaN(Actors)) { AppendNum(sb, ref first, "actors", Actors); }
            if (Season.Length > 0)
            {
                if (!first) sb.Append(',');
                sb.Append("\"season\":\"").Append(U.Esc(Season)).Append('"');
                first = false;
            }

            return first ? null : sb.ToString();
        }

        private static long lastSampleMs;

        private static void AppendNum(StringBuilder sb, ref bool first, string key, float v)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(key).Append("\":").Append(U.Num(v));
            first = false;
        }

        private static string BuildReport()
        {
            var sb = new StringBuilder();
            sb.Append("=== SotF Client Probe -- world probe ===\n");
            sb.Append("time : ").Append(U.Now()).Append('\n');
            sb.Append("type : ").Append(_type == null ? "(none)" : U.TypeName(_type)).Append('\n');
            sb.Append('\n');
            sb.Append("member resolution (null = no readable form found)\n");
            sb.Append("  days    : ").Append(_mDays ?? "(none)").Append('\n');
            sb.Append("  hour    : ").Append(_mHour ?? "(none)").Append('\n');
            sb.Append("  night   : ").Append(_mNight ?? "(none)").Append('\n');
            sb.Append("  season  : ").Append(_mSeason ?? "(none)").Append('\n');
            sb.Append("  wetness : ").Append(_mWet ?? "(none)").Append('\n');
            sb.Append("  actors  : ").Append(_mActors ?? "(none)").Append('\n');
            sb.Append('\n');
            sb.Append("Actors are counted, not enumerated. GetAllActors exists\n");
            sb.Append("but walking it on the main thread is a measured risk.\n");
            return sb.ToString();
        }
    }
}
