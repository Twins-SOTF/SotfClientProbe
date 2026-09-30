// Series.cs -- the time series behind the F1 chart views.
//
// WHY THIS FILE EXISTS SEPARATELY FROM Stats.cs
// Stats.cs holds scalar totals: how many hits, how much health lost, how far.
// Those are what the JSONL stream records, and they are enough for a text HUD.
// A chart needs more -- it needs the SHAPE of the session: when the hits
// landed, how fast the player was moving at each moment, how the loss
// accumulated. That is a different thing (a history, not a total) and it is
// only ever read by the overlay, so it lives here and nowhere else.
//
// MEMORY BOUND, AND WHY IT IS A HARD ONE
// Sampling runs at 2 Hz. A two-hour session is 14,400 speed samples. At 16
// bytes a point that is trivial, but OnGUI is not: drawing thousands of line
// segments every frame at 100+ fps would cost more than the game's own UI.
// So every series is capped at MaxPoints and aggregated -- never truncated,
// because truncating would silently drop the end of the session.
//
// No Unity types here. This file compiles without interop, like Stats.cs.

using System;
using System.Collections.Generic;

namespace SotfClientProbe
{
    internal struct Pt
    {
        public long T;   // ms since Series.T0
        public float V;  // meaning depends on the series
    }

    internal static class Series
    {
        public const int MaxPoints = 200;
        public const float Unknown = -1f;

        public static readonly List<Pt> Hits = new List<Pt>();
        public static readonly List<Pt> Speed = new List<Pt>();
        public static readonly List<Pt> Loss = new List<Pt>();

        public static long T0;
        public static float SpeedMax = 5f;
        public static float IdleKmh = 0.5f;
        public static double IdleSec;

        private static bool _pending;
        private static long _pendingT;
        private static double _lossAtPending;

        public static void Reset(long t0)
        {
            T0 = t0;
            Hits.Clear();
            Speed.Clear();
            Loss.Clear();
            SpeedMax = 5f;
            IdleSec = 0d;
            _pending = false;
            _pendingT = 0;
            _lossAtPending = 0d;
        }

        public static void AddHit(long nowMs, float dmg)
        {
            long t = nowMs - T0;
            if (t < 0) t = 0;
            if (!float.IsNaN(dmg) && dmg > 0f)
            {
                Hits.Add(new Pt { T = t, V = dmg });
                return;
            }
            _pending = true;
            _pendingT = t;
            _lossAtPending = Loss.Count > 0 ? Loss[Loss.Count - 1].V : 0d;
        }

        public static void ResolvePending(double lossNow)
        {
            if (!_pending) return;
            _pending = false;
            double drop = lossNow - _lossAtPending;
            float v = drop > 0d ? (float)drop : 1f;
            Hits.Add(new Pt { T = _pendingT, V = v });
        }

        public static void AddSpeed(long nowMs, float kmh)
        {
            long t = nowMs - T0;
            if (t < 0) t = 0;
            if (float.IsNaN(kmh) || float.IsInfinity(kmh)) kmh = 0f;
            if (kmh < 0f) kmh = 0f;
            Speed.Add(new Pt { T = t, V = kmh });
            if (kmh > SpeedMax) SpeedMax = kmh;
        }

        public static void AddLoss(long nowMs, float cumulative)
        {
            long t = nowMs - T0;
            if (t < 0) t = 0;
            if (float.IsNaN(cumulative) || float.IsInfinity(cumulative)) return;
            Loss.Add(new Pt { T = t, V = cumulative });
        }

        public static bool HasPos { get { return Speed.Count > 0; } }

        public static double SpanSec
        {
            get
            {
                if (Speed.Count < 2) return 0d;
                return (Speed[Speed.Count - 1].T - Speed[0].T) / 1000.0;
            }
        }

        public static List<Pt> Thin(List<Pt> src, int max, int mode)
        {
            if (src == null || src.Count <= max) return src;
            var outp = new List<Pt>(max);
            int n = src.Count;
            double bucket = (double)n / max;
            for (int i = 0; i < max; i++)
            {
                int a = (int)(i * bucket);
                int b = (int)((i + 1) * bucket);
                if (b <= a) b = a + 1;
                if (b > n) b = n;
                if (a >= n) break;
                if (mode == 1)
                {
                    outp.Add(src[b - 1]);
                }
                else
                {
                    float best = src[a].V;
                    for (int k = a + 1; k < b; k++)
                        if (src[k].V > best) best = src[k].V;
                    outp.Add(new Pt { T = src[a].T, V = best });
                }
            }
            return outp;
        }
    }
}
