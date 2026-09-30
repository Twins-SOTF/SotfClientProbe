// ItemDecode.cs -- turn a game item object into an id and a name.
//
// For two versions the weapon field read "Sons.Inventory.ItemInstance" and the
// backpack read {"?": 1} -- because every id hint spelled `_itemId` while the
// game spells it `_itemID` (ItemInstance) / `ItemId` (ItemInstanceModule).
// Reflection is case sensitive, so this file tries EVERY casing and never bets
// on one. It also maps numeric ids to names (table recovered from a real save
// file, plus runtime learning: the game's own enum table and object autopsies)
// and dumps the first undecodable item to item_probe.txt.
// NOTHING HERE THROWS: a probe that cannot identify an item must still report
// that the item exists.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SotfClientProbe
{
    internal static class ItemDecode
    {
        private static readonly string[] IdMembers =
        {
            "_itemID", "ItemID", "itemID", "_ItemID",
            "_itemId", "ItemId", "itemId", "_ItemId",
            "ItemDataId", "_itemDataId",
            "Id", "_id", "ID", "_ID"
        };

        private static readonly string[] CountMembers =
        {
            "TotalCount", "_totalCount", "Count", "_count",
            "Amount", "_amount", "Quantity", "_quantity",
            "StackSize", "_stackSize"
        };

        public static bool TryId(object item, out int id)
        {
            id = 0;
            if (item == null) return false;

            float f;
            if (U.AsFloat(item, out f))
            {
                if (float.IsNaN(f) || float.IsInfinity(f)) return false;
                id = (int)f;
                return true;
            }

            foreach (string m in IdMembers)
            {
                object v; string err;
                if (!U.TryGet(item, m, out v, out err) || v == null) continue;
                if (v is bool) continue;

                if (U.AsFloat(v, out f) && !float.IsNaN(f) && !float.IsInfinity(f))
                {
                    id = (int)f;
                    return true;
                }

                string s = null;
                try { s = v.ToString(); } catch { s = null; }
                if (!string.IsNullOrEmpty(s))
                {
                    int n;
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    {
                        id = n;
                        return true;
                    }
                }
            }
            return false;
        }

        public static int Count(object item)
        {
            if (item == null) return -1;
            foreach (string m in CountMembers)
            {
                object v; string err;
                if (!U.TryGet(item, m, out v, out err) || v == null) continue;
                if (v is bool) continue;
                float f;
                if (U.AsFloat(v, out f) && !float.IsNaN(f)) return (int)f;
            }
            return -1;
        }

        public static string Describe(object item)
        {
            if (item == null) return "";

            int id;
            if (!TryId(item, out id)) return U.TypeName(item);

            string name;
            if (Names.TryGetValue(id, out name) && !string.IsNullOrEmpty(name))
                return id.ToString(CultureInfo.InvariantCulture) + ":" + name;

            TryLearnEnumTable();
            if (Names.TryGetValue(id, out name) && !string.IsNullOrEmpty(name))
                return id.ToString(CultureInfo.InvariantCulture) + ":" + name;

            if (_unknownLogged.Add(id)) { Note(id); AutopsyId(item, id); }
            return id.ToString(CultureInfo.InvariantCulture);
        }

        public static string NameFor(int id)
        {
            TryLearnEnumTable();
            string nm;
            if (Names.TryGetValue(id, out nm) && !string.IsNullOrEmpty(nm)) return nm;
            return null;
        }

        private static bool _enumTried;

        public static void TryLearnEnumTable()
        {
            if (_enumTried) return;
            _enumTried = true;
            try
            {
                foreach (string tn in new string[] {
                    "Sons.Inventory.ItemInstanceManager+Items",
                    "Sons.Inventory.ItemInstanceManager+ItemsEnum",
                    "Sons.Items.Core.ItemId",
                    "Sons.Items.Core.ItemIds" })
                {
                    Type t = U.TypeNamed(tn);
                    if (t == null || !t.IsEnum) continue;

                    Array vals = Enum.GetValues(t);
                    string[] names = Enum.GetNames(t);
                    int added = 0;

                    for (int i = 0; i < vals.Length && i < names.Length; i++)
                    {
                        int id;
                        try { id = Convert.ToInt32(vals.GetValue(i), CultureInfo.InvariantCulture); }
                        catch { continue; }
                        string nm = names[i];
                        if (id <= 0 || string.IsNullOrEmpty(nm)) continue;
                        if (!PlausibleName(nm)) continue;
                        if (Names.ContainsKey(id)) continue;
                        Names[id] = nm;
                        added++;
                    }

                    if (added > 0)
                    {
                        try
                        {
                            if (Out.Dir != null)
                                Out.Append("item_probe.txt", U.Now() +
                                    "  learned " + U.Num(added) + " names from " + tn);
                        }
                        catch { }
                    }
                    break;
                }
            }
            catch { }
        }

        public static void Learn(int id, string name)
        {
            if (id <= 0 || string.IsNullOrEmpty(name)) return;
            if (!PlausibleName(name)) return;

            string had;
            if (Names.TryGetValue(id, out had) && !string.IsNullOrEmpty(had)) return;

            Names[id] = name;

            try
            {
                if (Out.Dir == null) return;
                Out.Append("item_probe.txt",
                    U.Now() + "  LEARNED " +
                    id.ToString(CultureInfo.InvariantCulture) + " = " + name);
            }
            catch { }
        }

        private static bool PlausibleName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length > 48) return false;
            if (s.IndexOf(' ') >= 0) return false;
            if (s.IndexOf('.') >= 0) return false;
            if (s.IndexOf('/') >= 0) return false;
            if (s.IndexOf(':') >= 0) return false;
            return true;
        }

        public static string IdOf(object item)
        {
            int id;
            if (TryId(item, out id)) return id.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        private static readonly HashSet<int> _unknownLogged = new HashSet<int>();

        private static void Note(int id)
        {
            try
            {
                if (Out.Dir == null) return;
                Out.Append("item_probe.txt",
                    U.Now() + "  unknown item id " +
                    id.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static void AutopsyId(object item, int id)
        {
            if (item == null) return;
            try
            {
                if (Out.Dir == null) return;
                var sb = new StringBuilder();
                sb.Append("--- autopsy id ").Append(id.ToString(CultureInfo.InvariantCulture))
                  .Append(" on ").Append(U.TypeName(item)).Append(" ---\n");
                Type t = item.GetType();

                int shown = 0;
                foreach (PropertyInfo p in t.GetProperties(U.BF))
                {
                    if (p == null || !p.CanRead) continue;
                    if (p.GetIndexParameters().Length != 0) continue;
                    object v;
                    try { v = p.GetValue(item, null); }
                    catch { continue; }
                    string s = v as string;
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.IndexOf('.') >= 0 && s.IndexOf(' ') < 0) continue;
                    sb.Append("    P ").Append(p.Name).Append(" : ").Append(s).Append('\n');
                    if (++shown >= 24) break;
                }

                foreach (string nested in new string[] {
                    "_itemData", "ItemData", "_data", "Data", "_definition", "Definition",
                    "ItemInfo", "_itemInfo", "_itemDefinition" })
                {
                    object nd; string err;
                    if (!U.TryGet(item, nested, out nd, out err) || nd == null) continue;
                    sb.Append("    via ").Append(nested).Append(" -> ").Append(U.TypeName(nd)).Append('\n');

                    string picked = NameFrom(nd);
                    if (!string.IsNullOrEmpty(picked)) Learn(id, picked);

                    int k = 0;
                    foreach (PropertyInfo p in nd.GetType().GetProperties(U.BF))
                    {
                        if (p == null || !p.CanRead) continue;
                        if (p.GetIndexParameters().Length != 0) continue;
                        object v;
                        try { v = p.GetValue(nd, null); }
                        catch { continue; }
                        string s = v as string;
                        if (string.IsNullOrEmpty(s)) continue;
                        sb.Append("       ").Append(p.Name).Append(" : ").Append(s).Append('\n');
                        if (++k >= 16) break;
                    }
                }

                if (shown == 0) sb.Append("    (no string members -- name is not on this object)\n");
                Out.Append("item_probe.txt", sb.ToString());
            }
            catch { }
        }

        private static readonly string[] NameMembers =
        {
            "_name", "Name", "name", "ItemName", "_itemName",
            "DisplayName", "_displayName"
        };

        private static string NameFrom(object nd)
        {
            if (nd == null) return null;
            foreach (string nm in NameMembers)
            {
                object v; string err;
                if (!U.TryGet(nd, nm, out v, out err) || v == null) continue;
                string s = v as string;
                if (string.IsNullOrEmpty(s)) continue;
                if (!PlausibleName(s)) continue;
                return s;
            }
            return null;
        }

        private static readonly HashSet<string> _dumped = new HashSet<string>();

        public static void DumpOnce(object o, string where)
        {
            if (o == null) return;
            string tn;
            try { tn = o.GetType().FullName; } catch { return; }
            if (string.IsNullOrEmpty(tn)) return;
            if (!_dumped.Add(tn)) return;

            try
            {
                var sb = new StringBuilder();
                sb.Append("=== item shape dump: ").Append(tn).Append(" ===\n");
                sb.Append("time : ").Append(U.Now()).Append('\n');
                sb.Append("seen : ").Append(where).Append('\n');
                sb.Append("tip  : look for the member holding the item id --\n");
                sb.Append("       ItemInstance uses _itemID, ItemInstanceModule uses ItemId.\n\n");

                Type t = o.GetType();
                foreach (PropertyInfo p in t.GetProperties(U.BF))
                {
                    if (p == null || !p.CanRead) continue;
                    if (p.GetIndexParameters().Length != 0) continue;
                    string kind; object v = null;
                    try { v = p.GetValue(o, null); kind = v == null ? "null" : U.TypeName(v); }
                    catch (Exception e) { kind = "throw " + U.Short(e); }
                    sb.Append("  P ").Append(p.Name).Append(" : ").Append(kind);
                    if (v != null) sb.Append("  = ").Append(U.Short2(v));
                    sb.Append('\n');
                }
                foreach (FieldInfo f in t.GetFields(U.BF))
                {
                    if (f == null) continue;
                    string kind; object v = null;
                    try { v = f.GetValue(o); kind = v == null ? "null" : U.TypeName(v); }
                    catch (Exception e) { kind = "throw " + U.Short(e); }
                    sb.Append("  F ").Append(f.Name).Append(" : ").Append(kind);
                    if (v != null) sb.Append("  = ").Append(U.Short2(v));
                    sb.Append('\n');
                }

                Out.Append("item_probe.txt", sb.ToString());
            }
            catch { }
        }

        private static readonly Dictionary<int, string> Names =
            new Dictionary<int, string>
            {
            { 340, "Guitar" },
            { 341, "Binoculars" },
            { 346, "Shotgun_Rail" },
            { 353, "Stun_Gun" },
            { 354, "Night_Vision_Goggles" },
            { 355, "Pistol" },
            { 356, "Modern_Axe" },
            { 358, "Shotgun" },
            { 359, "Machete" },
            { 360, "Compound_Bow" },
            { 365, "Crossbow" },
            { 367, "Katana" },
            { 374, "Silencer" },
            { 375, "Laser_Sight" },
            { 376, "Pistol_Rail" },
            { 386, "Revolver" },
            { 390, "Printer_Resin" },
            { 394, "Chainsaw" },
            { 396, "Stun_Baton" },
            { 423, "Books#Pennant_Line_Book" },
            { 424, "Books#Deep_Sleep_Book" },
            { 431, "Firefighter_Axe" },
            { 432, "Can_Opener" },
            { 435, "Gold_Mask" },
            { 444, "Rebreather" },
            { 459, "Slingshot" },
            { 468, "Cross" },
            { 471, "Flashlight" },
            { 485, "Shovel" },
            { 487, "Pajamas" },
            { 491, "Blazer" },
            { 492, "Tuxedo" },
            { 493, "Leather_Jacket" },
            { 499, "Wetsuit" },
            { 500, "Winter_Jacket" },
            { 521, "More_Lighting_Email" },
            { 522, "Rope_Gun" },
            { 525, "Putter" },
            { 529, "GPS_Locator" },
            { 534, "Golf_Balls_Emails" },
            { 535, "Golf_Balls_Emails" },
            { 536, "Golf_Balls_Emails" },
            { 537, "Golf_Balls_Emails" },
            { 538, "Painting_Email" },
            { 542, "Artifact_Emails" },
            { 543, "Artifact_Emails" },
            { 544, "Artifact_Emails" },
            { 545, "Photo_Reference_Email" },
            { 546, "Barbara_Email" },
            { 555, "Track_Suit" },
            { 558, "Camouflage_Suit" },
            { 566, "Maintenance_Keycard" },
            { 567, "Guest_Keycard" },
            { 568, "VIP_Keycard" },
            { 572, "Golden_Armor" },
            { 575, "Books#Parallel_Universes_Book" },
            { 631, "Books#The_Realm_Beyond" },
            };
    }
}
