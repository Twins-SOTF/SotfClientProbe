// LocalPlayer.cs -- the ONE hard-wired entry point into the live game state.
//
// Previous versions tried to FIND the player by scanning tens of thousands of
// type names; that failed twice (40-item cap during collection, and running
// once at plugin load while still in the main menu). The interop dump shows
// the game hands us a singleton instead:
//
//     TheForest.Utils.LocalPlayer          (Sons.dll, 398 members)
//         _instance            static singleton
//         get_Vitals()      -> Vitals      (GetHealth / GetMaxHealth / GetStamina)
//         get_Inventory()   -> inventory object
//         get_PlayerBase()  -> player entity (Transform carries position)
//         get_Entity()      -> Bolt entity
//
// So we stop searching and just walk that path. THREADING: every call here
// touches UnityEngine.Object and MUST run on the Unity main thread.

using System;
using System.Text;

namespace SotfClientProbe
{
    internal static class LocalPlayer
    {
        public const string TypeName = "TheForest.Utils.LocalPlayer";
        public const string VitalsTypeName = "Vitals";

        private static Type _type;
        private static bool _typeLooked;
        private static object _instance;
        private static DateTime _instanceAt;
        private static readonly TimeSpan InstanceTtl = TimeSpan.FromSeconds(5);

        public static string Diag = "";

        public static Type PlayerType()
        {
            if (_typeLooked) return _type;
            _typeLooked = true;
            _type = U.FindType(TypeName);
            return _type;
        }

        /// The live singleton. Returns null while in the main menu -- callers
        /// must simply try again later.
        public static object Instance()
        {
            try
            {
                if (_instance != null && (DateTime.Now - _instanceAt) < InstanceTtl)
                    return _instance;

                Type t = PlayerType();
                if (t == null) { _instance = null; return null; }

                object v; string err;
                if (U.TryGetStatic(t, "_instance", out v, out err) && v != null)
                { _instance = v; _instanceAt = DateTime.Now; return _instance; }

                if (U.TryGetStatic(t, "Instance", out v, out err) && v != null)
                { _instance = v; _instanceAt = DateTime.Now; return _instance; }

                if (U.TryCallStatic(t, "get__instance", out v, out err) && v != null)
                { _instance = v; _instanceAt = DateTime.Now; return _instance; }

                _instance = null;
                return null;
            }
            catch
            {
                _instance = null;
                return null;
            }
        }

        public static object Member(string name)
        {
            return MemberOf(Instance(), name);
        }

        public static object MemberOf(object target, string name)
        {
            if (target == null) return null;

            object v; string err;
            if (U.TryGet(target, name, out v, out err) && v != null) return v;

            if (U.TryGet(target, "_" + Lower1(name), out v, out err) && v != null) return v;
            if (U.TryCall(target, "get_" + name, out v, out err) && v != null) return v;

            return null;
        }

        public static object Vitals() { return Member("Vitals"); }
        public static object Inventory() { return Member("Inventory"); }
        public static object PlayerBase() { return Member("PlayerBase"); }
        public static object Entity() { return Member("Entity"); }

        public static object Stats() { return Member("Stats"); }
        public static object StatsMember(string name) { return MemberOf(Stats(), name); }

        private static string Lower1(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length == 1) return s.ToLowerInvariant();
            return char.ToLowerInvariant(s[0]) + s.Substring(1);
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.Append("=== local player entry point ===\n");
            sb.Append("time      : ").Append(U.Now()).Append('\n');

            Type t = PlayerType();
            sb.Append("type      : ").Append(t == null ? "NOT FOUND (" + TypeName + ")" : t.FullName).Append('\n');

            object lp = Instance();
            sb.Append("instance  : ").Append(lp == null ? "null (not in a session yet)" : U.TypeName(lp)).Append('\n');

            if (lp == null)
            {
                sb.Append("\nNOTE: null here is EXPECTED in the main menu.\n");
                sb.Append("      The probe retries every few seconds while a session runs.\n");
                Diag = sb.ToString();
                return Diag;
            }

            sb.Append('\n');
            ReportMember(sb, "Vitals", Vitals());
            ReportMember(sb, "Inventory", Inventory());
            ReportMember(sb, "PlayerBase", PlayerBase());
            ReportMember(sb, "Entity", Entity());
            ReportMember(sb, "Stats", Stats());

            object st = Stats();
            if (st != null)
            {
                sb.Append('\n');
                sb.Append("Stats members used for the death counter:\n");
                foreach (string mn in new string[] {
                    "_playerDeathCount", "get__playerDeathCount",
                    "PlayerIsDowned", "_playerIsDowned", "IsDead",
                    "_maxDeathCount", "_playerDownIntroEvent" })
                {
                    object val = MemberOf(st, mn);
                    sb.Append("  ").Append(mn).Append(" -> ")
                      .Append(val == null ? "null" : (U.TypeName(val) + " = " + val)).Append('\n');
                }
            }
            else
            {
                sb.Append('\n');
                sb.Append("Stats: null -- death count will fall back to edge detection.\n");
            }

            object vit = Vitals();
            if (vit != null)
            {
                sb.Append('\n');
                sb.Append("Vitals members that return a number:\n");
                int shown = 0;
                foreach (string mn in new string[] {
                    "GetHealth", "GetMaxHealth", "GetStamina", "GetVitality",
                    "GetHealthFactor", "GetStrength", "GetFullness", "GetHydration",
                    "GetRested", "GetSicknessFactor", "GetColdFactor" })
                {
                    object val; string err;
                    if (!U.TryCall(vit, mn, out val, out err))
                    {
                        sb.Append("  ").Append(mn).Append("() -> ").Append(err).Append('\n');
                        continue;
                    }
                    float f;
                    bool ok = U.AsFloat(val, out f);
                    sb.Append("  ").Append(mn).Append("() -> ")
                      .Append(ok ? U.Num(f) : "[" + U.TypeName(val) + "]").Append('\n');
                    if (ok) shown++;
                }
                sb.Append("  numeric: ").Append(U.Num(shown)).Append('\n');
            }

            Diag = sb.ToString();
            return Diag;
        }

        private static void ReportMember(StringBuilder sb, string name, object v)
        {
            sb.Append("  ").Append(name).Append(": ")
              .Append(v == null ? "null" : U.TypeName(v)).Append('\n');
        }
    }
}
