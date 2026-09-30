// HudBand.cs -- v2.35: the ten-cell status band, the player's HUD.
//
// One band across the top, ten cells, nothing to cycle through; drawn from the
// same counters the stream already carries. Cells: STEAM ID (last 8, starred),
// HP LOST, DIST, HITS, RANK/DEATH (grade + count), PARRY/BLK (parries are
// invisible to the client -- zero-damage hits produce no damage event; blocks
// are counted from the block flag), DODGE (needs the server's attack count),
// WPN, SEASON/DAY/TIME, PLAY TIME (cumulative, survives reconnect inside the
// grace window).
//
// F1 steps  grey -> lapis -> auto -> off -> grey. Auto palette samples scene
// luminance twice a second and eases with a dead band.

#if HAS_INTEROP

using System;
using System.Globalization;
using UnityEngine;

namespace SotfClientProbe
{
    internal static class HudBand
    {
        public const int P_GRAY = 0;
        public const int P_LAPIS = 1;
        public const int P_AUTO = 2;
        public const int P_COUNT = 3;

        public static int Palette = P_GRAY;
        public static bool Enabled = true;
        public static bool Connected;
        public const long ArmMs = 30000L;

        private const float BAND_H = 40f;
        private const float PAD = 6f;
        private const int N = 10;

        private static float Scale()
        {
            try
            {
                float h = Screen.height;
                if (h <= 0f) return 1f;
                float s = h / 1080f;
                if (s < 0.70f) s = 0.70f;
                if (s > 2.20f) s = 2.20f;
                return s;
            }
            catch { return 1f; }
        }

        private static readonly Color G_BG = new Color(0.50f, 0.50f, 0.50f, 0.52f);
        private static readonly Color G_TITLE = new Color(0.90f, 0.90f, 0.90f, 1f);
        private static readonly Color G_VAL = new Color(1f, 1f, 1f, 1f);
        private static readonly Color G_EDGE = new Color(0.80f, 0.80f, 0.80f, 0.30f);

        private static readonly Color L_BG = new Color(0.149f, 0.380f, 0.612f, 0.74f);
        private static readonly Color L_TITLE = new Color(0.82f, 0.89f, 0.97f, 1f);
        private static readonly Color L_VAL = new Color(1f, 1f, 1f, 1f);
        private static readonly Color L_EDGE = new Color(0.45f, 0.66f, 0.88f, 0.42f);

        private static readonly Color A_DARK_BG = new Color(0.07f, 0.08f, 0.09f, 0.80f);
        private static readonly Color A_DARK_TITLE = new Color(0.72f, 0.74f, 0.75f, 1f);
        private static readonly Color A_DARK_VAL = new Color(0.97f, 0.97f, 0.95f, 1f);
        private static readonly Color A_DARK_EDGE = new Color(0.55f, 0.57f, 0.58f, 0.30f);

        private static readonly Color A_LIGHT_BG = new Color(0.88f, 0.90f, 0.90f, 0.82f);
        private static readonly Color A_LIGHT_TITLE = new Color(0.22f, 0.24f, 0.25f, 1f);
        private static readonly Color A_LIGHT_VAL = new Color(0.04f, 0.05f, 0.06f, 1f);
        private static readonly Color A_LIGHT_EDGE = new Color(0.30f, 0.32f, 0.33f, 0.35f);

        private static readonly Color GR_SSS = new Color(0.55f, 0.40f, 0.05f, 1f);
        private static readonly Color GR_SS = new Color(1.00f, 0.84f, 0.00f, 1f);
        private static readonly Color GR_S = new Color(0.78f, 0.78f, 0.82f, 1f);
        private static readonly Color GR_A = new Color(0.80f, 0.45f, 0.20f, 1f);
        private static readonly Color GR_B = new Color(0.62f, 0.30f, 0.16f, 1f);
        private static readonly Color GR_C = new Color(1.00f, 1.00f, 1.00f, 1f);
        private static readonly Color GR_D = new Color(1.00f, 1.00f, 1.00f, 1f);
        private static readonly Color C_NODATA = new Color(0.62f, 0.62f, 0.62f, 1f);

        private static GUIStyle _sTitle, _sVal, _sValSmall, _sRank, _sFoot, _sArm;
        private static Texture2D _white;
        private static bool _ready;
        private static int _styledFor = -1;
        private static float _styledScale = -1f;

        private struct Cell
        {
            public string Title;
            public string Value;
            public Color VCol;
            public bool Dim;
            public bool Grade;
        }

        private static readonly Cell[] _cells = new Cell[N];
        private static long _lastDataMs;
        private static bool _haveData;

        private static long _armedAtMs;
        private static bool _wasConnected;

        private static Texture2D _lumTex;
        private static long _lastLumMs;
        private static float _lum = 0.5f;
        private static bool _lumValid;
        private const float LUM_DEAD = 0.045f;
        private const float LUM_EASE = 0.35f;
        private const long LUM_MS = 500L;

        private const string FOOTER = "F1: ON/SWITCH/OFF     F9: CLEAR DATA";

        public static void Arm()
        {
            if (_armedAtMs == 0) _armedAtMs = Environment.TickCount64;
        }

        public static void Disarm()
        {
            _armedAtMs = 0;
            _haveData = false;
            _wasConnected = false;
        }

        public static void Cycle()
        {
            if (!Enabled)
            {
                Enabled = true;
                Palette = P_GRAY;
                return;
            }
            Palette++;
            if (Palette >= P_COUNT)
            {
                Enabled = false;
                Palette = P_GRAY;
            }
        }

        public static void ClearStats()
        {
            SessionStore.Clear();
            _haveData = false;
        }

        public static void Draw()
        {
            try
            {
                if (!Enabled) return;

                if (Connected && !_wasConnected) { _wasConnected = true; Arm(); }
                if (!Connected && _wasConnected) { _wasConnected = false; Disarm(); }
                if (!Connected) return;

                Event ev = Event.current;
                if (ev == null || ev.type != EventType.Repaint) return;

                long now = Environment.TickCount64;

                if (!_haveData || now - _lastDataMs >= 500L)
                {
                    _lastDataMs = now;
                    Rebuild();
                    _haveData = true;
                }

                if (Palette == P_AUTO && now - _lastLumMs >= LUM_MS)
                {
                    _lastLumMs = now;
                    SampleLuminance();
                }

                float s = Scale();
                float bandH = BAND_H * s;
                float x = PAD, y = PAD;
                float w = Screen.width - PAD * 2f;
                if (w <= 0f) return;

                Color bg, title, val, edge;
                ResolveColors(out bg, out title, out val, out edge);

                EnsureStyles(s, title, val);

                DrawRect(x, y, w, bandH, bg);
                DrawRect(x, y, w, Mathf.Max(1f, 1f * s), edge);

                long since = _armedAtMs == 0 ? 0L : now - _armedAtMs;

                if (since < ArmMs)
                {
                    int left = (int)((ArmMs - since) / 1000L) + 1;
                    if (left > 30) left = 30;
                    string t = "STARTING IN " + left.ToString(CultureInfo.InvariantCulture) + "s";
                    var ar = new Rect(x, y, w, bandH);
                    GUI.Label(ar, t, _sArm);
                }
                else
                {
                    float cw = w / N;
                    for (int i = 0; i < N; i++)
                    {
                        float cx = x + cw * i;
                        DrawCell(cx, y, cw, bandH, i, edge);
                    }
                }

                float fh = 13f * s;
                var fr = new Rect(x, y + bandH + 2f * s, w, fh);
                GUI.Label(fr, FOOTER, _sFoot);
            }
            catch { }
        }

        private static void ResolveColors(out Color bg, out Color title,
                                          out Color val, out Color edge)
        {
            switch (Palette)
            {
                case P_LAPIS:
                    bg = L_BG; title = L_TITLE; val = L_VAL; edge = L_EDGE;
                    break;

                case P_AUTO:
                    if (_lum >= 0.5f)
                    {
                        bg = A_DARK_BG; title = A_DARK_TITLE;
                        val = A_DARK_VAL; edge = A_DARK_EDGE;
                    }
                    else
                    {
                        bg = A_LIGHT_BG; title = A_LIGHT_TITLE;
                        val = A_LIGHT_VAL; edge = A_LIGHT_EDGE;
                    }
                    break;

                default:
                    bg = G_BG; title = G_TITLE; val = G_VAL; edge = G_EDGE;
                    break;
            }
        }

        private static void DrawCell(float x, float y, float w, float h,
                                     int i, Color edge)
        {
            if (i > 0) DrawRect(x, y + h * 0.18f, Mathf.Max(1f, 1f), h * 0.64f, edge);

            var ct = _cells[i];
            float pad = 2f;

            var tr = new Rect(x + pad, y + h * 0.10f, w - pad * 2f, h * 0.30f);
            GUI.Label(tr, ct.Title ?? "", _sTitle);

            if (string.IsNullOrEmpty(ct.Value)) return;

            var vr = new Rect(x + pad, y + h * 0.40f, w - pad * 2f, h * 0.50f);
            if (ct.Dim) GUI.Label(vr, ct.Value, _sValSmall);
            else if (ct.Grade)
            {
                var sv = _sRank.normal.textColor;
                _sRank.normal.textColor = ct.VCol;
                GUI.Label(vr, ct.Value, _sRank);
                _sRank.normal.textColor = sv;
            }
            else GUI.Label(vr, ct.Value, _sVal);
        }

        private static void Rebuild()
        {
            for (int i = 0; i < N; i++)
            {
                _cells[i].Title = "";
                _cells[i].Value = "";
                _cells[i].VCol = GR_C;
                _cells[i].Dim = false;
                _cells[i].Grade = false;
            }

            _cells[0].Title = "STEAM ID";
            _cells[0].Value = StarredSteamId();

            _cells[1].Title = "HP LOST";
            _cells[1].Value = Stats.HaveHp || Stats.HpLoss > 0f
                ? ((int)Math.Round(Stats.HpLoss)).ToString(CultureInfo.InvariantCulture)
                : "--";

            _cells[2].Title = "DIST";
            _cells[2].Value = Stats.HavePos || Stats.Distance > 0.0
                ? ((int)Math.Round(Stats.Distance)).ToString(CultureInfo.InvariantCulture) + "m"
                : "--";

            _cells[3].Title = "HITS";
            _cells[3].Value = Stats.Hits.ToString(CultureInfo.InvariantCulture);

            int deaths = (int)SessionStore.DeathsCum;
            Color gc;
            string grade = Grade(deaths, out gc);
            _cells[4].Title = "RANK / DEATH";
            _cells[4].Value = grade + " / " + Two(deaths);
            _cells[4].VCol = gc;
            _cells[4].Grade = true;

            _cells[5].Title = "PARRY / BLK";
            _cells[5].Value = "NO DATA / " + Stats.Blocks.ToString(CultureInfo.InvariantCulture);

            _cells[6].Title = "DODGE";
            _cells[6].Value = "NO DATA";
            _cells[6].Dim = true;

            _cells[7].Title = "WPN";
            _cells[7].Value = WeaponName();

            _cells[8].Title = "SEASON / DAY / TIME";
            _cells[8].Value = WorldCell();

            _cells[9].Title = "PLAY TIME";
            _cells[9].Value = FmtHms(SessionStore.PlayMsCum);
        }

        private static string StarredSteamId()
        {
            try
            {
                if (Identity.SteamId == 0UL) return "NO DATA";
                string s = Identity.SteamId.ToString(CultureInfo.InvariantCulture);
                if (s.Length <= 8) return s;
                int stars = s.Length - 8;
                if (stars > 9) stars = 9;
                return new string('*', stars) + s.Substring(s.Length - 8);
            }
            catch { return "NO DATA"; }
        }

        private static string WeaponName()
        {
            try
            {
                string w = GearProbe.Weapon;
                if (string.IsNullOrEmpty(w)) return "NONE";
                int c = w.IndexOf(':');
                string n = c >= 0 ? w.Substring(c + 1) : w;
                n = n.Replace('_', ' ').Trim();
                if (n.Length == 0) return "NONE";
                if (n.Length > 13) n = n.Substring(0, 13);
                return n.ToUpperInvariant();
            }
            catch { return "NONE"; }
        }

        private static string WorldCell()
        {
            try
            {
                string season = ShortSeason(WorldProbe.Season);
                string day = float.IsNaN(WorldProbe.Days)
                    ? "--"
                    : ((int)Math.Round(WorldProbe.Days)).ToString(CultureInfo.InvariantCulture);

                string clock = "--:--";
                if (!float.IsNaN(WorldProbe.Hour))
                {
                    double h = WorldProbe.Hour % 24.0;
                    if (h < 0) h += 24.0;
                    int hh = (int)h;
                    int mm = (int)Math.Round((h - hh) * 60.0);
                    if (mm >= 60) { mm = 0; hh++; }
                    if (hh >= 24) hh = 0;
                    clock = hh.ToString("00", CultureInfo.InvariantCulture)
                          + ":" + mm.ToString("00", CultureInfo.InvariantCulture);
                }
                return season + " / " + day + " / " + clock;
            }
            catch { return "-- / -- / --:--"; }
        }

        private static string ShortSeason(string s)
        {
            if (string.IsNullOrEmpty(s)) return "--";
            string u = s.ToUpperInvariant();
            if (u.IndexOf("WINTER") >= 0) return "WIN";
            if (u.IndexOf("SUMMER") >= 0) return "SUM";
            if (u.IndexOf("SPRING") >= 0) return "SPR";
            if (u.IndexOf("AUTUMN") >= 0) return "AUT";
            if (u.IndexOf("FALL") >= 0) return "FAL";
            if (u.Length > 3) return u.Substring(0, 3);
            return u;
        }

        public static string Grade(int deaths, out Color c)
        {
            if (deaths <= 0) { c = GR_SSS; return "SSS"; }
            if (deaths <= 5) { c = GR_SS; return "SS"; }
            if (deaths <= 10) { c = GR_S; return "S"; }
            if (deaths <= 20) { c = GR_A; return "A"; }
            if (deaths <= 30) { c = GR_B; return "B"; }
            if (deaths <= 40) { c = GR_C; return "C"; }
            c = GR_D; return "D";
        }

        private static string Two(int v)
        {
            if (v < 0) v = 0;
            if (v > 99) return v.ToString(CultureInfo.InvariantCulture);
            return v.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string FmtHms(long ms)
        {
            if (ms < 0) ms = 0;
            long total = ms / 1000L;
            long h = total / 3600L;
            long m = (total % 3600L) / 60L;
            long s = total % 60L;
            return h.ToString("00", CultureInfo.InvariantCulture)
                 + ":" + m.ToString("00", CultureInfo.InvariantCulture)
                 + ":" + s.ToString("00", CultureInfo.InvariantCulture);
        }

        private static void SampleLuminance()
        {
            try
            {
                int w = 48, h = 8;
                int sx = Screen.width / 2 - w / 2;
                int sy = (int)(Screen.height * 0.62f);
                if (sx < 0) sx = 0;
                if (sy < 0) sy = 0;
                if (sy + h > Screen.height) sy = Screen.height - h;
                if (sy < 0 || Screen.width < w) return;

                if (_lumTex == null)
                    _lumTex = new Texture2D(w, h, TextureFormat.RGB24, false);

                _lumTex.ReadPixels(new Rect(sx, sy, w, h), 0, 0, false);
                Color32[] px = _lumTex.GetPixels32();
                if (px == null || px.Length == 0) return;

                double sum = 0.0;
                for (int i = 0; i < px.Length; i++)
                {
                    sum += (0.299 * px[i].r + 0.587 * px[i].g + 0.114 * px[i].b) / 255.0;
                }
                float lum = (float)(sum / px.Length);

                if (!_lumValid) { _lum = lum; _lumValid = true; return; }

                if (Math.Abs(lum - _lum) < LUM_DEAD) return;
                _lum += (lum - _lum) * LUM_EASE;
            }
            catch { }
        }

        private static void EnsureStyles(float s, Color title, Color val)
        {
            int key = Palette * 1000 + (int)(s * 100f);
            if (_ready && _styledFor == key && Math.Abs(_styledScale - s) < 0.001f)
            {
                if (_sTitle != null)
                {
                    _sTitle.normal.textColor = title;
                    _sVal.normal.textColor = val;
                    _sArm.normal.textColor = val;
                    _sFoot.normal.textColor = title;
                }
                return;
            }
            _styledFor = key;
            _styledScale = s;
            _ready = true;

            if (_white == null)
            {
                _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply(false, true);
            }

            _sTitle = new GUIStyle(GUI.skin.label);
            _sTitle.fontSize = Mathf.RoundToInt(11f * s);
            _sTitle.fontStyle = FontStyle.Bold;
            _sTitle.alignment = TextAnchor.UpperCenter;
            _sTitle.normal.textColor = title;
            _sTitle.richText = false;
            _sTitle.wordWrap = false;
            _sTitle.clipping = TextClipping.Overflow;

            _sVal = new GUIStyle(_sTitle);
            _sVal.fontSize = Mathf.RoundToInt(17f * s);
            _sVal.alignment = TextAnchor.UpperCenter;

            _sValSmall = new GUIStyle(_sVal);
            _sValSmall.fontSize = Mathf.RoundToInt(13f * s);
            _sValSmall.normal.textColor = C_NODATA;

            _sRank = new GUIStyle(_sVal);
            _sRank.fontSize = Mathf.RoundToInt(16f * s);

            _sArm = new GUIStyle(_sVal);
            _sArm.fontSize = Mathf.RoundToInt(18f * s);
            _sArm.normal.textColor = val;

            _sFoot = new GUIStyle(GUI.skin.label);
            _sFoot.fontSize = Mathf.RoundToInt(11f * s);
            _sFoot.fontStyle = FontStyle.Normal;
            _sFoot.alignment = TextAnchor.UpperCenter;
            _sFoot.normal.textColor = title;
            _sFoot.wordWrap = false;
            _sFoot.clipping = TextClipping.Overflow;
        }

        private static void DrawRect(float x, float y, float w, float h, Color c)
        {
            if (_white == null) return;
            if (w <= 0f || h <= 0f) return;
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(x, y, w, h), _white, ScaleMode.StretchToFill, true);
            GUI.color = prev;
        }
    }
}

#endif
