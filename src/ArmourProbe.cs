// ArmourProbe.cs -- read what the player is actually wearing.
//
// v2.26-v2.31 looked for armour inside the inventory and always answered
// "nothing": armour is NOT in the inventory. It is a separate system:
//   Sons.Wearable.Armour.PlayerArmourSystem
//       IsWearingAnyArmour() / IsWearingGoldenArmour() /
//       get_IsWearingFullTechArmour / GetCurrentArmourIds() /
//       GetArmourPieceById(int) / CalculateRemainingDamageAfterArmourHit(float,bool)
// We walk the player object graph (depth 3, capped) and take the first member
// whose type is the armour system, so a patch moving the property still works.
// Nothing here throws; a probe that cannot find the system must let the caller
// fall back to the old inventory path.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SotfClientProbe
{
    internal static class ArmourProbe
    {
        public const string SysTypeName = "Sons.Wearable.Armour.PlayerArmourSystem";

        public static bool Found;
        public static bool Wearing;
        public static bool Golden;
        public static bool FullTech;
        public static float Points = -1f;
        public static float Rating = -1f;

        // v2.34: measured reduction, straight from the game. The system exposes
        //   float CalculateRemainingDamageAfterArmourHit(float dmg, bool demonic)
        // Feed it a round 100 and the answer IS the reduction (62 back => 38%
        // eaten). That is the game's own arithmetic, authoritative.
        public static float Reduction = -1f;
        public static float ReductionDemonic = -1f;
        public static float Through = -1f;
        public static float ThroughDemonic = -1f;
        public static string CalcWhy = "";

        public static int PieceCount;
        public static int KindCount;
        public static string IdList = "";
        public static string Labels = "";
        public static string Why = "";

        private static object _sys;
        private static object _lpAtFind;
        private static int _fails;
        private static bool _dumpedSys;
        private static bool _dumpedPiece;
        private static string _lastCalcKey;

        private static readonly Dictionary<int, string> _pieceName =
            new Dictionary<int, string>();

        private static readonly string[] Accessors =
        {
            "ArmourSystem", "PlayerArmourSystem", "_armourSystem",
            "armourSystem", "Armour", "_armour"
        };

        private static readonly string[] PointMembers =
        {
            "RemainingArmourpoints", "remainingArmourpoints",
            "_remainingArmourpoints", "RemainingArmourPoints",
            "_remainingArmourPoints", "ArmourPoints", "_armourPoints",
            "_armourRating", "Armour", "_armour"
        };

        private static readonly string[] PieceMembers =
        {
            "_armourPieces", "ArmourPieces", "_armourList", "ArmourList",
            "_armourSlots", "ArmourSlots"
        };

        private static readonly string[] PieceItemMembers =
        {
            "ArmourInstance", "_armourInstance", "ItemInstance", "_itemInstance",
            "Instance", "_instance", "ItemData", "_itemData", "Data", "_data"
        };

        public static void Refresh()
        {
            try
            {
                object lp = LocalPlayer.Instance();
                if (lp == null) { Miss("no localplayer"); return; }

                if (_sys == null || !object.ReferenceEquals(_lpAtFind, lp) || _fails >= 3)
                {
                    _sys = FindSystem(lp);
                    _lpAtFind = lp;
                    _fails = 0;
                }

                if (_sys == null) { Miss("system not reachable"); return; }

                if (!_dumpedSys)
                {
                    _dumpedSys = true;
                    ItemDecode.DumpOnce(_sys, "armour system: first sight");
                    DumpSystem(_sys);
                }

                Read();
            }
            catch (Exception e)
            {
                _fails++;
                Miss("throw " + U.Short(e));
            }
        }

        private static void Miss(string why)
        {
            Found = false;
            Why = why;
            Wearing = false;
            Golden = false;
            FullTech = false;
            Points = -1f;
            Rating = -1f;
            PieceCount = 0;
            KindCount = 0;
            IdList = "";
            Labels = "";
            Reduction = -1f;
            ReductionDemonic = -1f;
            Through = -1f;
            ThroughDemonic = -1f;
            CalcWhy = why;
        }

        private static bool IsSys(object o)
        {
            if (o == null) return false;
            string tn;
            try { tn = o.GetType().FullName; } catch { return false; }
            return !string.IsNullOrEmpty(tn) &&
                   tn.IndexOf("PlayerArmourSystem", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object FindSystem(object lp)
        {
            foreach (string n in Accessors)
            {
                object v = LocalPlayer.MemberOf(lp, n);
                if (IsSys(v)) return v;
            }

            object hit = Search(lp);
            if (hit != null) return hit;

            object pb = LocalPlayer.PlayerBase();
            if (pb != null && !object.ReferenceEquals(pb, lp))
            {
                foreach (string n in Accessors)
                {
                    object v = LocalPlayer.MemberOf(pb, n);
                    if (IsSys(v)) return v;
                }
                hit = Search(pb);
                if (hit != null) return hit;
            }

            object inv = LocalPlayer.Inventory();
            if (inv != null && !object.ReferenceEquals(inv, lp))
            {
                foreach (string n in Accessors)
                {
                    object v = LocalPlayer.MemberOf(inv, n);
                    if (IsSys(v)) return v;
                }
                hit = Search(inv);
                if (hit != null) return hit;
            }

            return null;
        }

        private static object Search(object root)
        {
            if (root == null) return null;

            var level = new List<object>();
            level.Add(root);
            int visited = 0;

            for (int depth = 0; depth < 3; depth++)
            {
                var next = new List<object>();
                foreach (object o in level)
                {
                    if (o == null) continue;
                    if (++visited > 300) return null;

                    Type t;
                    PropertyInfo[] props;
                    try
                    {
                        t = o.GetType();
                        props = t.GetProperties(U.BF);
                    }
                    catch { continue; }

                    foreach (PropertyInfo p in props)
                    {
                        if (p == null || !p.CanRead) continue;
                        if (p.GetIndexParameters().Length != 0) continue;

                        object v;
                        try { v = p.GetValue(o, null); }
                        catch { continue; }
                        if (v == null) continue;

                        if (IsSys(v)) return v;

                        if (next.Count < 40 && WorthWalking(v)) next.Add(v);
                    }
                }
                if (next.Count == 0) return null;
                level = next;
            }
            return null;
        }

        private static bool WorthWalking(object v)
        {
            if (v is string) return false;
            if (v is ValueType) return false;
            Type t = v.GetType();
            if (t.IsPrimitive || t.IsEnum) return false;
            string tn = t.FullName;
            if (string.IsNullOrEmpty(tn)) return false;
            if (tn.StartsWith("System.", StringComparison.Ordinal)) return false;
            if (tn.StartsWith("UnityEngine.", StringComparison.Ordinal)) return false;
            if (tn.StartsWith("Il2CppSystem.", StringComparison.Ordinal)) return false;
            if (tn.IndexOf("Array", StringComparison.Ordinal) >= 0) return false;
            return true;
        }

        private static void Read()
        {
            Found = true;
            Why = "";

            object v; string err;
            float f;

            if (U.TryCall(_sys, "IsWearingAnyArmour", out v, out err) && v is bool)
                Wearing = (bool)v;
            else
                Wearing = false;

            Golden = U.TryCall(_sys, "IsWearingGoldenArmour", out v, out err) && v is bool && (bool)v;
            FullTech = U.TryGet(_sys, "IsWearingFullTechArmour", out v, out err) && v is bool && (bool)v;

            Points = -1f;
            foreach (string n in PointMembers)
            {
                if (U.TryGet(_sys, n, out v, out err) && v != null &&
                    !(v is bool) && U.AsFloat(v, out f) && !float.IsNaN(f))
                { Points = f; break; }
            }

            Rating = -1f;
            if (U.TryGet(_sys, "ArmourRating", out v, out err) && v != null &&
                U.AsFloat(v, out f) && !float.IsNaN(f))
                Rating = f;

            var ids = new List<int>();
            if (U.TryCall(_sys, "GetCurrentArmourIds", out v, out err) && v != null)
            {
                List<object> seq = U.Seq(v, 32);
                if (seq != null)
                {
                    foreach (object o in seq)
                    {
                        int id;
                        if (ItemDecode.TryId(o, out id) && id > 0) ids.Add(id);
                    }
                }
            }

            if (ids.Count > 0)
            {
                PieceCount = ids.Count;
            }
            else
            {
                PieceCount = 0;
                foreach (string n in PieceMembers)
                {
                    if (U.TryGet(_sys, n, out v, out err) && v != null)
                    {
                        List<object> seq = U.Seq(v, 32);
                        if (seq != null && seq.Count > 0) { PieceCount = seq.Count; break; }
                    }
                }
            }

            var order = new List<int>();
            var perId = new Dictionary<int, int>();
            foreach (int id in ids)
            {
                if (!perId.ContainsKey(id)) { perId[id] = 0; order.Add(id); }
                perId[id]++;
            }
            KindCount = order.Count;

            var sbIds = new StringBuilder();
            var sbLab = new StringBuilder();
            foreach (int id in order)
            {
                string nm = ItemDecode.NameFor(id);
                if (string.IsNullOrEmpty(nm)) nm = NameViaPiece(id);

                string idStr = U.Num(id);
                if (!string.IsNullOrEmpty(nm) && nm.StartsWith(idStr + ":", StringComparison.Ordinal))
                    nm = nm.Substring(idStr.Length + 1);

                int c = perId[id];
                string many = c > 1 ? (" x" + U.Num(c)) : "";
                string bare = idStr + many;

                if (sbIds.Length > 0) sbIds.Append(',');
                sbIds.Append(idStr);
                if (c > 1) sbIds.Append('x').Append(U.Num(c));

                if (sbLab.Length > 0) sbLab.Append(" | ");
                sbLab.Append(string.IsNullOrEmpty(nm) ? bare : (idStr + ":" + nm + many));
            }
            IdList = sbIds.ToString();
            Labels = sbLab.ToString();

            string key = U.Bool(Wearing) + "|" + U.Num(PieceCount) + "|" + IdList;
            if (!string.Equals(key, _lastCalcKey, StringComparison.Ordinal))
            {
                _lastCalcKey = key;
                Measure();
            }
        }

        private static void DumpSystem(object sys)
        {
            if (sys == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("=== armour system walk ===\n");
                sb.Append("time : ").Append(U.Now()).Append('\n');
                sb.Append("type : ").Append(U.TypeName(sys)).Append('\n');
                sb.Append("note : P=property  F=field.  Values are truncated.\n\n");

                sb.Append("--- candidate names, and what each answered ---\n");
                foreach (string n in PointMembers) ProbeOne(sb, sys, "pts", n);
                foreach (string n in PieceMembers) ProbeOne(sb, sys, "list", n);
                ProbeOne(sb, sys, "rating", "ArmourRating");
                sb.Append('\n');

                sb.Append("--- every member ---\n");
                Type t = sys.GetType();
                foreach (PropertyInfo p in t.GetProperties(U.BF))
                {
                    if (p == null || !p.CanRead) continue;
                    if (p.GetIndexParameters().Length != 0) continue;
                    object v = null; string s;
                    try
                    {
                        v = p.GetValue(sys, null);
                        s = v == null ? "null" : (U.TypeName(v) + " = " + U.Short2(v));
                    }
                    catch (Exception e) { s = "throw " + U.Short(e); }
                    sb.Append("  P ").Append(p.Name).Append(" : ").Append(s).Append('\n');
                }
                foreach (FieldInfo f in t.GetFields(U.BF))
                {
                    if (f == null) continue;
                    object v = null; string s;
                    try
                    {
                        v = f.GetValue(sys);
                        s = v == null ? "null" : (U.TypeName(v) + " = " + U.Short2(v));
                    }
                    catch (Exception e) { s = "throw " + U.Short(e); }
                    sb.Append("  F ").Append(f.Name).Append(" : ").Append(s).Append('\n');
                }

                sb.Append('\n');
                sb.Append("--- what is actually in the armour collections ---\n");
                DumpCollection(sb, sys, "_armourPieces", "pieces");
                DumpCollection(sb, sys, "_armourSlotData", "slotdata");
                DumpCollection(sb, sys, "_armourSlotOrder", "order");

                Out.WriteReport("armour_probe.txt", sb.ToString());
            }
            catch { }
        }

        private static void DumpCollection(StringBuilder sb, object sys,
                                           string member, string tag)
        {
            object v; string err;
            sb.Append("  [").Append(tag).Append("] ").Append(member).Append(" -> ");

            if (!U.TryGet(sys, member, out v, out err) || v == null)
            {
                sb.Append("absent");
                if (!string.IsNullOrEmpty(err)) sb.Append(" (").Append(err).Append(')');
                sb.Append('\n');
                return;
            }

            int n = U.SeqLen(v);
            List<object> seq = U.Seq(v, 8);
            sb.Append(TShort(U.TypeName(v)))
              .Append("  count=").Append(U.Num(n)).Append('\n');

            if (seq == null || seq.Count == 0)
            {
                sb.Append("      (empty");
                if (seq == null) sb.Append(", and the container refused enumeration");
                sb.Append(")\n");
                return;
            }

            for (int i = 0; i < seq.Count; i++)
            {
                sb.Append("      [").Append(U.Num(i)).Append("] ")
                  .Append(U.TypeName(seq[i])).Append('\n');
                DumpMembers(sb, seq[i], "          ", 16);
            }
        }

        private static void DumpMembers(StringBuilder sb, object o, string pad, int max)
        {
            if (o == null) return;
            int shown = 0;
            Type t;
            try { t = o.GetType(); } catch { return; }

            foreach (PropertyInfo p in t.GetProperties(U.BF))
            {
                if (p == null || !p.CanRead) continue;
                if (p.GetIndexParameters().Length != 0) continue;
                if (p.Name.StartsWith("Native", StringComparison.Ordinal)) continue;
                string tn = p.PropertyType == null ? "" : (p.PropertyType.FullName ?? "");
                if (!Interesting(tn)) continue;

                string s;
                try
                {
                    object v = p.GetValue(o, null);
                    s = v == null ? "null" : U.Short2(v);
                }
                catch (Exception e) { s = "throw " + U.Short(e); }

                sb.Append(pad).Append("P ").Append(p.Name)
                  .Append(" (").Append(TShort(tn)).Append(") = ").Append(s).Append('\n');
                if (++shown >= max) return;
            }

            foreach (FieldInfo f in t.GetFields(U.BF))
            {
                if (f == null) continue;
                if (f.Name.StartsWith("Native", StringComparison.Ordinal)) continue;
                string tn = f.FieldType == null ? "" : (f.FieldType.FullName ?? "");
                if (!Interesting(tn)) continue;

                string s;
                try
                {
                    object v = f.GetValue(o);
                    s = v == null ? "null" : U.Short2(v);
                }
                catch (Exception e) { s = "throw " + U.Short(e); }

                sb.Append(pad).Append("F ").Append(f.Name)
                  .Append(" (").Append(TShort(tn)).Append(") = ").Append(s).Append('\n');
                if (++shown >= max) return;
            }
        }

        private static bool Interesting(string tn)
        {
            if (string.IsNullOrEmpty(tn)) return false;
            if (tn == "System.Int32" || tn == "System.Single" ||
                tn == "System.Boolean" || tn == "System.String" ||
                tn == "System.Int64" || tn == "System.Byte")
                return true;
            if (tn.StartsWith("UnityEngine.", StringComparison.Ordinal)) return false;
            if (tn.StartsWith("Il2CppSystem.", StringComparison.Ordinal)) return false;
            if (tn.StartsWith("System.", StringComparison.Ordinal)) return false;
            return true;
        }

        private static string TShort(string tn)
        {
            if (string.IsNullOrEmpty(tn)) return "?";
            int d = tn.LastIndexOf('.');
            return (d >= 0 && d < tn.Length - 1) ? tn.Substring(d + 1) : tn;
        }

        private static void ProbeOne(StringBuilder sb, object o, string tag, string name)
        {
            sb.Append("  [").Append(tag).Append("] ").Append(name).Append(" -> ");
            object v; string err;
            if (!U.TryGet(o, name, out v, out err))
            {
                sb.Append("absent");
                if (!string.IsNullOrEmpty(err)) sb.Append(" (").Append(err).Append(')');
                sb.Append('\n');
                return;
            }
            if (v == null) { sb.Append("null\n"); return; }
            sb.Append(U.TypeName(v)).Append(" = ").Append(U.Short2(v)).Append('\n');
        }

        private static string NameViaPiece(int id)
        {
            string cached;
            if (_pieceName.TryGetValue(id, out cached)) return cached;

            string result = null;
            try
            {
                object piece = CallInt(_sys, "GetArmourPieceById", id);
                if (piece != null)
                {
                    if (!_dumpedPiece)
                    {
                        _dumpedPiece = true;
                        ItemDecode.DumpOnce(piece, "armour piece: first sight");
                    }

                    foreach (string n in PieceItemMembers)
                    {
                        object v; string err;
                        if (!U.TryGet(piece, n, out v, out err) || v == null) continue;
                        string d = ItemDecode.Describe(v);
                        if (string.IsNullOrEmpty(d)) continue;
                        if (d.IndexOf("ItemInstance", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        result = d;
                        break;
                    }
                    if (string.IsNullOrEmpty(result))
                        result = ItemDecode.NameFor(id);
                }
            }
            catch { result = null; }

            _pieceName[id] = result;
            return result;
        }

        private static void Measure()
        {
            Reduction = -1f;
            ReductionDemonic = -1f;
            Through = -1f;
            ThroughDemonic = -1f;
            CalcWhy = "";

            const float probe = 100f;

            object r = CallFloatBool(_sys, "CalculateRemainingDamageAfterArmourHit",
                                     probe, false);
            float f;
            if (r != null && U.AsFloat(r, out f) && !float.IsNaN(f))
            {
                Through = f;
                Reduction = (probe - f) / probe;
            }
            else
            {
                CalcWhy = "calc no_method/throw";
            }

            object rd = CallFloatBool(_sys, "CalculateRemainingDamageAfterArmourHit",
                                      probe, true);
            if (rd != null && U.AsFloat(rd, out f) && !float.IsNaN(f))
            {
                ThroughDemonic = f;
                ReductionDemonic = (probe - f) / probe;
            }

            if (Reduction >= 0f)
                CalcWhy = "";
        }

        private static object CallFloatBool(object o, string name, float f, bool b)
        {
            if (o == null) return null;
            try
            {
                foreach (MethodInfo m in o.GetType().GetMethods(U.BF))
                {
                    if (m == null ||
                        !string.Equals(m.Name, name, StringComparison.Ordinal))
                        continue;

                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 2) continue;

                    Type t0 = ps[0].ParameterType;
                    Type t1 = ps[1].ParameterType;
                    if (t1 != typeof(bool)) continue;
                    if (!(t0 == typeof(float) || t0 == typeof(double))) continue;

                    object a0 = Convert.ChangeType(f, t0, CultureInfo.InvariantCulture);
                    return m.Invoke(o, new object[] { a0, b });
                }
            }
            catch { }
            return null;
        }

        private static object CallInt(object o, string name, int arg)
        {
            if (o == null) return null;
            try
            {
                foreach (MethodInfo m in o.GetType().GetMethods(U.BF))
                {
                    if (m == null || !string.Equals(m.Name, name, StringComparison.Ordinal))
                        continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    Type pt = ps[0].ParameterType;
                    if (pt == typeof(int) || pt == typeof(uint) ||
                        pt == typeof(long) || pt == typeof(short))
                    {
                        return m.Invoke(o, new object[] {
                            Convert.ChangeType(arg, pt, CultureInfo.InvariantCulture) });
                    }
                }
            }
            catch { }
            return null;
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.Append("  armour system : ").Append(Found ? "found" : "NOT FOUND").Append('\n');
            if (!Found)
            {
                sb.Append("  why           : ").Append(Why).Append('\n');
                sb.Append("  (falling back to the inventory slot walk --\n");
                sb.Append("   that path cannot see worn armour on this patch)\n");
                return sb.ToString();
            }
            sb.Append("  wearing       : ").Append(U.Bool(Wearing)).Append('\n');
            sb.Append("  pieces        : ").Append(U.Num(PieceCount)).Append('\n');
            sb.Append("  ids           : ").Append(IdList).Append('\n');
            sb.Append("  labels        : ").Append(Labels).Append('\n');
            sb.Append("  points        : ").Append(Points >= 0f ? U.Num(Points) : "(no such member)").Append('\n');
            sb.Append("  rating        : ").Append(Rating >= 0f ? U.Num(Rating) : "(no such member)").Append('\n');

            if (Reduction >= 0f)
            {
                sb.Append("  reduction     : ")
                  .Append(U.Num(Reduction * 100f)).Append("%  ")
                  .Append("(100 dmg -> ").Append(U.Num(Through)).Append(')').Append('\n');
                if (ReductionDemonic >= 0f)
                    sb.Append("  reduction(dem): ")
                      .Append(U.Num(ReductionDemonic * 100f)).Append("%  ")
                      .Append("(100 dmg -> ").Append(U.Num(ThroughDemonic)).Append(')').Append('\n');
            }
            else
            {
                sb.Append("  reduction     : (not measurable)");
                if (!string.IsNullOrEmpty(CalcWhy)) sb.Append("  -- ").Append(CalcWhy);
                sb.Append('\n');
            }
            sb.Append("  golden        : ").Append(U.Bool(Golden)).Append('\n');
            sb.Append("  full tech     : ").Append(U.Bool(FullTech)).Append('\n');
            return sb.ToString();
        }
    }
}
