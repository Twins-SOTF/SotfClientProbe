// Stats.cs -- session counters behind the in-game HUD.
//
// WHY THE COUNTERS LIVE HERE AND NOT IN THE HUD
// The HUD only paints; every number it shows is produced here, once, on the
// sampling tick. That keeps OnGUI free of any game-object access.
//
// THREE NUMBERS, THREE DIFFERENT DEFINITIONS OF "TRUTH"
//   Hits     -- counted from the damage hook, i.e. from the game telling us a
//               hit landed. Not derived from health going down.
//   HpLoss   -- summed health actually lost (armour/parry reduce raw damage).
//   Distance -- integrated from position samples. Jumps larger than MaxStepM
//               are dropped (respawn/teleport/loading is not walking).

using System;

namespace SotfClientProbe
{
    internal static class Stats
    {
        public static int Hits;         // hits taken by the local player
        public static float HpLoss;     // health actually lost, summed
        public static double Distance;  // metres walked

        /// v2.35: strikes the game itself flagged as blocked.
        /// The patched method carries a flag in its argument tuple; each hit
        /// with the flag set came through at exactly one tenth of the raw
        /// damage. Counting the flag is counting the game's own verdict.
        public static int Blocks;

        public static float Hp = float.NaN;
        public static bool HaveHp;
        public static float X, Y, Z;
        public static bool HavePos;

        public static float SpeedKmh;
        private const float SpeedSmooth = 0.5f;

        public static int DedupeMs = 200;
        public static float MaxStepM = 20f;
        public static int MaxPosGapMs = 5000;
        private const float MinHpDrop = 0.01f;

        private static bool _haveLastPos;
        private static float _lx, _ly, _lz;
        private static long _lastPosMs;
        private static long _lastHitMs;
        private static long _lastIdleMs;

        public static void Reset()
        {
            Hits = 0;
            HpLoss = 0f;
            Distance = 0d;
            Blocks = 0;
            Hp = float.NaN;
            HaveHp = false;
            X = Y = Z = 0f;
            HavePos = false;
            SpeedKmh = 0f;
            _haveLastPos = false;
            _lastPosMs = 0;
            _lastHitMs = 0;
            _lastIdleMs = 0;
        }

        public static bool OnLocalHit(long nowMs, float dmg, bool blocked = false)
        {
            if (_lastHitMs != 0 && nowMs - _lastHitMs >= 0 && nowMs - _lastHitMs < DedupeMs)
                return false;
            _lastHitMs = nowMs;
            Hits++;
            if (blocked) Blocks++;
            Series.AddHit(nowMs, dmg);
            return true;
        }

        public static bool OnSample(bool haveHp, float hp, bool havePos,
                                    float x, float y, float z, long nowMs)
        {
            bool dropped = false;
            float inst = 0f;

            if (haveHp && !float.IsNaN(hp) && !float.IsInfinity(hp))
            {
                HaveHp = true;
                if (!float.IsNaN(Hp))
                {
                    float drop = Hp - hp;
                    if (drop >= MinHpDrop)
                    {
                        HpLoss += drop;
                        dropped = true;
                    }
                }
                Hp = hp;
            }

            if (havePos && !float.IsNaN(x) && !float.IsNaN(y) && !float.IsNaN(z))
            {
                HavePos = true;
                X = x; Y = y; Z = z;

                if (_haveLastPos)
                {
                    double dx = (double)x - _lx;
                    double dy = (double)y - _ly;
                    double dz = (double)z - _lz;
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    bool fresh = _lastPosMs == 0 || (nowMs - _lastPosMs) <= MaxPosGapMs;
                    if (fresh && d <= MaxStepM)
                    {
                        Distance += d;
                        long dt = nowMs - _lastPosMs;
                        if (dt > 0)
                        {
                            double ms = d / (dt / 1000.0);
                            inst = (float)(ms * 3.6);
                        }
                    }
                }

                _haveLastPos = true;
                _lx = x; _ly = y; _lz = z;
                _lastPosMs = nowMs;
            }

            SpeedKmh = SpeedKmh * (1f - SpeedSmooth) + inst * SpeedSmooth;
            if (SpeedKmh < Series.IdleKmh)
            {
                long gap = (_lastIdleMs == 0) ? 0 : (nowMs - _lastIdleMs);
                if (gap > 0 && gap <= MaxPosGapMs) Series.IdleSec += gap / 1000.0;
            }
            _lastIdleMs = nowMs;

            return dropped;
        }

        public static string Fmt(float v, int dp)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "--";
            return v.ToString("F" + dp.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string Fmt(double v, int dp)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "--";
            return v.ToString("F" + dp.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
