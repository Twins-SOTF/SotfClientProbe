// PlayerInfoProbe.cs -- read everything the game will tell us about the player.
//
// Sons.Vitals and PlayerStats expose the whole survival state by name; every
// name below was read out of the interop assemblies rather than invented.
//
// Two phases: the first pass dumps EVERY readable member to
// player_info_probe.txt (so a renamed member is visible immediately); after
// that, sampling reads the confirmed list and nothing else. Anything that
// cannot be read is left out rather than defaulted -- "read as zero" and
// "could not read" are different claims.
//
// THREADING: main thread only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SotfClientProbe
{
    internal static class PlayerInfoProbe
    {
        private static readonly string[] VitalsMethods =
        {
            "GetHealth", "GetMaxHealth", "GetHealthFactor",
            "GetStamina", "GetStaminaFactor",
            "GetFullness", "GetFullnessFactor",
            "GetHydration", "GetHydrationFactor",
            "GetRest", "GetRestFactor",
            "GetStrength", "GetStrengthFactor",
            "GetVitality", "GetVitalityFactor", "GetResolvedMaxVitality",
            "GetMaxVitalityFactor",
            "GetSicknessFactor",
            "GetVitalityLostFromColdFactor", "GetVitalityLostFromCold"
        };

        private static readonly string[] VitalsProps =
        {
            "CurrentStrengthLevel", "Sickness", "ColdFactor",
            "Temperature", "InteriorSpaceWarmth", "Rested"
        };

        private static readonly string[] VitalsFlags =
        {
            "IsAlive", "HasDied", "IsLowHealth",
            "IsTired", "IsHungry", "IsStarving",
            "IsThirsty", "IsDehydrated", "IsSick", "IsCold",
            "IsRestedBuffed", "IsFullnessBuffed", "IsHydrationBuffed"
        };

        private static readonly string[] StatsProps =
        {
            "_playerDeathCount", "PlayerIsDowned",
            "IsBloody", "IsMuddy", "IsBurning", "IsFightingBoss",
            "StealthRatingClamped", "ComfortRatingClamped", "WaterResistRatingClamped"
        };

        private static bool _probed;
        private static bool _vitalsOk;
        private static bool _statsOk;
        private static readonly List<string> _okNames = new List<string>();
        private static readonly Dictionary<string, bool> _okMap = new Dictionary<string, bool>();
        private static readonly Dictionary<string, float> _last = new Dictionary<string, float>();
        private static long _lastEmitMs;
        private const long MinEmitMs = 2000L;

        public static string Report;

        public static void Discover() { Discover(false); }

        public static void Discover(bool force)
        {
            if (_probed && !force) return;
            _probed = true;

            var sb = new StringBuilder();
            sb.Append("=== player info discovery ===\n");
            sb.Append("time   : ").Append(U.Now()).Append('\n');

            object vitals = LocalPlayer.Vitals();
            object stats = LocalPlayer.Stats();

            sb.Append("vitals : ").Append(vitals == null ? "(null)" : U.TypeName(vitals)).Append('\n');
            sb.Append("stats  : ").Append(stats == null ? "(null)" : U.TypeName(stats)).Append('\n');
            sb.Append('\n');

            if (vitals != null)
            {
                _vitalsOk = true;
                sb.Append("--- Vitals: zero-arg methods ---\n");
                DumpMembers(vitals, sb, true);
                sb.Append("--- Vitals: properties/fields ---\n");
                DumpMembers(vitals, sb, false);
            }
            else
            {
                sb.Append("--- Vitals unavailable ---\n");
            }

            if (stats != null)
            {
                _statsOk = true;
                sb.Append('\n');
                sb.Append("--- PlayerStats: properties/fields ---\n");
                DumpMembers(stats, sb, false);
            }
            else
            {
                sb.Append('\n');
                sb.Append("--- PlayerStats unavailable ---\n");
            }

            sb.Append('\n');
            sb.Append("--- confirmed read list resolution ---\n");
            ResolveAll(vitals, stats, sb);

            Report = sb.ToString();
        }

        private static void DumpMembers(object o, StringBuilder sb, bool methods)
        {
            if (o == null) return;
            Type t = o.GetType();
            int shown = 0;

            try
            {
                if (methods)
                {
                    foreach (MethodInfo m in t.GetMethods(U.BF))
                    {
                        if (m == null) continue;
                        if (m.GetParameters().Length != 0) continue;
                        if (m.IsGenericMethodDefinition) continue;
                        if (m.ReturnType == typeof(void)) continue;
                        string n = m.Name;
                        if (n.StartsWith("get_", StringComparison.Ordinal) ||
                            n.StartsWith("set_", StringComparison.Ordinal)) continue;

                        // ONLY read-shaped names are called. TriggerDeath() is
                        // zero-argument and would mutate the game -- a
                        // diagnostic must never kill the player.
                        if (!IsReadOnlyName(n)) continue;

                        object v; string err;
                        if (!U.TryCall(o, n, out v, out err)) continue;
                        shown++;
                        if (shown > 120) { sb.Append("     ... (truncated)\n"); break; }
                        sb.Append("     ").Append(n).Append("() = ").Append(Describe(v)).Append('\n');
                    }
                }
                else
                {
                    foreach (PropertyInfo p in t.GetProperties(U.BF))
                    {
                        if (p == null || !p.CanRead) continue;
                        if (p.GetIndexParameters().Length != 0) continue;
                        object v; string err;
                        if (!U.TryGet(o, p.Name, out v, out err)) continue;
                        shown++;
                        if (shown > 160) { sb.Append("     ... (truncated)\n"); break; }
                        sb.Append("     P ").Append(p.Name).Append(" = ").Append(Describe(v)).Append('\n');
                    }
                    foreach (FieldInfo f in t.GetFields(U.BF))
                    {
                        if (f == null) continue;
                        object v; string err;
                        if (!U.TryGet(o, f.Name, out v, out err)) continue;
                        shown++;
                        if (shown > 200) { sb.Append("     ... (truncated)\n"); break; }
                        sb.Append("     F ").Append(f.Name).Append(" = ").Append(Describe(v)).Append('\n');
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append("     enum threw ").Append(U.Short(e)).Append('\n');
            }

            if (shown == 0) sb.Append("     (nothing readable)\n");
        }

        private static bool IsReadOnlyName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (n.StartsWith("Get", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Is", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Has", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Can", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Should", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Does", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Contains", StringComparison.Ordinal)) return true;
            return false;
        }

        private static string Describe(object v)
        {
            if (v == null) return "null";
            if (v is bool) return ((bool)v) ? "true" : "false";
            float f;
            if (U.AsFloat(v, out f)) return U.Num(f) + "  (" + U.TypeName(v) + ")";
            if (v is string)
            {
                string s = (string)v;
                if (s.Length > 160) s = s.Substring(0, 160) + "...";
                return "\"" + s + "\"";
            }
            return "<" + U.TypeName(v) + ">";
        }

        private static void ResolveAll(object vitals, object stats, StringBuilder sb)
        {
            int ok = 0, no = 0;

            if (vitals != null)
            {
                foreach (string n in VitalsMethods)
                    if (TryResolve(vitals, n, true, sb)) ok++; else no++;
                foreach (string n in VitalsProps)
                    if (TryResolve(vitals, n, false, sb)) ok++; else no++;
                foreach (string n in VitalsFlags)
                    if (TryResolve(vitals, n, false, sb)) ok++; else no++;
            }
            if (stats != null)
            {
                foreach (string n in StatsProps)
                    if (TryResolve(stats, n, false, sb)) ok++; else no++;
            }

            sb.Append("     resolved ").Append(U.Num(ok))
              .Append(", unavailable ").Append(U.Num(no)).Append('\n');
        }

        private static bool TryResolve(object o, string name, bool preferMethod, StringBuilder sb)
        {
            object v; string err;
            bool got;

            if (preferMethod)
            {
                got = U.TryCall(o, name, out v, out err);
                if (!got) { got = U.TryGet(o, name, out v, out err); if (got) preferMethod = false; }
            }
            else
            {
                got = U.TryGet(o, name, out v, out err);
                if (!got) { got = U.TryCall(o, name, out v, out err); if (got) preferMethod = true; }
            }

            if (!got || v == null) return false;

            float f;
            if (U.AsFloat(v, out f) || v is bool)
            {
                _okMap[name] = preferMethod;
                if (!_okNames.Contains(name)) _okNames.Add(name);
                return true;
            }
            if (sb != null)
                sb.Append("     skip ").Append(name).Append(" -> ").Append(U.TypeName(v)).Append('\n');
            return false;
        }

        private static bool ReadMember(object vitals, object stats, string name,
                                       bool isMethod, out object v)
        {
            v = null;
            string err;
            for (int pass = 0; pass < 2; pass++)
            {
                object target = pass == 0 ? vitals : stats;
                if (target == null) continue;

                bool got = isMethod
                    ? U.TryCall(target, name, out v, out err)
                    : U.TryGet(target, name, out v, out err);
                if (!got)
                    got = isMethod
                        ? U.TryGet(target, name, out v, out err)
                        : U.TryCall(target, name, out v, out err);
                if (got && v != null) return true;
            }
            return false;
        }

        public static string Sample(long nowMs)
        {
            try
            {
                if (!_probed) Discover();

                object vitals = LocalPlayer.Vitals();
                object stats = LocalPlayer.Stats();

                var vals = new Dictionary<string, float>();

                foreach (string n in _okNames)
                {
                    bool isMethod;
                    if (!_okMap.TryGetValue(n, out isMethod)) continue;

                    object v;
                    if (!ReadMember(vitals, stats, n, isMethod, out v)) continue;

                    if (v is bool) { vals[Key(n)] = ((bool)v) ? 1f : 0f; continue; }
                    float f;
                    if (U.AsFloat(v, out f)) vals[Key(n)] = f;
                }

                if (vals.Count == 0) return null;

                bool changed = vals.Count != _last.Count;
                if (!changed)
                {
                    foreach (var kv in vals)
                    {
                        float prev;
                        if (!_last.TryGetValue(kv.Key, out prev) ||
                            Math.Abs(prev - kv.Value) > 0.05f)
                        { changed = true; break; }
                    }
                }
                if (!changed) return null;
                if (_lastEmitMs != 0 && nowMs - _lastEmitMs < MinEmitMs) return null;

                _last.Clear();
                foreach (var kv in vals) _last[kv.Key] = kv.Value;
                _lastEmitMs = nowMs;

                var sb = new StringBuilder(256);
                bool first = true;
                foreach (var kv in vals)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(kv.Key).Append("\":").Append(U.Num(kv.Value));
                }
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string Key(string member)
        {
            string n = member;
            if (n.StartsWith("Get", StringComparison.Ordinal) && n.Length > 3) n = n.Substring(3);
            n = n.TrimStart('_');
            return "pi_" + n.ToLowerInvariant();
        }

        public static bool HasData { get { return _okNames.Count > 0; } }

        public static void Reset()
        {
            _last.Clear();
            _lastEmitMs = 0;
        }

        public static string Status()
        {
            return "playerinfo: " + U.Num(_okNames.Count) + " fields"
                   + (_vitalsOk ? " vitals=ok" : " vitals=MISSING")
                   + (_statsOk ? " stats=ok" : " stats=MISSING");
        }
    }
}
