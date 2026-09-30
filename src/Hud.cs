// Hud.cs -- in-game overlay: six chart views cycled with F1, then closed.
// Views: 1 overview (six cards in a row: damage taken / hp lost / distance /
// death grade / position / steamid), 2 death, 3 damage waveform, 4 speed
// waveform, 5 hp loss line, 6 logo (10 s) then closed.
//
// IMGUI (needs no assets), English-only (IMGUI's built-in font has no CJK
// glyphs), everything in try/catch, touches Unity only from OnGUI / Update.
// F1 cycles views (HudBand owns F1 since v2.35), F2/F3 show the diagnostic
// ok/failed panels, F4 the chart cycle, F5 invert, F9 clear stats.

#if HAS_INTEROP

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SotfClientProbe
{
    internal static class Hud
    {
        public static bool Connected;
        public static bool Visible = false;

        private const float CW = 252f;
        private const float CH = 62f;
        private const float OV_CH = 62f;
        private const float OV_GAP = 4f;
        private const int OV_N = 6;
        private const float PAD = 12f;
        private const float OV_CW_MIN = 104f;

        private static float OvCardW()
        {
            try
            {
                float sw = Screen.width;
                if (sw <= 0) sw = 1920f;
                float w = (sw - PAD * 2f - OV_GAP * (OV_N - 1)) / OV_N;
                return w < OV_CW_MIN ? OV_CW_MIN : w;
            }
            catch { return 150f; }
        }

        private const long LogoMs = 10000L;
        private const long AdvanceDebounceMs = 220L;

        private static Color C_FRAME, C_TEXT, C_DIM, C_DOT, C_WAVE, C_BG;
        private static readonly Color A_FRAME = new Color(1.00f, 1.00f, 1.00f, 0.82f);
        private static readonly Color A_TEXT = new Color(1.00f, 1.00f, 1.00f, 1.00f);
        private static readonly Color A_DIM = new Color(0.90f, 0.93f, 0.92f, 1.00f);
        private static readonly Color A_DOT = new Color(0.82f, 0.84f, 0.82f, 0.95f);
        private static readonly Color A_WAVE = new Color(1.00f, 1.00f, 1.00f, 0.95f);
        private static readonly Color A_BG = new Color(0.30f, 0.30f, 0.30f, 0.30f);

        private static readonly Color B_FRAME = new Color(0.04f, 0.05f, 0.04f, 0.90f);
        private static readonly Color B_TEXT = new Color(0.03f, 0.04f, 0.03f, 1.00f);
        private static readonly Color B_DIM = new Color(0.10f, 0.13f, 0.10f, 1.00f);
        private static readonly Color B_DOT = new Color(0.16f, 0.19f, 0.16f, 0.95f);
        private static readonly Color B_WAVE = new Color(0.04f, 0.05f, 0.04f, 1.00f);
        private static readonly Color B_BG = new Color(0.93f, 0.95f, 0.93f, 0.92f);

        private const long InvMs = 500L;
        private const int VIEW_LOGO = 5;
        private const int VIEW_CLOSED = 6;
        private static int _view = VIEW_CLOSED;
        private static long _viewOpenedMs;
        private static long _lastAdvanceMs;
        private static bool _invOn;
        private static bool _invPhase;
        private static long _lastInvMs;
        private static long _lastInvKeyMs;
        private static int _palFor = -1;
        private static Texture2D _white;
        private static Texture2D _logo;
        private static bool _logoTried;
        private static bool _updatePathWorks;

        private static GUIStyle _sTitle, _sBig, _sNum, _sTiny, _sGrade, _sSmall;
        private static bool _okPanel;
        private static bool _badPanel;
        private static GUIStyle _sDiag, _sDiagBad, _sDiagHead;
        private const float DIAG_W = 360f;
        private const float DIAG_LH = 15f;
        private const int DIAG_MAX = 26;
        private static GUIStyle[] _sSmallFit;
        private static readonly int[] SMALL_SIZES = { 13, 12, 11, 10, 9, 8, 7 };
        private static readonly GUIContent _gc = new GUIContent();
        private static Font _mono;
        private static bool _fontTried;
        private static int _fontFor = -1;

        public static void PollKey()
        {
            if (_pollFails <= 3)
            {
                try
                {
                    if (Input.GetKeyDown(KeyCode.F1)) HudBand.Cycle();
                    if (Input.GetKeyDown(KeyCode.F2)) TogglePanel(true);
                    if (Input.GetKeyDown(KeyCode.F3)) TogglePanel(false);
                    if (Input.GetKeyDown(KeyCode.F4)) Advance();
                    if (Input.GetKeyDown(KeyCode.F5)) ToggleInvert();
                    if (Input.GetKeyDown(KeyCode.F9)) HudBand.ClearStats();
                    _updatePathWorks = true;
                }
                catch
                {
                    _pollFails++;
                }
            }

            if (!_updatePathWorks)
            {
                try
                {
                    if (NewKeyDown(0)) HudBand.Cycle();
                    if (NewKeyDown(1)) TogglePanel(true);
                    if (NewKeyDown(2)) TogglePanel(false);
                    if (NewKeyDown(3)) Advance();
                    if (NewKeyDown(4)) ToggleInvert();
                    if (NewKeyDown(5)) HudBand.ClearStats();
                }
                catch { }
            }
        }

        private static readonly string[] NEW_KEY_NAMES =
            { "f1Key", "f2Key", "f3Key", "f4Key", "f5Key", "f9Key" };
        private static bool _newTried;
        private static bool _newResolved;
        private static System.Reflection.PropertyInfo _kbCurrent;
        private static System.Reflection.PropertyInfo[] _newKeys;

        private static bool NewKeyDown(int i)
        {
            if (!_newTried)
            {
                _newTried = true;
                try
                {
                    System.Type t = System.Type.GetType(
                        "UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                    if (t == null)
                        t = System.Type.GetType("UnityEngine.InputSystem.Keyboard");
                    if (t != null)
                    {
                        _kbCurrent = t.GetProperty("current",
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Static);
                        _newKeys = new System.Reflection.PropertyInfo[NEW_KEY_NAMES.Length];
                        for (int k = 0; k < NEW_KEY_NAMES.Length; k++)
                        {
                            _newKeys[k] = t.GetProperty(NEW_KEY_NAMES[k],
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.Instance);
                        }
                        _newResolved = _kbCurrent != null;
                    }
                }
                catch { }
            }
            if (!_newResolved || _newKeys == null || i < 0 || i >= _newKeys.Length) return false;
            if (_newKeys[i] == null) return false;
            try
            {
                object kb = _kbCurrent.GetValue(null);
                if (kb == null) return false;
                object key = _newKeys[i].GetValue(kb);
                if (key == null) return false;
                System.Reflection.PropertyInfo wp = key.GetType().GetProperty(
                    "wasPressedThisFrame",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance);
                if (wp == null) return false;
                object v = wp.GetValue(key);
                return v is bool && (bool)v;
            }
            catch { return false; }
        }

        private static int _pollFails;

        private static void PollKeyGui()
        {
            if (_updatePathWorks || _newResolved) return;
            try
            {
                Event e = Event.current;
                if (e == null) return;
                if (e.type != EventType.KeyDown) return;
                if (e.keyCode == KeyCode.F1) { e.Use(); HudBand.Cycle(); }
                else if (e.keyCode == KeyCode.F2) { e.Use(); TogglePanel(true); }
                else if (e.keyCode == KeyCode.F3) { e.Use(); TogglePanel(false); }
                else if (e.keyCode == KeyCode.F4) { e.Use(); Advance(); }
                else if (e.keyCode == KeyCode.F5) { e.Use(); ToggleInvert(); }
                else if (e.keyCode == KeyCode.F9) { e.Use(); HudBand.ClearStats(); }
            }
            catch { }
        }

        private static void Advance()
        {
            long now = Environment.TickCount64;
            if (_lastAdvanceMs != 0 && now - _lastAdvanceMs < AdvanceDebounceMs) return;
            _lastAdvanceMs = now;

            _view++;
            if (_view > VIEW_CLOSED) _view = 0;
            Visible = (_view != VIEW_CLOSED);
            _viewOpenedMs = now;
        }

        private static void TogglePanel(bool ok)
        {
            long now = Environment.TickCount64;
            if (_lastAdvanceMs != 0 && now - _lastAdvanceMs < AdvanceDebounceMs) return;
            _lastAdvanceMs = now;

            if (ok)
            {
                _okPanel = !_okPanel;
                if (_okPanel) _badPanel = false;
            }
            else
            {
                _badPanel = !_badPanel;
                if (_badPanel) _okPanel = false;
            }
            Visible = _okPanel || _badPanel || (_view != VIEW_CLOSED);
        }

        private static void ToggleInvert()
        {
            long now = Environment.TickCount64;
            if (_lastInvKeyMs != 0 && now - _lastInvKeyMs < AdvanceDebounceMs) return;
            _lastInvKeyMs = now;

            _invOn = !_invOn;
            _invPhase = false;
            _lastInvMs = now;
            if (_invOn && !Visible)
            {
                _view = 0;
                _viewOpenedMs = now;
                Visible = true;
            }
        }

        public static void StopAuto()
        {
            _invOn = false;
            _invPhase = false;
        }

        private static void InvTick()
        {
            if (!_invOn) return;
            long now = Environment.TickCount64;
            if (now - _lastInvMs < InvMs) return;
            _lastInvMs = now;
            _invPhase = !_invPhase;
        }

        public static void Draw(bool requireServer, int fontSize)
        {
            try
            {
                PollKeyGui();
                InvTick();

                try { HudBand.Draw(); } catch { }

                if (!Visible) return;
                if (_view == VIEW_CLOSED && !_okPanel && !_badPanel) return;
                if (requireServer && !Connected) return;

                Event ev = Event.current;
                if (ev != null && ev.type != EventType.Repaint) return;

                EnsureResources(fontSize);
                SyncPalette();

                if (_view == VIEW_LOGO && _viewOpenedMs != 0 &&
                    Environment.TickCount64 - _viewOpenedMs > LogoMs)
                {
                    Visible = false;
                    return;
                }

                float x = PAD, y = PAD;

                if (_okPanel) { DrawDiag(true, x, y); return; }
                if (_badPanel) { DrawDiag(false, x, y); return; }

                if (_view == 0)
                    Backdrop(x, y, OvCardW() * OV_N + OV_GAP * (OV_N - 1), OV_CH);
                else
                    Backdrop(x, y, CW, CH);

                switch (_view)
                {
                    case 0: DrawOverview(x, y); break;
                    case 1: DrawDeath(x, y, CW, CH); break;
                    case 2: DrawHits(x, y, CW, CH); break;
                    case 3: DrawSpeed(x, y, CW, CH); break;
                    case 4: DrawHploss(x, y, CW, CH); break;
                    default: DrawLogo(x, y, CW, CH); break;
                }
            }
            catch { }
        }

        private static void DrawDiag(bool ok, float x, float y)
        {
            var items = ok ? Diag.Succeeded() : Diag.Failed();
            int n = items == null ? 0 : items.Count;
            int shown = n > DIAG_MAX ? DIAG_MAX : n;

            float h = 24f + (shown > 0 ? shown : 1) * DIAG_LH + 8f;
            Backdrop(x, y, DIAG_W, h);

            string head = ok
                ? "PROBE OK (" + Diag.CountOk().ToString(CultureInfo.InvariantCulture) + ")  F1"
                : "PROBE FAILED (" + Diag.CountBad().ToString(CultureInfo.InvariantCulture) + ")  F2";

            if (_sDiagHead != null)
                GUI.Label(new Rect(x + 6f, y + 3f, DIAG_W - 12f, 18f), head, _sDiagHead);

            if (n == 0)
            {
                GUI.Label(new Rect(x + 8f, y + 24f, DIAG_W - 16f, DIAG_LH),
                    ok ? "(nothing confirmed yet)" : "(no failures)",
                    _sTiny != null ? _sTiny : _sDiag);
                return;
            }

            GUIStyle face = _sDiag;
            if (!ok && _sDiagBad != null) face = _sDiagBad;

            for (int i = 0; i < shown; i++)
            {
                GUI.Label(new Rect(x + 8f, y + 24f + i * DIAG_LH, DIAG_W - 16f, DIAG_LH),
                    Diag.Line(items[i]), face);
            }

            if (n > shown)
            {
                GUI.Label(new Rect(x + 8f, y + 24f + shown * DIAG_LH, DIAG_W - 16f, DIAG_LH),
                    "... and " + (n - shown).ToString(CultureInfo.InvariantCulture) +
                    " more (see probe_diag.txt)", face);
            }
        }

        private static void DrawOverview(float x, float y)
        {
            float cw = OvCardW();
            float cx = x;

            Card(cx, y, cw, OV_CH, "DAMAGE TAKEN",
                Stats.Hits.ToString(CultureInfo.InvariantCulture), "");
            cx += cw + OV_GAP;

            Card(cx, y, cw, OV_CH, "HP LOST", Stats.Fmt(Stats.HpLoss, 1), "");
            cx += cw + OV_GAP;

            Card(cx, y, cw, OV_CH, "DISTANCE", Km(Stats.Distance), "KM");
            cx += cw + OV_GAP;

            Color gc;
            string grade = Grade(DeathProbe.Deaths, out gc);
            string body = DeathProbe.Source == "none"
                ? "--"
                : grade + " - " + Two(DeathProbe.Deaths);
            Card(cx, y, cw, OV_CH, "DEATH", body, "", gc);
            cx += cw + OV_GAP;

            string pos = "--";
            if (Stats.HavePos)
            {
                string fmt = (Mathf.Abs(Stats.X) > 9999f ||
                              Mathf.Abs(Stats.Z) > 9999f ||
                              cw < 110f) ? "F0" : "F1";
                pos = Stats.X.ToString(fmt, CultureInfo.InvariantCulture) + "," +
                      Stats.Y.ToString(fmt, CultureInfo.InvariantCulture) + "," +
                      Stats.Z.ToString(fmt, CultureInfo.InvariantCulture);
            }
            CardSmall(cx, y, cw, OV_CH, "POSITION", pos);
            cx += cw + OV_GAP;

            ulong sid = Identity.SteamId;
            CardSmall(cx, y, cw, OV_CH, "STEAMID",
                sid != 0 ? sid.ToString(CultureInfo.InvariantCulture) : "--");
        }

        private static void CardSmall(float x, float y, float w, float h,
                                      string title, string value)
        {
            Frame(x, y, w, h, C_FRAME);
            GUI.Label(new Rect(x + 4f, y + 2f, w - 8f, 15f), title, _sTitle);

            Color save = _sSmall.normal.textColor;
            _sSmall.normal.textColor = C_TEXT;

            GUIStyle face = _sSmall;
            float avail = w - 6f;
            if (_sSmallFit != null && value != null && value.Length > 0)
            {
                _gc.text = value;
                for (int i = 0; i < _sSmallFit.Length; i++)
                {
                    GUIStyle s = _sSmallFit[i];
                    if (s == null) continue;
                    try
                    {
                        if (s.CalcSize(_gc).x <= avail) { face = s; break; }
                    }
                    catch { }
                    face = s;
                }
            }

            Color fsave = face.normal.textColor;
            face.normal.textColor = C_TEXT;
            GUI.Label(new Rect(x + 3f, y + 18f, w - 6f, h - 21f), value, face);
            face.normal.textColor = fsave;
            _sSmall.normal.textColor = save;
        }

        private static void Card(float x, float y, float w, float h,
                                 string title, string value, string unit)
        {
            Card(x, y, w, h, title, value, unit, C_TEXT);
        }

        private static void Card(float x, float y, float w, float h,
                                 string title, string value, string unit, Color vcol)
        {
            Frame(x, y, w, h, C_FRAME);
            GUI.Label(new Rect(x + 4f, y + 2f, w - 8f, 15f), title, _sTitle);

            float vy = y + 18f;
            float vh = h - 21f;
            if (unit != null && unit.Length > 0)
            {
                float uw = 26f;
                GUI.Label(new Rect(x + w - uw - 4f, vy + vh * 0.38f, uw, 13f), unit, _sTiny);
            }

            Color save = _sNum.normal.textColor;
            _sNum.normal.textColor = vcol;
            GUI.Label(new Rect(x + 4f, vy, w - 8f, vh), value, _sNum);
            _sNum.normal.textColor = save;
        }

        private static void DrawDeath(float x, float y, float w, float h)
        {
            Frame(x, y, w, h, C_FRAME);
            GUI.Label(new Rect(x, y + 2f, w, 15f), "DEATH", _sTitle);

            Color gc;
            string grade = Grade(DeathProbe.Deaths, out gc);
            string body = DeathProbe.Source == "none"
                ? "--"
                : grade + " - " + Two(DeathProbe.Deaths);

            Color save = _sGrade.normal.textColor;
            _sGrade.normal.textColor = gc;
            GUI.Label(new Rect(x, y + 16f, w, h - 18f), body, _sGrade);
            _sGrade.normal.textColor = save;
        }

        private static string Grade(int deaths, out Color c)
        {
            return HudBand.Grade(deaths, out c);
        }

        private static string Two(int v)
        {
            if (v < 0) v = 0;
            if (v > 99) v = 99;
            return v.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string Km(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres)) return "--";
            return (metres / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
        }

        private static void DrawHits(float x, float y, float w, float h)
        {
            Frame(x, y, w, h, C_FRAME);

            GUI.Label(new Rect(x + 6f, y + 2f, 200f, 15f), "DAMAGE TAKEN", _sTitle);
            GUI.Label(new Rect(x + 6f, y + 16f, 120f, 12f),
                "HITS " + Stats.Hits.ToString(CultureInfo.InvariantCulture), _sTiny);
            GUI.Label(new Rect(x + w - 96f, y + 16f, 90f, 12f),
                "TOTAL " + Stats.Fmt(Stats.HpLoss, 1), _sTiny);

            float px = x + 30f, py = y + 26f;
            float pw = w - 38f, ph = h - 30f;

            List<Pt> pts = Series.Thin(Series.Hits, Series.MaxPoints, 0);
            if (pts == null || pts.Count == 0)
            {
                NoData(px, py, pw, ph);
                return;
            }

            float span = pts[pts.Count - 1].T - pts[0].T;
            if (span <= 0f) span = 1000f;

            float vmax = 1f;
            for (int i = 0; i < pts.Count; i++) if (pts[i].V > vmax) vmax = pts[i].V;
            vmax *= 1.15f;

            Fill(px, py + ph - 1f, pw, 1f, C_DIM);

            float half = pts.Count > 1
                ? Mathf.Min(5f, (pw / (float)pts.Count) * 0.35f)
                : 5f;
            if (half < 1.2f) half = 1.2f;

            float lastLabel = -999f;
            for (int i = 0; i < pts.Count; i++)
            {
                float cx = px + ((pts[i].T - pts[0].T) / span) * pw;
                float top = py + ph - (pts[i].V / vmax) * ph;

                Seg(cx - half, py + ph, cx, top, 1.5f, C_WAVE);
                Seg(cx, top, cx + half, py + ph, 1.5f, C_WAVE);
                Fill(cx - 1.2f, top - 1.2f, 2.4f, 2.4f, C_DOT);

                if (pts[i].V >= vmax * 0.45f && cx - lastLabel > 26f)
                {
                    GUI.Label(new Rect(cx - 20f, top - 13f, 40f, 12f),
                        Stats.Fmt(pts[i].V, 0), _sTiny);
                    lastLabel = cx;
                }
            }

            GUI.Label(new Rect(x + 2f, py - 4f, 26f, 12f),
                Stats.Fmt(vmax / 1.15f, 0), _sTiny);
        }

        private static void DrawSpeed(float x, float y, float w, float h)
        {
            Frame(x, y, w, h, C_FRAME);

            GUI.Label(new Rect(x, y + 1f, w, 21f), Km(Stats.Distance), _sBig);
            GUI.Label(new Rect(x + 6f, y + 21f, 90f, 15f), "SPEED", _sTitle);
            GUI.Label(new Rect(x + w - 100f, y + 22f, 94f, 13f),
                "IDLE " + FmtDur(Series.IdleSec), _sTiny);

            float px = x + 32f, py = y + 34f;
            float pw = w - 40f, ph = h - 38f;

            List<Pt> pts = Series.Thin(Series.Speed, Series.MaxPoints, 0);
            if (pts == null || pts.Count < 2)
            {
                NoData(px, py, pw, ph);
                return;
            }

            float vmax = Series.SpeedMax * 1.15f;
            if (vmax < 5f) vmax = 5f;
            vmax = CeilNice(vmax);

            Axis(px, py, pw, ph, vmax);

            float span = pts[pts.Count - 1].T - pts[0].T;
            if (span <= 0f) span = 1000f;

            float prevX = 0f, prevY = 0f;
            bool have = false;
            for (int i = 0; i < pts.Count; i++)
            {
                float cx = px + ((pts[i].T - pts[0].T) / span) * pw;
                float cy = py + ph - (pts[i].V / vmax) * ph;
                if (cy > py + ph) cy = py + ph;
                if (cy < py) cy = py;

                if (have) Seg(prevX, prevY, cx, cy, 1.5f, C_WAVE);
                Fill(cx - 1.2f, cy - 1.2f, 2.4f, 2.4f, C_DOT);

                prevX = cx; prevY = cy; have = true;
            }
        }

        private static float CeilNice(float v)
        {
            if (v <= 5f) return 5f;
            if (v <= 10f) return 10f;
            if (v <= 15f) return 15f;
            if (v <= 20f) return 20f;
            if (v <= 30f) return 30f;
            float step = 10f;
            float r = v;
            while (r > step * 4f) step *= 2f;
            return Mathf.Ceil(v / step) * step;
        }

        private static void Axis(float x, float y, float w, float h, float vmax)
        {
            Fill(x, y + h, w, 1f, C_DIM);
            GUI.Label(new Rect(x - 30f, y - 4f, 28f, 12f), Stats.Fmt(vmax, 0), _sTiny);
            GUI.Label(new Rect(x - 30f, y + h - 10f, 28f, 12f), "0", _sTiny);
            GUI.Label(new Rect(x - 30f, y + h - 22f, 28f, 12f), "KM/H", _sTiny);
        }

        private static void DrawHploss(float x, float y, float w, float h)
        {
            Frame(x, y, w, h, C_FRAME);

            GUI.Label(new Rect(x + 6f, y + 2f, 160f, 15f), "HP LOSS", _sTitle);
            GUI.Label(new Rect(x + w - 110f, y + 3f, 104f, 14f),
                Stats.Fmt(Stats.HpLoss, 1) + " TOTAL", _sTiny);

            float px = x + 32f, py = y + 22f;
            float pw = w - 40f, ph = h - 28f;

            List<Pt> pts = Series.Thin(Series.Loss, Series.MaxPoints, 1);
            if (pts == null || pts.Count < 2)
            {
                NoData(px, py, pw, ph);
                return;
            }

            float vmax = Stats.HpLoss;
            if (vmax <= 0f) vmax = 1f;
            vmax *= 1.15f;

            Axis(px, py, pw, ph, vmax);

            float span = pts[pts.Count - 1].T - pts[0].T;
            if (span <= 0f) span = 1000f;

            float prevX = 0f, prevY = 0f;
            bool have = false;
            for (int i = 0; i < pts.Count; i++)
            {
                float cx = px + ((pts[i].T - pts[0].T) / span) * pw;
                float cy = py + ph - (pts[i].V / vmax) * ph;
                if (cy > py + ph) cy = py + ph;
                if (cy < py) cy = py;

                if (have)
                {
                    Seg(prevX, prevY, cx, prevY, 1.4f, C_WAVE);
                    Seg(cx, prevY, cx, cy, 1.4f, C_WAVE);
                }
                Fill(cx - 1.2f, cy - 1.2f, 2.4f, 2.4f, C_DOT);

                prevX = cx; prevY = cy; have = true;
            }
        }

        private static void DrawLogo(float x, float y, float w, float h)
        {
            Frame(x, y, w, h, C_FRAME);

            EnsureLogo();
            if (_logo != null)
            {
                Color save = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(x, y, w, h), _logo,
                    ScaleMode.ScaleAndCrop, true, 0f);
                GUI.color = save;
            }
            else
            {
                GUI.Label(new Rect(x, y + h * 0.32f, w, 24f), "GUEIFU LAOBAI", _sNum);
            }

            long left = LogoMs - (Environment.TickCount64 - _viewOpenedMs);
            if (left < 0) left = 0;
            GUI.Label(new Rect(x + 5f, y + 3f, 56f, 14f),
                (left / 1000L).ToString(CultureInfo.InvariantCulture) + "s", _sTitle);
        }

        private static void NoData(float x, float y, float w, float h)
        {
            GUI.Label(new Rect(x, y + h * 0.4f, w, 14f), "NO DATA", _sTiny);
        }

        private static string FmtDur(double sec)
        {
            if (double.IsNaN(sec) || sec < 0) sec = 0;
            int s = (int)sec;
            int m = s / 60;
            int r = s % 60;
            return m.ToString(CultureInfo.InvariantCulture) + "m" +
                   r.ToString("00", CultureInfo.InvariantCulture) + "s";
        }

        private static void Frame(float x, float y, float w, float h, Color c)
        {
            Fill(x, y, w, 1f, c);
            Fill(x, y + h - 1f, w, 1f, c);
            Fill(x, y, 1f, h, c);
            Fill(x + w - 1f, y, 1f, h, c);
        }

        private static void Fill(float x, float y, float w, float h, Color c)
        {
            if (_white == null) return;
            if (w <= 0f || h <= 0f) return;
            Color save = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(x, y, w, h), _white,
                ScaleMode.StretchToFill, true, 0f);
            GUI.color = save;
        }

        private static void Seg(float x1, float y1, float x2, float y2, float w, Color c)
        {
            if (_white == null) return;
            float dx = x2 - x1, dy = y2 - y1;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.4f) return;

            float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
            Matrix4x4 save = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, new Vector2(x1, y1));
            Fill(x1, y1 - w * 0.5f, len, w, c);
            GUI.matrix = save;
        }

        private static void EnsureResources(int fontSize)
        {
            if (_white == null)
            {
                try
                {
                    _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    _white.SetPixel(0, 0, Color.white);
                    _white.Apply(false, false);
                    _white.hideFlags = HideFlags.HideAndDontSave;
                    _white.filterMode = FilterMode.Point;
                }
                catch { _white = null; }
            }

            if (_fontFor == fontSize && _sTitle != null) return;
            _fontFor = fontSize;
            ApplyTheme(_invPhase);
            BuildStyles(fontSize);
            _palFor = -1;
        }

        private static void BuildStyles(int fontSize)
        {
            try
            {
                if (!_fontTried)
                {
                    _fontTried = true;
                    string[] want = { "Consolas", "Lucida Console", "Courier New" };
                    foreach (string n in want)
                    {
                        try
                        {
                            Font f = Font.CreateDynamicFontFromOSFont(n, fontSize);
                            if (f != null) { _mono = f; break; }
                        }
                        catch { }
                    }
                }

                _sTitle = Make(fontSize <= 12 ? 13 : 14, C_DIM, TextAnchor.UpperCenter);
                _sTitle.fontStyle = FontStyle.Bold;
                _sTiny = Make(10, C_DIM, TextAnchor.UpperCenter);
                _sNum = Make(34, C_TEXT, TextAnchor.MiddleLeft);
                _sBig = Make(22, C_TEXT, TextAnchor.UpperCenter);

                int gradePx = (int)((CH - 18f) / 1.2f);
                if (gradePx > 56) gradePx = 56;
                if (gradePx < 12) gradePx = 12;
                _sGrade = Make(gradePx, C_TEXT, TextAnchor.MiddleCenter);

                _sSmall = Make(13, C_TEXT, TextAnchor.MiddleCenter);

                _sSmallFit = new GUIStyle[SMALL_SIZES.Length];
                for (int i = 0; i < SMALL_SIZES.Length; i++)
                    _sSmallFit[i] = Make(SMALL_SIZES[i], C_TEXT, TextAnchor.MiddleCenter);

                _sDiag = Make(11, C_TEXT, TextAnchor.MiddleLeft);
                _sDiagBad = Make(11, new Color(1f, 0.36f, 0.30f), TextAnchor.MiddleLeft);
                _sDiagHead = Make(12, C_DIM, TextAnchor.MiddleLeft);
                _sDiagHead.fontStyle = FontStyle.Bold;
            }
            catch
            {
                try
                {
                    _sTitle = new GUIStyle(GUI.skin.label);
                    _sNum = new GUIStyle(GUI.skin.label);
                    _sBig = new GUIStyle(GUI.skin.label);
                    _sGrade = new GUIStyle(GUI.skin.label);
                    _sTiny = new GUIStyle(GUI.skin.label);
                    _sSmall = new GUIStyle(GUI.skin.label);
                    _sDiag = new GUIStyle(GUI.skin.label);
                    _sDiagBad = new GUIStyle(GUI.skin.label);
                    _sDiagHead = new GUIStyle(GUI.skin.label);
                }
                catch { }
            }
        }

        private static GUIStyle Make(int size, Color c, TextAnchor anchor)
        {
            GUIStyle s;
            try { s = new GUIStyle(GUI.skin.label); }
            catch { s = new GUIStyle(); }

            s.fontSize = size;
            s.font = _mono;
            s.normal.textColor = c;
            s.alignment = anchor;
            s.wordWrap = false;
            s.clipping = TextClipping.Overflow;
            s.richText = false;
            return s;
        }

        private static void ApplyTheme(bool inv)
        {
            C_FRAME = inv ? B_FRAME : A_FRAME;
            C_TEXT = inv ? B_TEXT : A_TEXT;
            C_DIM = inv ? B_DIM : A_DIM;
            C_DOT = inv ? B_DOT : A_DOT;
            C_WAVE = inv ? B_WAVE : A_WAVE;
            C_BG = inv ? B_BG : A_BG;
        }

        private static void SyncPalette()
        {
            int want = _invPhase ? 1 : 0;
            if (_palFor == want) return;
            _palFor = want;

            ApplyTheme(_invPhase);

            if (_sTitle != null) _sTitle.normal.textColor = C_DIM;
            if (_sTiny != null) _sTiny.normal.textColor = C_DIM;
            if (_sNum != null) _sNum.normal.textColor = C_TEXT;
            if (_sBig != null) _sBig.normal.textColor = C_TEXT;
            if (_sGrade != null) _sGrade.normal.textColor = C_TEXT;
            if (_sSmall != null) _sSmall.normal.textColor = C_TEXT;
            if (_sDiag != null) _sDiag.normal.textColor = C_TEXT;
            if (_sDiagHead != null) _sDiagHead.normal.textColor = C_DIM;
        }

        private static void Backdrop(float x, float y, float w, float h)
        {
            Fill(x, y, w, h, C_BG);
        }

        private static void EnsureLogo()
        {
            if (_logoTried) return;
            _logoTried = true;
            try
            {
                byte[] buf = null;
                System.Reflection.Assembly asm = typeof(Hud).Assembly;
                System.IO.Stream s = asm.GetManifestResourceStream("SotfClientProbe.logo.png");
                if (s != null)
                {
                    using (s)
                    {
                        buf = new byte[s.Length];
                        int off = 0;
                        while (off < buf.Length)
                        {
                            int n = s.Read(buf, off, buf.Length - off);
                            if (n <= 0) break;
                            off += n;
                        }
                    }
                }
                if (buf == null || buf.Length == 0) return;

                Type ty = Type.GetType(
                    "UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                if (ty == null)
                    ty = Type.GetType("UnityEngine.ImageConversion, UnityEngine.CoreModule");
                if (ty == null) return;

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                object[] a2 = { tex, buf };
                object[] a3 = { tex, buf, false };

                System.Reflection.MethodInfo mi =
                    ty.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]) });
                object[] use = a2;
                if (mi == null)
                {
                    mi = ty.GetMethod("LoadImage",
                        new Type[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
                    use = a3;
                }
                if (mi == null) return;

                object ok = mi.Invoke(null, use);
                if (ok is bool b && !b) return;

                tex.hideFlags = HideFlags.HideAndDontSave;
                tex.filterMode = FilterMode.Bilinear;
                _logo = tex;
            }
            catch
            {
                _logo = null;
            }
        }
    }
}

#endif
