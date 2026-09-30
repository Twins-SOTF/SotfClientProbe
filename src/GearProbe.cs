// GearProbe.cs -- what the player is WEARING and HOLDING.
//
// ARMOUR -> feeds the ranking gate (boolean + breakdown). HELD WEAPON ->
// feeds the block experiment. Discipline: no hard-coded item type; item ids
// are numbers whose meaning moves between patches, so we report ids/names as
// OBSERVATIONS and only LABEL with the durability reference table -- never
// decide presence from it.
//
// v2.32 PATH A: the real armour system Sons.Wearable.Armour.PlayerArmourSystem
// (see ArmourProbe.cs) -- armour is NOT in the inventory on this patch.
// v2.31 PATH 0: _equipmentSlots array walk. v2.26 PATH 1: named slots.
// PATH 2: name-hint sweep, fallback only. The ranking gate uses the game's
// own IsWearingAnyArmour() verdict when available.
//
// THREADING: main thread only.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SotfClientProbe
{
    internal static class GearProbe
    {
        public static bool HasArmor;
        public static int ArmorPieces;
        public static float ArmorPoints = -1f;
        public static bool AncientArmor;
        public static string AncientName = "";
        public static string Weapon = "";
        public static string WeaponId = "";
        public static int Snapshots;
        public static string Report;
        public static string ArmorRoute = "";
        public static string ArmorDetail = "";
        public static int FailTicks;

        private static readonly string[] ArmorSlots =
        {
            "EquippedChestItem", "_equippedChestItem",
            "EquippedFeetItem", "_equippedFeetItem",
            "EquippedEyesItem", "_equippedEyesItem"
        };

        private static readonly string[] ArmorHints =
        {
            "Armor", "Armour", "_armor", "_armour", "Armors", "Armours",
            "ArmorSlots", "_armorSlots", "ArmorPieces", "_armorPieces",
            "Equipment", "_equipment", "EquipmentSlots", "_equipmentSlots",
            "WornItems", "_wornItems", "Outfit", "_outfit", "BodyArmor",
            "_bodyArmor", "Wearable", "_wearable", "WornArmor", "_wornArmor"
        };

        private static readonly string[] HeldHints =
        {
            "HeldItem", "_heldItem", "CurrentItem", "_currentItem",
            "EquippedItem", "_equippedItem", "ActiveItem", "_activeItem",
            "RightHandItem", "_rightHandItem", "InHandItem", "_inHandItem",
            "HeldItemInstance", "_heldItemInstance", "CurrentWeapon",
            "_currentWeapon", "EquippedWeapon", "_equippedWeapon"
        };

        private static readonly string[] DurHints =
        {
            "Armor", "Armour", "Durability", "_durability", "Hp", "_hp",
            "Health", "_health", "Points", "_points", "Value", "_value",
            "Protection", "_protection", "Remaining", "_remaining",
            "ArmorValue", "_armorValue", "Condition", "_condition"
        };

        private static readonly string[] NameHints =
        {
            "Name", "_name", "ItemName", "_itemName", "DisplayName",
            "_displayName", "Id", "_id", "ItemId", "_itemId", "Type", "_type"
        };

        /// Durability reference from measured play; labels observations only.
        private static readonly string[] KnownPlates =
        {
            "25:leaf/grass", "40:hide/deer", "65:bone",
            "80:creepy", "100:tech", "130:solafite"
        };

        public static string Sample()
        {
            int pieces0 = ArmorPieces;
            bool armor0 = HasArmor;
            bool ancient0 = AncientArmor;
            string weapon0 = Weapon;
            float points0 = ArmorPoints;

            Refresh();

            bool changed =
                pieces0 != ArmorPieces ||
                armor0 != HasArmor ||
                ancient0 != AncientArmor ||
                points0 != ArmorPoints ||
                !string.Equals(weapon0, Weapon, StringComparison.Ordinal);

            if (!changed) return null;

            var sb = new StringBuilder(96);
            sb.Append("\"armor\":").Append(U.Bool(HasArmor));
            sb.Append(",\"armor_pc\":").Append(U.Num(ArmorPieces));
            if (ArmorPoints >= 0f) sb.Append(",\"armor_pts\":").Append(U.Num(ArmorPoints));
            if (AncientArmor) sb.Append(",\"ancient\":true");
            sb.Append(",\"weapon\":\"").Append(U.Esc(Weapon)).Append('"');
            return sb.ToString();
        }

        private static string[] _slotNames;

        private static string SlotName(int i)
        {
            if (_slotNames == null)
            {
                string[] names = null;
                try
                {
                    Type et = U.TypeNamed("Sons.Items.Core.EquipmentSlot");
                    if (et != null && et.IsEnum) names = Enum.GetNames(et);
                }
                catch { }
                _slotNames = names ?? new string[0];
            }
            if (i >= 0 && i < _slotNames.Length && !string.IsNullOrEmpty(_slotNames[i]))
                return _slotNames[i];
            return "slot#" + U.Num(i);
        }

        private static bool IsHandSlot(string slotName)
        {
            if (string.IsNullOrEmpty(slotName)) return false;
            return slotName.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void MarkAncient(string label, ref bool ancient,
                                        ref string ancientName)
        {
            if (string.IsNullOrEmpty(label)) return;
            if (label.IndexOf("ancient", StringComparison.OrdinalIgnoreCase) >= 0 ||
                label.IndexOf("golden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                label.IndexOf("solafite", StringComparison.OrdinalIgnoreCase) >= 0)
            { ancient = true; ancientName = label; }
        }

        private static string HeldLabelOf(object inv)
        {
            if (inv == null) return "";
            foreach (string hint in HeldHints)
            {
                object val; string err;
                if (!U.TryGet(inv, hint, out val, out err) || val == null) continue;
                string s = ItemDecode.Describe(val);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return "";
        }

        private static int ReadEquipmentSlots(object inv, string held,
                                              List<string> seen, ref float points,
                                              ref bool anyPoints, ref bool ancient,
                                              ref string ancientName)
        {
            int got = 0;
            bool walked = false;

            foreach (string name in new string[] {
                "_equipmentSlots", "EquipmentSlots", "_equipmentSlotsNext" })
            {
                object arr; string err;
                if (!U.TryGet(inv, name, out arr, out err) || arr == null) continue;

                List<object> items = U.Seq(arr, 64);
                if (items == null) continue;
                walked = true;

                for (int i = 0; i < items.Count; i++)
                {
                    object it = items[i];
                    if (it == null) continue;

                    string label = ItemDecode.Describe(it);
                    if (string.IsNullOrEmpty(label)) continue;

                    string slot = SlotName(i);
                    float d = DurabilityOf(it);
                    string shown = slot + " = " + label +
                                   (d > 0f ? " (" + U.Num(d) + ")" : "");

                    bool hand = IsHandSlot(slot);
                    if (!hand && !string.IsNullOrEmpty(held) && label == held)
                        hand = true;

                    if (hand) { seen.Add(shown + " [hand]"); continue; }

                    seen.Add(shown);
                    if (d > 0f) { points += d; anyPoints = true; }
                    MarkAncient(label, ref ancient, ref ancientName);
                    got++;
                }
                if (walked) break;
            }

            return walked ? got : -1;
        }

        private static void Refresh()
        {
            try
            {
                object lp = LocalPlayer.Instance();
                if (lp == null) { FailReport("LocalPlayer instance is null"); return; }

                ArmourProbe.Refresh();

                object inv = LocalPlayer.Inventory();
                if (inv == null)
                {
                    FailReport("LocalPlayer.Inventory() returned null (main menu)");
                    return;
                }

                Snapshots++;

                int pieces = 0;
                float points = 0f;
                bool anyPoints = false;
                bool ancient = false;
                string ancientName = "";
                var seen = new List<string>();

                string held = HeldLabelOf(inv);

                // v2.32 PATH A -- the real armour system. Armour is NOT in the
                // inventory; it lives in Sons.Wearable.Armour.PlayerArmourSystem
                // (see ArmourProbe.cs). Its verdict is authoritative.
                bool viaSystem = ArmourProbe.Found;

                // v2.31 PATH 0 -- the real equipment-slot array.
                int slotPieces = -1;
                if (!viaSystem)
                {
                    slotPieces = ReadEquipmentSlots(inv, held, seen, ref points,
                                                    ref anyPoints, ref ancient,
                                                    ref ancientName);
                    pieces += slotPieces;
                }

                // v2.26 PATH 1 -- the named equipment slots.
                if (!viaSystem) foreach (string slot in ArmorSlots)
                {
                    object val; string err;
                    if (!U.TryGet(inv, slot, out val, out err) || val == null) continue;
                    if (val is bool) continue;

                    string label = ItemDecode.Describe(val);
                    if (label == null || label.Length == 0) continue;

                    pieces++;
                    float d = DurabilityOf(val);
                    if (d > 0f) { points += d; anyPoints = true; }
                    seen.Add(slot + " = " + label +
                             (d > 0f ? " (" + U.Num(d) + ")" : ""));

                    MarkAncient(label, ref ancient, ref ancientName);
                }

                if (viaSystem)
                {
                    ArmorRoute = "armoursys";
                }
                else if (pieces > 0)
                {
                    ArmorRoute = slotPieces > 0 ? "slots" : "named";
                }
                else if (slotPieces >= 0)
                {
                    ArmorRoute = "slots(empty)";
                }
                else
                {
                    ArmorRoute = "hints";
                    ItemDecode.DumpOnce(inv, "gear armor: slots empty, inventory shape");
                    SweepHints(inv, seen, ref pieces, ref points,
                               ref anyPoints, ref ancient, ref ancientName);
                }

                if (viaSystem)
                {
                    // The game's own gate decides -- _armourPieces can hold
                    // entries while IsWearingAnyArmour() is false.
                    HasArmor = ArmourProbe.Wearing;
                    ArmorPieces = ArmourProbe.PieceCount;
                    ArmorPoints = ArmourProbe.Points;
                    AncientArmor = ArmourProbe.Golden;
                    AncientName = ArmourProbe.Golden ? "Golden_Armour" : "";
                }
                else
                {
                    HasArmor = pieces > 0;
                    ArmorPieces = pieces;
                    ArmorPoints = anyPoints ? points : -1f;
                    AncientArmor = ancient;
                    AncientName = ancientName;
                }
                try
                {
                    ArmorDetail = viaSystem
                        ? ArmourProbe.Labels
                        : (seen.Count > 0 ? string.Join(" | ", seen.ToArray()) : "");
                }
                catch { ArmorDetail = ""; }

                Weapon = "";
                WeaponId = "";
                foreach (string hint in HeldHints)
                {
                    object val; string err;
                    if (!U.TryGet(inv, hint, out val, out err) || val == null) continue;
                    string s = ItemDecode.Describe(val);
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.IndexOf("ItemInstance", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ItemDecode.DumpOnce(val, "gear held " + hint);
                    }
                    Weapon = s;
                    WeaponId = hint;
                    break;
                }
                if (string.IsNullOrEmpty(Weapon))
                {
                    foreach (string hint in HeldHints)
                    {
                        object val = LocalPlayer.Member(hint);
                        if (val == null) continue;
                        string s = ItemDecode.Describe(val);
                        if (string.IsNullOrEmpty(s)) continue;
                        Weapon = s;
                        WeaponId = "player." + hint;
                        break;
                    }
                }

                if (Snapshots == 1 || Snapshots % 20 == 0)
                    Report = BuildReport(seen);
            }
            catch { }
        }

        private static void FailReport(string why)
        {
            try
            {
                FailTicks++;
                if (FailTicks != 60 && FailTicks % 600 != 0) return;
                var sb = new StringBuilder();
                sb.Append("=== gear probe -- FAILED ===\n");
                sb.Append("time      : ").Append(U.Now()).Append('\n');
                sb.Append("ticks     : ").Append(U.Num(FailTicks)).Append('\n');
                sb.Append("reason    : ").Append(why).Append('\n');
                sb.Append('\n');
                sb.Append("Expected once a live session is running. On the main\n");
                sb.Append("menu LocalPlayer is null and this is not an error.\n");
                Report = sb.ToString();
            }
            catch { }
        }

        private static void SweepHints(object inv, List<string> seen,
            ref int pieces, ref float points, ref bool anyPoints,
            ref bool ancient, ref string ancientName)
        {
            try
            {
                foreach (string hint in ArmorHints)
                {
                    object val; string err;
                    if (!U.TryGet(inv, hint, out val, out err) || val == null) continue;
                    if (val is bool) continue;

                    string tn = U.TypeName(val);

                    float f;
                    if (U.AsFloat(val, out f))
                    {
                        if (f > 0f)
                        {
                            pieces++;
                            points += f;
                            anyPoints = true;
                            seen.Add(hint + " = " + U.Num(f));
                        }
                        continue;
                    }

                    IEnumerable seq = val as IEnumerable;
                    if (seq != null && !(val is string))
                    {
                        int n = 0;
                        foreach (object el in seq)
                        {
                            if (el == null) continue;
                            n++;
                            string label = Label(el);
                            float d = DurabilityOf(el);
                            if (d > 0f) { points += d; anyPoints = true; }
                            seen.Add(label + (d > 0f ? " (" + U.Num(d) + ")" : ""));

                            if (label.IndexOf("ancient", StringComparison.OrdinalIgnoreCase) >= 0)
                            { ancient = true; ancientName = label; }
                        }
                        if (n > 0) pieces += n;
                        continue;
                    }

                    pieces++;
                    string lbl = ItemDecode.Describe(val);
                    if (string.IsNullOrEmpty(lbl) ||
                        lbl.IndexOf("ItemInstance", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (string.IsNullOrEmpty(lbl)) lbl = "";
                        ItemDecode.DumpOnce(val, "gear armor hint " + hint);
                    }
                    if (string.IsNullOrEmpty(lbl)) lbl = "[" + tn + "]";
                    seen.Add(hint + " = " + lbl);
                    if (lbl.IndexOf("ancient", StringComparison.OrdinalIgnoreCase) >= 0)
                    { ancient = true; ancientName = lbl; }
                }
            }
            catch { }
        }

        public static void Reset()
        {
            HasArmor = false;
            ArmorPieces = 0;
            ArmorPoints = -1f;
            AncientArmor = false;
            AncientName = "";
            Weapon = "";
            WeaponId = "";
            Snapshots = 0;
            ArmorRoute = "";
            FailTicks = 0;
            Report = null;
        }

        private static string Label(object o)
        {
            if (o == null) return "";
            try
            {
                float f;
                if (U.AsFloat(o, out f)) return U.Num(f);

                int id;
                if (ItemDecode.TryId(o, out id))
                {
                    string d = ItemDecode.Describe(o);
                    if (!string.IsNullOrEmpty(d) &&
                        d.IndexOf("ItemInstance", StringComparison.OrdinalIgnoreCase) < 0)
                        return d;
                }

                foreach (string h in NameHints)
                {
                    object v; string err;
                    if (U.TryGet(o, h, out v, out err) && v != null)
                    {
                        string s = v as string;
                        if (!string.IsNullOrEmpty(s)) return s;
                        float nf;
                        if (U.AsFloat(v, out nf)) return U.Num(nf);
                        return U.TypeName(v);
                    }
                }
                return U.TypeName(o);
            }
            catch { return "(unreadable)"; }
        }

        private static float DurabilityOf(object o)
        {
            if (o == null) return 0f;
            try
            {
                foreach (string h in DurHints)
                {
                    object v; string err;
                    if (!U.TryGet(o, h, out v, out err) || v == null) continue;
                    if (v is bool) continue;
                    float f;
                    if (U.AsFloat(v, out f) && f > 0f) return f;
                }
            }
            catch { }
            return 0f;
        }

        private static string PlateLabel(float points)
        {
            if (points <= 0f) return "";
            foreach (string k in KnownPlates)
            {
                int c = k.IndexOf(':');
                float v;
                if (!float.TryParse(k.Substring(0, c), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out v)) continue;
                if (Math.Abs(v - points) < 0.5f) return k.Substring(c + 1);
            }
            return "";
        }

        private static string BuildReport(List<string> seen)
        {
            var sb = new StringBuilder();
            sb.Append("=== gear probe -- worn and held ===\n");
            sb.Append("time      : ").Append(U.Now()).Append('\n');
            sb.Append("snapshots : ").Append(U.Num(Snapshots)).Append('\n');
            sb.Append('\n');

            sb.Append("ARMOR\n");
            sb.Append("  has_armor     : ").Append(U.Bool(HasArmor)).Append('\n');
            sb.Append("  pieces        : ").Append(U.Num(ArmorPieces)).Append('\n');
            sb.Append("  route         : ").Append(
                string.IsNullOrEmpty(ArmorRoute) ? "(none)" : ArmorRoute).Append('\n');
            if (ArmorPoints >= 0f)
            {
                sb.Append("  points        : ").Append(U.Num(ArmorPoints));
                string pl = PlateLabel(ArmorPoints);
                if (!string.IsNullOrEmpty(pl)) sb.Append("  (near " + pl + ")");
                sb.Append('\n');
            }
            else
            {
                sb.Append("  points        : (not readable)\n");
            }
            sb.Append("  ancient_armor : ").Append(U.Bool(AncientArmor));
            if (AncientArmor) sb.Append("  ").Append(AncientName);
            sb.Append('\n');
            sb.Append('\n');
            sb.Append("ARMOUR SYSTEM (Sons.Wearable.Armour.PlayerArmourSystem)\n");
            sb.Append(ArmourProbe.Report());
            sb.Append('\n');
            sb.Append("  ranking gate  : ")
              .Append(HasArmor ? "VOID -- armour worn" : "clear -- no armour seen")
              .Append('\n');

            sb.Append('\n');
            sb.Append("HELD\n");
            sb.Append("  weapon : ")
              .Append(string.IsNullOrEmpty(Weapon) ? "(not read)" : Weapon)
              .Append('\n');
            if (!string.IsNullOrEmpty(WeaponId))
                sb.Append("  via    : ").Append(WeaponId).Append('\n');

            if (seen != null && seen.Count > 0)
            {
                sb.Append('\n');
                sb.Append("OBSERVED SLOTS (").Append(U.Num(seen.Count)).Append(")\n");
                for (int i = 0; i < seen.Count && i < 40; i++)
                    sb.Append("  ").Append(seen[i]).Append('\n');
            }

            sb.Append('\n');
            sb.Append("NOTE: ids and names are reported as observed. The\n");
            sb.Append("      durability table is used to label, not to judge.\n");
            return sb.ToString();
        }

        public static string Status()
        {
            if (Snapshots == 0) return "gear: (no session yet)";
            var sb = new StringBuilder();
            sb.Append("gear: ");
            sb.Append(HasArmor ? "ARMOR " + U.Num(ArmorPieces) + "pc" : "no armor");
            if (AncientArmor) sb.Append(" ANCIENT");
            if (!string.IsNullOrEmpty(Weapon)) sb.Append("  held=" + Weapon);
            return sb.ToString();
        }
    }
}
