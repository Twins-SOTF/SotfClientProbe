// DeathProbe.cs -- cumulative death count for the HUD.
//
// PlayerStats keeps its own tally (_playerDeathCount, authoritative;
// PlayerIsDowned; IsDead -- confirmed in the interop dump). Reading it beats
// inferring deaths from health. Three counters run whenever they can and all
// go into the report:
//   1. Native  -- _playerDeathCount, reported as a DELTA (first reading is the
//                baseline; HUD shows current minus baseline)
//   2. Downed  -- false -> true edge on PlayerIsDowned
//   3. Health  -- health reaching zero
// A 60 s window collapses chain deaths into one event; raw (unwindowed) and
// windowed figures are both kept and reported.
//
// THREADING: touches UnityEngine.Object through LocalPlayer -- must be called
// from the sampling tick (Unity main thread) only.

using System;
using System.Globalization;
using System.Text;

namespace SotfClientProbe
{
    internal static class DeathProbe
    {
        public const long WindowMs = 60000L;

        public static int Deaths;
        public static int Native;
        public static int Downed;
        public static int Health;

        public static int NativeRaw;
        public static int DownedRaw;
        public static int HealthRaw;
        public static int Raw;

        public static string Source = "none";
        public static bool HaveNative;
        public static int NativeField;
        public static int Baseline;
        public static string Report = "";
        public static string Note = "";

        private const float DeadHp = 0.001f;
        private const float AliveHp = 1.0f;

        private static bool _baselineSet;
        private static bool _lastDown;
        private static bool _haveDownState;

        public static bool IsDowned
        {
            get { return _haveDownState && _lastDown; }
        }
        private static bool _lastDead;
        private static bool _haveHpState;

        private static long _lastNativeMs, _lastDownMs, _lastHealthMs;
        private static bool _haveNatT, _haveDownT, _haveHealthT;
        private static int _lastNativeDelta;

        public static void Reset()
        {
            Deaths = Native = Downed = Health = 0;
            NativeRaw = DownedRaw = HealthRaw = Raw = 0;
            Source = "none";
            HaveNative = false;
            NativeField = Baseline = 0;
            Report = Note = "";
            _baselineSet = false;
            _lastDown = false;
            _haveDownState = false;
            _lastDead = false;
            _haveHpState = false;
            _lastNativeMs = _lastDownMs = _lastHealthMs = 0;
            _haveNatT = _haveDownT = _haveHealthT = false;
            _lastNativeDelta = 0;
        }

        private static bool Accept(ref long lastMs, ref bool have)
        {
            long now = Environment.TickCount64;
            if (!have || now - lastMs >= WindowMs)
            {
                lastMs = now;
                have = true;
                return true;
            }
            return false;
        }

        public static void Update(bool haveHp, float hp)
        {
            try { ReadNative(); } catch { }
            try { ReadDowned(); } catch { }
            try { ReadHealth(haveHp, hp); } catch { }

            if (HaveNative)
            {
                Deaths = Native; Raw = NativeRaw; Source = "native";
            }
            else if (Downed > 0 || _haveDownState)
            {
                Deaths = Downed; Raw = DownedRaw; Source = "downed";
            }
            else if (Health > 0 || _haveHpState)
            {
                Deaths = Health; Raw = HealthRaw; Source = "health";
            }
            else
            {
                Deaths = 0; Raw = 0; Source = "none";
            }
        }

        private static void ReadNative()
        {
            object stats = LocalPlayer.Stats();
            if (stats == null) { HaveNative = false; return; }

            object v = LocalPlayer.MemberOf(stats, "_playerDeathCount");
            if (v == null)
            {
                object tmp; string err;
                if (U.TryCall(stats, "get__playerDeathCount", out tmp, out err)) v = tmp;
            }
            if (v == null) { HaveNative = false; return; }

            int n;
            try { n = Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { HaveNative = false; return; }

            NativeField = n;
            HaveNative = true;

            if (!_baselineSet)
            {
                _baselineSet = true;
                Baseline = n;
                _lastNativeDelta = 0;
                Note = "native baseline = " + n.ToString(CultureInfo.InvariantCulture)
                     + " (deaths before attach are excluded)";
            }

            int d = n - Baseline;
            if (d < 0)
            {
                Baseline = n;
                d = 0;
                _lastNativeDelta = 0;
                Note = "native counter reset, re-baselined at " +
                       n.ToString(CultureInfo.InvariantCulture);
            }

            if (d > _lastNativeDelta)
            {
                int inc = d - _lastNativeDelta;
                for (int k = 0; k < inc; k++)
                {
                    NativeRaw++;
                    if (Accept(ref _lastNativeMs, ref _haveNatT)) Native++;
                }
                _lastNativeDelta = d;
            }
        }

        private static void ReadDowned()
        {
            object stats = LocalPlayer.Stats();
            if (stats == null) return;

            object v = LocalPlayer.MemberOf(stats, "PlayerIsDowned");
            if (v == null) v = LocalPlayer.MemberOf(stats, "_playerIsDowned");
            if (v == null)
            {
                object tmp; string err;
                if (U.TryCall(stats, "IsDead", out tmp, out err)) v = tmp;
            }
            if (v == null) return;

            bool down;
            try { down = Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return; }

            if (_haveDownState && down && !_lastDown)
            {
                DownedRaw++;
                if (Accept(ref _lastDownMs, ref _haveDownT)) Downed++;
            }
            _haveDownState = true;
            _lastDown = down;
        }

        private static void ReadHealth(bool haveHp, float hp)
        {
            if (!haveHp || float.IsNaN(hp) || float.IsInfinity(hp)) return;

            bool dead = hp <= DeadHp;
            bool alive = hp > AliveHp;

            if (_haveHpState && dead && !_lastDead)
            {
                HealthRaw++;
                if (Accept(ref _lastHealthMs, ref _haveHealthT)) Health++;
            }

            if (alive) _lastDead = false;
            else if (dead) _lastDead = true;

            _haveHpState = true;
        }

        public static string BuildReport()
        {
            var sb = new StringBuilder();
            sb.Append("=== death count ===\n");
            sb.Append("time     : ").Append(U.Now()).Append('\n');
            sb.Append("window   : ").Append((WindowMs / 1000L).ToString(CultureInfo.InvariantCulture))
              .Append(" s (deaths closer together than this count once)\n");
            sb.Append("shown    : ").Append(Deaths.ToString(CultureInfo.InvariantCulture))
              .Append("   (source: ").Append(Source).Append(")\n");
            sb.Append("  of     : ").Append(Raw.ToString(CultureInfo.InvariantCulture))
              .Append(" raw death events on the same source\n");
            sb.Append('\n');
            sb.Append("native   : ").Append(Native.ToString(CultureInfo.InvariantCulture))
              .Append("  [raw ").Append(NativeRaw.ToString(CultureInfo.InvariantCulture)).Append(']')
              .Append(HaveNative ? "" : "  [unreadable]").Append('\n');
            sb.Append("  field  : ").Append(NativeField.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("  base   : ").Append(Baseline.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("downed   : ").Append(Downed.ToString(CultureInfo.InvariantCulture))
              .Append("  [raw ").Append(DownedRaw.ToString(CultureInfo.InvariantCulture)).Append("]\n");
            sb.Append("health   : ").Append(Health.ToString(CultureInfo.InvariantCulture))
              .Append("  [raw ").Append(HealthRaw.ToString(CultureInfo.InvariantCulture)).Append("]\n");

            if (Note.Length > 0) sb.Append('\n').Append(Note).Append('\n');

            sb.Append('\n');
            sb.Append("Reading the three together:\n");
            sb.Append("  native agreeing with downed -> the counter is real.\n");
            sb.Append("  native stuck while downed climbs -> _playerDeathCount\n");
            sb.Append("      is not per-session; trust downed instead.\n");
            sb.Append("  health staying 0 while others climb -> expected: this\n");
            sb.Append("      game downs the player rather than zeroing health.\n");
            sb.Append("  shown much lower than its raw count -> the 60 s window\n");
            sb.Append("      suppressed chain deaths; both numbers are reported.\n");

            Report = sb.ToString();
            return Report;
        }
    }
}
