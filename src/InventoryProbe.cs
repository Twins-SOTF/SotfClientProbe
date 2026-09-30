// InventoryProbe.cs -- watch the local player's backpack.
//
// WHY: the ranking rules allow no mods at all, and one thing mods reliably do
// is hand out items. Watching the inventory gives a second, independent signal
// (a backpack that gains 200 arrows in one tick without a matching pickup is
// worth a look). We do NOT hard-code the inventory item type: interop names
// differ between patches, so we walk to LocalPlayer.Inventory and discover the
// object's shape at RUNTIME (enumerate members, keep enumerables, read the
// id/amount pair off each element). Everything is dumped to inventory_probe.txt
// on the first pass.
//
// v2.14: GetAllItems() is the primary path; the return shape is discovered
// (IDictionary / IEnumerable of KVP / plain elements / Il2Cpp dictionary via
// reflection GetEnumerator), amounts are normalised the same way.
//
// THREADING: main thread only (see the v3.23 crash note in ProbePlugin.cs).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SotfClientProbe
{
    internal static class InventoryProbe
    {
        private static readonly string[] ContainerHints =
        {
            "_possessedItems", "_multiplayerPossessedItems",
            "Items", "_items", "ItemList", "_itemList", "AllItems",
            "Entries", "_entries", "Slots", "_slots", "Content", "Contents",
            "InventoryItems", "_inventoryItems", "ItemInstances", "_itemInstances",
            "_equipmentSlots"
        };

        private static readonly string[] IdHints =
        {
            "_itemID", "ItemID", "itemID",
            "ItemId", "_itemId", "Id", "_id", "ItemType", "_itemType",
            "Type", "_type", "ItemDataId", "_itemDataId"
        };

        private static readonly string[] CountHints =
        {
            "Count", "_count", "Amount", "_amount", "Quantity", "_quantity",
            "StackSize", "_stackSize", "TotalCount", "_totalCount"
        };

        private enum Mode { Unknown, GetAll, Container, None }

        private static Mode _mode = Mode.Unknown;
        private static string _containerName;
        private static string _idName;
        private static string _countName;
        private static string _shape = "(not probed yet)";

        private static readonly Dictionary<string, int> _last = new Dictionary<string, int>();

        public static int Snapshots;
        public static int Changes;
        public static int ItemKinds;

        public static string Report;

        private static bool DiscoverShape(object inv, StringBuilder sb)
        {
            if (inv == null) return false;

            foreach (string hint in ContainerHints)
            {
                object val; string err;
                if (!U.TryGet(inv, hint, out val, out err) || val == null) continue;

                if (val is string) continue;

                List<object> seq = U.Seq(val, 256);
                if (seq == null) continue;

                object first = null;
                int n = 0;
                try
                {
                    foreach (object o in seq)
                    {
                        if (o == null) continue;
                        first = o;
                        n++;
                        if (n >= 2) break;
                    }
                }
                catch (Exception e)
                {
                    sb.Append("     container ").Append(hint).Append(" enum threw ").Append(U.Short(e)).Append('\n');
                    continue;
                }

                if (n == 0)
                {
                    _containerName = hint;
                    sb.Append("     container  : ").Append(hint).Append(" (empty)\n");
                    return true;
                }

                _containerName = hint;
                sb.Append("     container  : ").Append(hint)
                  .Append("  element=").Append(U.TypeName(first)).Append('\n');

                _idName = FindMember(first, IdHints, true);
                _countName = FindMember(first, CountHints, true);
                sb.Append("     id member  : ").Append(_idName ?? "(none)").Append('\n');
                sb.Append("     cnt member : ").Append(_countName ?? "(none)").Append('\n');
                return true;
            }

            sb.Append("     no enumerable container found -- dumping candidates\n");
            DumpCandidates(inv, sb);
            return false;
        }

        private static string FindMember(object obj, string[] hints, bool numeric)
        {
            if (obj == null) return null;
            foreach (string h in hints)
            {
                object v; string err;
                if (!U.TryGet(obj, h, out v, out err) || v == null) continue;
                if (numeric)
                {
                    float f;
                    if (U.AsFloat(v, out f)) return h;
                }
                else
                {
                    return h;
                }
            }
            return null;
        }

        private static void DumpCandidates(object inv, StringBuilder sb)
        {
            try
            {
                Type t = inv.GetType();
                foreach (PropertyInfo p in t.GetProperties(U.BF))
                {
                    if (p == null || !p.CanRead || p.GetIndexParameters().Length != 0) continue;
                    object v = null;
                    try { v = p.GetValue(inv, null); } catch { continue; }
                    string kind = v == null ? "null" : U.TypeName(v);
                    bool en = (v is IEnumerable) && !(v is string);
                    sb.Append("       P ").Append(p.Name).Append(" : ").Append(kind)
                      .Append(en ? "  <-- enumerable" : "").Append('\n');
                }
                foreach (FieldInfo f in t.GetFields(U.BF))
                {
                    if (f == null) continue;
                    object v = null;
                    try { v = f.GetValue(inv); } catch { continue; }
                    string kind = v == null ? "null" : U.TypeName(v);
                    bool en = (v is IEnumerable) && !(v is string);
                    sb.Append("       F ").Append(f.Name).Append(" : ").Append(kind)
                      .Append(en ? "  <-- enumerable" : "").Append('\n');
                }
            }
            catch (Exception e) { sb.Append("       dump threw ").Append(U.Short(e)).Append('\n'); }
        }

        private static int AmountOf(object v)
        {
            if (v == null) return 1;
            float f;
            if (U.AsFloat(v, out f)) return f < 0f ? 0 : (int)f;

            string cn = FindMember(v, CountHints, true);
            if (!string.IsNullOrEmpty(cn))
            {
                object cv; string err;
                if (U.TryGet(v, cn, out cv, out err) && U.AsFloat(cv, out f))
                    return f < 0f ? 0 : (int)f;
            }
            return 1;
        }

        private static void Add(Dictionary<string, int> into, string id, int amount)
        {
            if (id == null) id = "?";
            int prev;
            if (into.TryGetValue(id, out prev)) into[id] = prev + amount;
            else into[id] = amount;
        }

        private static bool SplitKvp(object el, out string id, out int amount)
        {
            id = null; amount = 1;
            object k, v; string err;
            if (!U.TryGet(el, "Key", out k, out err) || k == null) return false;
            if (!U.TryGet(el, "Value", out v, out err)) return false;
            id = k.ToString();
            amount = AmountOf(v);
            return true;
        }

        private static bool Harvest(object raw, Dictionary<string, int> result, StringBuilder sb)
        {
            if (raw == null) return false;

            IDictionary dict = raw as IDictionary;
            if (dict != null)
            {
                sb.Append("     shape      : IDictionary of ").Append(dict.Count).Append('\n');
                int shown = 0;
                try
                {
                    foreach (DictionaryEntry de in dict)
                    {
                        string id = de.Key == null ? "?" : de.Key.ToString();
                        int amt = AmountOf(de.Value);
                        Add(result, id, amt);
                        if (shown < 8)
                        {
                            sb.Append("       e.g. ").Append(id).Append(" => ")
                              .Append(U.TypeName(de.Value)).Append(" = ").Append(amt).Append('\n');
                            shown++;
                        }
                    }
                }
                catch (Exception e) { sb.Append("       dict enum threw ").Append(U.Short(e)).Append('\n'); }
                _shape = "IDictionary(" + dict.Count + ")";
                return result.Count > 0;
            }

            IEnumerable seq = raw as IEnumerable;
            if (seq != null && !(raw is string))
            {
                int n = 0, shown = 0;
                try
                {
                    foreach (object el in seq)
                    {
                        if (el == null) continue;
                        n++;

                        string id; int amt;
                        if (SplitKvp(el, out id, out amt))
                        {
                            Add(result, id, amt);
                            if (shown < 8)
                            {
                                sb.Append("       kvp ").Append(id).Append(" = ").Append(amt).Append('\n');
                                shown++;
                            }
                            continue;
                        }

                        string idv = "?";
                        int decoded;
                        if (ItemDecode.TryId(el, out decoded))
                        {
                            idv = ItemDecode.Describe(el);
                        }
                        else
                        {
                            string im = FindMember(el, IdHints, false);
                            object iv; string err;
                            if (!string.IsNullOrEmpty(im) &&
                                U.TryGet(el, im, out iv, out err) && iv != null)
                                idv = iv.ToString();
                            else
                                ItemDecode.DumpOnce(el, "inventory element");
                        }

                        string cm = FindMember(el, CountHints, true);
                        int amtv = 1;
                        object cv;
                        string cerr;
                        int ic = ItemDecode.Count(el);
                        if (ic > 0) amtv = ic;
                        else if (!string.IsNullOrEmpty(cm) &&
                                 U.TryGet(el, cm, out cv, out cerr) && cv != null)
                            amtv = AmountOf(cv);

                        Add(result, idv, amtv);
                        if (shown < 8)
                        {
                            sb.Append("       el ").Append(U.TypeName(el))
                              .Append(" id=").Append(idv).Append(" n=").Append(amtv).Append('\n');
                            shown++;
                        }
                    }
                }
                catch (Exception e) { sb.Append("       seq enum threw ").Append(U.Short(e)).Append('\n'); }

                _shape = "IEnumerable(" + n + ")";
                sb.Append("     shape      : IEnumerable of ").Append(n).Append('\n');
                return result.Count > 0;
            }

            if (HarvestIl2CppDict(raw, result, sb)) return true;

            sb.Append("     shape      : ").Append(U.TypeName(raw)).Append(" (not enumerable)\n");
            return false;
        }

        private static bool HarvestIl2CppDict(object raw, Dictionary<string, int> result, StringBuilder sb)
        {
            if (raw == null) return false;
            try
            {
                MethodInfo ge = null;
                foreach (MethodInfo m in raw.GetType().GetMethods(U.BF))
                {
                    if (m == null || m.Name != "GetEnumerator") continue;
                    if (m.GetParameters().Length != 0) continue;
                    ge = m; break;
                }
                if (ge == null) return false;

                object e = ge.Invoke(raw, null);
                if (e == null) return false;

                Type et = e.GetType();
                MethodInfo move = et.GetMethod("MoveNext", U.BF);
                PropertyInfo cur = et.GetProperty("Current", U.BF);
                if (move == null || cur == null) return false;

                int n = 0, shown = 0, guard = 0;
                while (guard++ < 4096)
                {
                    object alive;
                    try { alive = move.Invoke(e, null); } catch { break; }
                    if (alive is bool && !(bool)alive) break;

                    object el;
                    try { el = cur.GetValue(e, null); } catch { break; }
                    if (el == null) continue;
                    n++;

                    string id; int amt;
                    if (SplitKvp(el, out id, out amt))
                    {
                        Add(result, id, amt);
                        if (shown < 8) { sb.Append("       kvp ").Append(id).Append(" = ").Append(amt).Append('\n'); shown++; }
                        continue;
                    }

                    object k, v; string e2;
                    if (U.TryGet(el, "Key", out k, out e2) && k != null)
                    {
                        U.TryGet(el, "Value", out v, out e2);
                        string sid = ItemDecode.Describe(k);
                        if (string.IsNullOrEmpty(sid)) { try { sid = k.ToString(); } catch { sid = "?"; } }
                        int a2 = AmountOf(v);
                        Add(result, sid, a2);
                        if (shown < 8) { sb.Append("       key ").Append(sid).Append(" = ").Append(a2).Append('\n'); shown++; }
                        continue;
                    }

                    int decoded;
                    string idv = "?";
                    if (ItemDecode.TryId(el, out decoded)) idv = ItemDecode.Describe(el);
                    else ItemDecode.DumpOnce(el, "il2cpp dict element");
                    int a3 = AmountOf(el);
                    Add(result, idv, a3);
                    if (shown < 8) { sb.Append("       el  ").Append(idv).Append(" = ").Append(a3).Append('\n'); shown++; }
                }

                if (result.Count == 0) return false;
                _shape = "Il2CppDict(" + n + ")";
                sb.Append("     shape      : IL2CPP dictionary, ").Append(n)
                  .Append(" pairs (reached via GetEnumerator)\n");
                return true;
            }
            catch (Exception ex)
            {
                sb.Append("       il2cpp dict threw ").Append(U.Short(ex)).Append('\n');
                return false;
            }
        }

        private static Mode Probe(object inv)
        {
            var sb = new StringBuilder();
            sb.Append("=== inventory shape discovery ===\n");
            sb.Append("time      : ").Append(U.Now()).Append('\n');
            sb.Append("inventory : ").Append(U.TypeName(inv)).Append('\n');

            object raw; string err;
            if (U.TryCall(inv, "GetAllItems", out raw, out err))
            {
                sb.Append("     GetAllItems() -> ").Append(raw == null ? "null" : U.TypeName(raw)).Append('\n');
                var probe = new Dictionary<string, int>();
                if (Harvest(raw, probe, sb))
                {
                    sb.Append("     entries    : ").Append(U.Num(probe.Count)).Append(" kinds\n");
                    foreach (var kv in probe)
                        sb.Append("       item ").Append(kv.Key).Append(" x").Append(kv.Value).Append('\n');
                    Report = sb.ToString();
                    return Mode.GetAll;
                }
                sb.Append("     GetAllItems() yielded no entries\n");
            }
            else
            {
                sb.Append("     GetAllItems() -> FAIL ").Append(err ?? "?").Append('\n');
            }

            if (DiscoverShape(inv, sb) && !string.IsNullOrEmpty(_containerName))
            {
                Report = sb.ToString();
                return Mode.Container;
            }

            sb.Append("     RESULT     : no readable inventory path\n");
            Report = sb.ToString();
            return Mode.None;
        }

        private static Dictionary<string, int> ReadViaGetAll(object inv)
        {
            object raw; string err;
            if (!U.TryCall(inv, "GetAllItems", out raw, out err) || raw == null) return null;
            var result = new Dictionary<string, int>();
            Harvest(raw, result, new StringBuilder());
            return result;
        }

        private static Dictionary<string, int> ReadViaContainer(object inv)
        {
            if (string.IsNullOrEmpty(_containerName)) return null;
            object val; string err;
            if (!U.TryGet(inv, _containerName, out val, out err) || val == null) return null;
            List<object> seq = U.Seq(val, 512);
            if (seq == null || seq.Count == 0) return null;

            var result = new Dictionary<string, int>();
            foreach (object el in seq)
            {
                if (el == null) continue;

                string id = "?";
                if (!string.IsNullOrEmpty(_idName))
                {
                    object iv;
                    if (U.TryGet(el, _idName, out iv, out err) && iv != null)
                        id = iv.ToString();
                }

                int amount = 1;
                if (!string.IsNullOrEmpty(_countName))
                {
                    object cv;
                    float f;
                    if (U.TryGet(el, _countName, out cv, out err) && U.AsFloat(cv, out f))
                        amount = (int)f;
                }

                Add(result, id, amount);
            }
            return result;
        }

        public static Dictionary<string, int> Read()
        {
            try
            {
                object inv = LocalPlayer.Inventory();
                if (inv == null) return null;

                if (_mode == Mode.Unknown) _mode = Probe(inv);

                Dictionary<string, int> now;
                if (_mode == Mode.GetAll) now = ReadViaGetAll(inv);
                else if (_mode == Mode.Container) now = ReadViaContainer(inv);
                else return null;

                return now;
            }
            catch
            {
                return null;
            }
        }

        public static string Sample()
        {
            try
            {
                Dictionary<string, int> now = Read();
                if (now == null) return null;
                Snapshots++;
                ItemKinds = now.Count;

                bool changed = now.Count != _last.Count;
                if (!changed)
                {
                    foreach (var kv in now)
                    {
                        int prev;
                        if (!_last.TryGetValue(kv.Key, out prev) || prev != kv.Value)
                        { changed = true; break; }
                    }
                }
                if (!changed) return null;

                Changes++;

                var sb = new StringBuilder(256);
                sb.Append("\"inv_count\":").Append(U.Num(now.Count));

                sb.Append(",\"inv_changed\":{");
                bool first = true;

                foreach (var kv in now)
                {
                    int prev;
                    bool has = _last.TryGetValue(kv.Key, out prev);
                    if (has && prev == kv.Value) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(U.Esc(kv.Key)).Append("\":")
                      .Append(kv.Value.ToString(CultureInfo.InvariantCulture));
                }
                foreach (var kv in _last)
                {
                    if (now.ContainsKey(kv.Key)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(U.Esc(kv.Key)).Append("\":0");
                }
                sb.Append('}');

                _last.Clear();
                foreach (var kv in now) _last[kv.Key] = kv.Value;
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        public static void Reset()
        {
            _last.Clear();
            _mode = Mode.Unknown;
            _containerName = null;
            _idName = null;
            _countName = null;
            _shape = "(not probed yet)";
            Snapshots = 0;
            Changes = 0;
            ItemKinds = 0;
            Report = null;
        }

        public static string Status()
        {
            return "inv: " + _mode.ToString() + " shape=" + _shape +
                   " kinds=" + U.Num(ItemKinds) + " changes=" + U.Num(Changes);
        }
    }
}
