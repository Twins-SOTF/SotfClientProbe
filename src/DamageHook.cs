// DamageHook.cs -- event-driven damage timestamps.
//
// Polling health at 2 Hz cannot tell us WHEN a hit landed. We patch the game's
// own damage entry points and read health the instant one fires. The hook is
// only a TRIGGER: it does not parse the damage value out of the arguments
// (argument layout differs between patches -- that bet is what made
// IsParrying read as "always false" for several versions).
//
// Targets (confirmed in interop):
//   Sons.Ai.Vail.VailActor.ReceivedDamage(...)   any actor taking damage
//   PlayerStats.OnNetworkHit(...)                the local player, from net
//   PlayerStats.Hit(...)
// Patching is best-effort; a missing name is skipped and sampling continues.

using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using System.Text;

namespace SotfClientProbe
{
    internal static class DamageHook
    {
        private static readonly string[][] Targets =
        {
            new string[] { "Sons.Ai.Vail.VailActor", "ReceivedDamage" },
            new string[] { "PlayerStats",            "OnNetworkHit" },
            new string[] { "PlayerStats",            "Hit" }
        };

        public static int Patched;
        public static int Missed;
        public static int Fired;

        /// v2.26: PlayerStats.Hit and PlayerStats.OnNetworkHit both fire for
        /// the same strike one millisecond apart; the second one is dropped
        /// and counted here.
        public static int Dups;

        private const long DupWindowMs = 60L;
        private static string _lastHook;
        private static long _lastHookMs;

        public static string Report;
        private static bool _installed;

        private const int ArgDumpLimit = 8;
        private static int _argDumps;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            var sb = new StringBuilder();
            sb.Append("=== SotF Client Probe -- damage event hooks ===\n");
            sb.Append("time : ").Append(U.Now()).Append('\n');

            try
            {
                var harmony = new Harmony(ProbePlugin.GUID + ".damage");
                MethodInfo post = typeof(DamageHook).GetMethod(
                    "Postfix", BindingFlags.Static | BindingFlags.Public);

                foreach (string[] t in Targets)
                {
                    string tn = t[0], mn = t[1];
                    Type ty = null;
                    try { ty = U.FindType(tn); } catch { }
                    if (ty == null)
                    {
                        sb.Append("  SKIP ").Append(tn).Append('.').Append(mn)
                          .Append("  (type not found)\n");
                        Missed++;
                        continue;
                    }

                    MethodInfo mi = null;
                    try
                    {
                        mi = AccessTools.Method(ty, mn);
                    }
                    catch (Exception e)
                    {
                        sb.Append("  note ").Append(tn).Append('.').Append(mn)
                          .Append(" ambiguous: ").Append(U.Short(e)).Append('\n');
                        try
                        {
                            foreach (MethodInfo c in ty.GetMethods(
                                BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Instance | BindingFlags.Static))
                            {
                                if (c.Name == mn) { mi = c; break; }
                            }
                        }
                        catch { }
                    }

                    if (mi == null)
                    {
                        sb.Append("  SKIP ").Append(tn).Append('.').Append(mn)
                          .Append("  (method not found)\n");
                        Missed++;
                        continue;
                    }

                    try
                    {
                        harmony.Patch(mi, null, new HarmonyMethod(post));
                        Patched++;
                        sb.Append("  OK   ").Append(tn).Append('.').Append(mn)
                          .Append("  (").Append(mi.GetParameters().Length).Append(" args)\n");
                    }
                    catch (Exception e)
                    {
                        Missed++;
                        sb.Append("  FAIL ").Append(tn).Append('.').Append(mn)
                          .Append("  ").Append(U.Short(e)).Append('\n');
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append("  FATAL ").Append(U.Short(e)).Append('\n');
            }

            sb.Append("\npatched: ").Append(U.Num(Patched))
              .Append("   missed: ").Append(U.Num(Missed)).Append('\n');
            Report = sb.ToString();
        }

        /// Harmony postfix. __instance is the actor, __args the arguments.
        public static void Postfix(object __instance, object[] __args,
                                   MethodBase __originalMethod)
        {
            try
            {
                Fired++;

                float hp = 0f;
                bool haveHp = false;
                try { haveHp = VitalsProbe.Sample(out hp); } catch { }

                var sb = new StringBuilder(256);
                sb.Append("\"type\":\"hit_event\"");
                sb.Append(",\"ts\":\"").Append(U.Esc(U.Now())).Append('"');
                sb.Append(",\"ts_utc\":\"").Append(U.Esc(U.NowUtc())).Append('"');
                sb.Append(",\"src\":\"").Append(U.Esc(U.TypeName(__instance))).Append('"');
                sb.Append(",\"hp\":").Append(haveHp ? U.Num(hp) : "null");
                sb.Append(",\"hit_n\":").Append(U.Num(Fired));

                string tn = U.TypeName(__instance);
                bool viaPlayerStats = tn != null && tn.IndexOf(
                    "PlayerStats", StringComparison.OrdinalIgnoreCase) >= 0;
                bool local = viaPlayerStats || IsLocal(__instance);

                string hook = HookName(__instance, __originalMethod);

                if (local && IsDuplicate(hook))
                {
                    Dups++;
                    return;
                }

                bool haveBlock = false;
                bool blocked = false;
                if (local)
                {
                    bool flag;
                    if (BlockFlag(__args, out flag)) { haveBlock = true; blocked = flag; }
                }

                if (local) Stats.OnLocalHit(Environment.TickCount64, FirstNumber(__args), blocked);

                sb.Append(",\"is_local\":").Append(U.Bool(local));
                sb.Append(",\"hook\":\"").Append(U.Esc(hook)).Append('"');

                if (local)
                {
                    try { GearProbe.Sample(); } catch { }

                    sb.Append(",\"weapon\":\"")
                      .Append(U.Esc(string.IsNullOrEmpty(GearProbe.Weapon)
                          ? "" : GearProbe.Weapon)).Append('"');
                    sb.Append(",\"armor\":").Append(U.Bool(GearProbe.HasArmor));
                    sb.Append(",\"armor_pc\":").Append(U.Num(GearProbe.ArmorPieces));
                    if (GearProbe.AncientArmor) sb.Append(",\"ancient\":true");

                    if (haveBlock)
                        sb.Append(",\"block\":").Append(U.Bool(blocked));

                    sb.Append(",\"args\":").Append(DumpArgs(__args));
                }
                else if (_argDumps < ArgDumpLimit)
                {
                    _argDumps++;
                    sb.Append(",\"args\":").Append(DumpArgs(__args));
                }

                // A1: feed this line to the online cross-check. Only for the
                // local player -- non-local hits have no args to compare.
                if (local)
                {
                    try { CrossCheck.NoteLocal(sb.ToString()); } catch { }
                }

                Out.Event(sb.ToString());
                if ((Fired & 7) == 0) Out.Flush();
            }
            catch { }
        }

        private static string HookName(object __instance, MethodBase original)
        {
            try
            {
                if (original != null)
                {
                    Type dt = original.DeclaringType;
                    string tn = dt != null ? (dt.Name ?? "") : "";
                    string mn = original.Name ?? "";
                    if (tn.Length > 0) return tn + "." + mn;
                    if (mn.Length > 0) return mn;
                }
            }
            catch { }
            return U.TypeName(__instance);
        }

        /// Different hook within 60 ms -> duplicate, dropped; SAME hook within
        /// 60 ms -> two real strikes, kept. The window is not updated on a
        /// duplicate, so a three-hook burst collapses to one event.
        private static bool IsDuplicate(string hook)
        {
            try
            {
                long now = Environment.TickCount64;
                if (_lastHook == null) { _lastHook = hook; _lastHookMs = now; return false; }
                if (string.Equals(_lastHook, hook, StringComparison.Ordinal))
                {
                    _lastHook = hook;
                    _lastHookMs = now;
                    return false;
                }
                if (now - _lastHookMs <= DupWindowMs) return true;

                _lastHook = hook;
                _lastHookMs = now;
                return false;
            }
            catch { return false; }
        }

        /// First plain-number argument, used only for the HIT waveform sizing.
        /// Deliberately a guess -- the recorded stream carries the raw args
        /// dump for real analysis.
        private static float FirstNumber(object[] args)
        {
            if (args == null) return Series.Unknown;
            for (int i = 0; i < args.Length; i++)
            {
                object a = args[i];
                if (a == null) continue;
                if (a is float) return (float)a;
                if (a is double) return (float)(double)a;
                if (a is int) return (float)(int)a;
                if (a is short) return (float)(short)a;
                if (a is byte) return (float)(byte)a;

                string s = a as string;
                if (s == null)
                {
                    try { s = a.ToString(); } catch { s = null; }
                }
                if (!string.IsNullOrEmpty(s))
                {
                    float f;
                    if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out f))
                    {
                        if (!float.IsNaN(f) && !float.IsInfinity(f) && f > 0f) return f;
                    }
                }
            }
            return Series.Unknown;
        }

        /// v2.15: the block flag carried in the hit argument tuple. Measured:
        /// every hit with this flag set came through at exactly one tenth of
        /// the raw damage (52.5 -> 5.2500014), which is the block reduction.
        /// Index is a hint, not a contract; ambiguous tuples report nothing.
        private static bool BlockFlag(object[] args, out bool flag)
        {
            flag = false;
            if (args == null) return false;

            const int KnownIndex = 6;
            if (KnownIndex < args.Length && args[KnownIndex] is bool)
            {
                flag = (bool)args[KnownIndex];
                return true;
            }

            int found = -1, count = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is bool) { found = i; count++; }
            }
            if (count == 1)
            {
                flag = (bool)args[found];
                return true;
            }
            return false;
        }

        private static bool IsLocal(object inst)
        {
            try
            {
                if (inst == null) return false;
                object lp = LocalPlayer.Instance();
                if (lp == null) return false;
                foreach (string n in new string[] { "PlayerBase", "Entity", "Vitals" })
                {
                    object m = LocalPlayer.Member(n);
                    if (m == null) continue;
                    if (ReferenceEquals(m, inst)) return true;
                }
            }
            catch { }
            return false;
        }

        private static string DumpArgs(object[] args)
        {
            var sb = new StringBuilder(128);
            sb.Append('[');
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    object a = args[i];
                    if (a == null) { sb.Append("null"); continue; }
                    sb.Append("{\"t\":\"").Append(U.Esc(U.TypeName(a))).Append("\",\"v\":\"");
                    try
                    {
                        string s = a.ToString();
                        if (s != null && s.Length > 60) s = s.Substring(0, 60);
                        sb.Append(U.Esc(s ?? ""));
                    }
                    catch { sb.Append("?"); }
                    sb.Append("\"}");
                }
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
