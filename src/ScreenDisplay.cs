// ScreenDisplay.cs -- discover the game's video players and record their
// position + component layout once. Positional info is used by the media
// centre pipeline (cinema / VIP screens) to pick the right player for the
// right screen. This file only SCANS and REPORTS; playback is handled by the
// dedicated media module.

using BepInEx;
using System;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

namespace SotfClientProbe
{
    public static class ScreenDisplay
    {
        private static string _state = "";
        private static bool _found = false;

        public static void Tick()
        {
            if (_found) return;

            try
            {
                var vps = UnityEngine.Object.FindObjectsOfType<VideoPlayer>();
                if (vps == null || vps.Length == 0) return;

                VitalsProbe.SamplePosition(out float px, out float py, out float pz);
                Vector3 myPos = new Vector3(px, py, pz);
                VideoPlayer firstVp = null;
                float bestDist = 10f;

                foreach (var vp in vps)
                {
                    if (vp == null) continue;
                    Vector3 p;
                    if (!Pos(vp, out p)) continue;

                    float dist = Vector3.Distance(myPos, p);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        firstVp = vp;
                    }
                }

                if (firstVp != null)
                {
                    _found = true;
                    _state = "found videoplayer at " + bestDist.ToString("F1") + "m";

                    // Record the VideoPlayer's transform hierarchy for the
                    // media centre pipeline (screen registry).
                    var sb = new System.Text.StringBuilder();
                    sb.Append("state: ").Append(_state).Append("\n");
                    Transform t = firstVp.transform;
                    for (int i = 0; i < 5; i++)
                    {
                        if (t == null) break;
                        sb.Append("level ").Append(i).Append(": ").Append(t.gameObject.name).Append("\n");
                        t = t.parent;
                    }
                    SaveStatus(sb.ToString());
                }
            }
            catch (Exception e)
            {
                _state = "error: " + e.Message;
            }
        }

        private static bool Pos(VideoPlayer vp, out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                if (vp == null) return false;
                pos = vp.transform.position;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SaveStatus(string extra = "")
        {
            try
            {
                string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "probe_out", "screen_display_status.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, extra);
            }
            catch { }
        }
    }
}
