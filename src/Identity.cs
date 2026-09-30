// Identity.cs -- who am I?  Three independent SteamID routes.
//
// Route A (trusted first): the game writes one folder per Steam account under
//     %USERPROFILE%\AppData\LocalLow\Endnight\SonsOfTheForest\Saves\<steamid64>\
// A plain filesystem read: no Steamworks binding, no P/Invoke, no Unity objects.
// Route B (in-process Steamworks reflection) is DISABLED: Steamworks reflection
// triggered Steam client IPC which, stacked on the game's own Steam API calls,
// hit the rate limiter and the account was temporarily banned (36h). Method
// body retained for a future re-enable. Route C checks whether steam_api64.dll
// is even loaded, so a failed route B would be expected rather than alarming.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SotfClientProbe
{
    internal static class Identity
    {
        public static ulong SteamId;
        public static string Route;
        public static string Report;

        private static readonly StringBuilder _sb = new StringBuilder();

        public static void Probe()
        {
            _sb.Length = 0;
            SteamId = 0;
            Route = "none";

            Line("=== SotF Client Probe -- identity report ===");
            Line("time     : " + U.Now());
            Line("machine  : " + Environment.MachineName);
            Line("user     : " + Environment.UserName);
            Line("");

            RouteA_SaveFolder();
            // RouteB_SteamworksReflection();   // DISABLED: Steam API rate-limit ban
            RouteC_SteamModule();

            if (SteamId == 0)
            {
                Line("[FALLBACK] RouteA (save-folder) failed and RouteB (Steamworks) is disabled.");
                Line("           No SteamID resolution will be attempted. Data will be keyed as anonymous.");
            }

            Line("");
            Line("---------------------------------------------");
            if (SteamId != 0)
            {
                Line("RESULT   : steamid64 = " + SteamId.ToString(CultureInfo.InvariantCulture));
                Line("ROUTE    : " + Route);
                Line("VERDICT  : identity OK -- events can be keyed by player");
            }
            else
            {
                Line("RESULT   : no SteamID found");
                Line("VERDICT  : identity FAILED -- see which route failed above");
            }

            Report = _sb.ToString();
        }

        private static void Line(string s)
        {
            _sb.Append(s).Append('\n');
        }

        private static void RouteA_SaveFolder()
        {
            Line("[A] save-folder scan (filesystem only -- cannot crash the game)");
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "Endnight", "SonsOfTheForest", "Saves");

                Line("    path    : " + root);
                if (!Directory.Exists(root))
                {
                    Line("    result  : MISSING -- is this a Steam install? (Store builds use another path)");
                    return;
                }

                var dirs = Directory.GetDirectories(root);
                Line("    entries : " + dirs.Length);

                var hits = new List<KeyValuePair<ulong, DateTime>>();
                foreach (string d in dirs)
                {
                    string name = Path.GetFileName(d);
                    ulong id;
                    if (!U.LooksLikeSteamId64(name, out id))
                    {
                        Line("      skip  : " + name + " (not a 17-digit 7656... id)");
                        continue;
                    }
                    DateTime wt;
                    try { wt = Directory.GetLastWriteTime(d); }
                    catch { wt = DateTime.MinValue; }
                    hits.Add(new KeyValuePair<ulong, DateTime>(id, wt));
                    Line("      HIT   : " + id.ToString(CultureInfo.InvariantCulture) +
                         "   lastwrite=" + wt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                }

                if (hits.Count == 0)
                {
                    Line("    result  : no SteamID-shaped folder (single-player-only install?)");
                    return;
                }

                var best = hits.OrderByDescending(h => h.Value).First();
                SteamId = best.Key;
                Route = "A:save-folder";
                if (hits.Count > 1)
                {
                    Line("    note    : " + hits.Count + " accounts on this PC -- took the most recent.");
                    Line("              If the wrong one is picked, delete the other folder or use a separate Windows account.");
                }
                Line("    result  : SteamID64 = " + SteamId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                Line("    ERROR   : " + U.Short(e));
            }
            Line("");
        }

        private static void RouteB_SteamworksReflection()
        {
            Line("[B] in-process Steamworks reflection (static members only, value types only)");
            try
            {
                string[] named =
                {
                    "Steamworks.SteamUser, Steamworks.NET",
                    "Steamworks.SteamUser",
                    "Steamworks.SteamClient, Facepunch.Steamworks",
                    "Steamworks.SteamClient",
                    "Steamworks.SteamworksClient",
                };

                foreach (string n in named)
                {
                    Type t = Type.GetType(n, false);
                    if (t == null)
                    {
                        Line("    type    : " + n + "  -> not found");
                        continue;
                    }
                    Line("    type    : " + t.FullName + "  -> FOUND");

                    string[] methods = { "GetSteamID" };
                    foreach (string mn in methods)
                    {
                        MethodInfo m;
                        try { m = t.GetMethod(mn, U.BF, null, Type.EmptyTypes, null); }
                        catch { m = null; }
                        if (m == null) continue;
                        try
                        {
                            object raw = m.Invoke(null, null);
                            if (raw != null)
                            {
                                Line("      call  : " + mn + "() -> " + U.TypeName(raw));
                                object inner;
                                if (U.TryGet(raw, "m_SteamID", out inner, out _) ||
                                    U.TryGet(raw, "Value", out inner, out _))
                                {
                                    ulong id;
                                    if (U.LooksLikeSteamId64(inner, out id))
                                    {
                                        Line("      HIT   : " + id.ToString(CultureInfo.InvariantCulture));
                                        if (SteamId == 0) { SteamId = id; Route = "B:steamworks." + t.Name; }
                                        else if (id != SteamId)
                                            Line("      NOTE  : differs from route A (" + SteamId + ") -- investigate");
                                    }
                                }
                            }
                        }
                        catch (Exception e) { Line("      call  : " + mn + " threw " + U.Short(e)); }
                    }

                    SweepStatics(t);
                }

                int swept = 0;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (Type t in types)
                    {
                        if (t == null || t.FullName == null) continue;
                        string fn = t.FullName;
                        bool steamish =
                            fn.IndexOf("Steamworks", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            fn.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!steamish) continue;
                        if (fn.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (++swept > 12) break;
                        SweepStatics(t);
                    }
                    if (swept > 12) break;
                }
                Line("    swept   : " + swept + " steam-named types (capped at 12)");
            }
            catch (Exception e)
            {
                Line("    ERROR   : " + U.Short(e));
            }
            Line("");
        }

        private static void SweepStatics(Type t)
        {
            if (t == null) return;
            try
            {
                foreach (PropertyInfo p in t.GetProperties(U.BF))
                {
                    if (p == null || !p.CanRead || p.GetIndexParameters().Length != 0) continue;
                    if (!U.IsSafeValue(p.PropertyType)) continue;
                    object v;
                    try { v = p.GetValue(null, null); }
                    catch { continue; }
                    ulong id;
                    if (U.LooksLikeSteamId64(v, out id))
                    {
                        Line("      HIT   : static " + t.Name + "." + p.Name +
                             " = " + id.ToString(CultureInfo.InvariantCulture));
                        if (SteamId == 0) { SteamId = id; Route = "B:staticsweep." + t.Name + "." + p.Name; }
                    }
                }
                foreach (FieldInfo f in t.GetFields(U.BF))
                {
                    if (f == null || !U.IsSafeValue(f.FieldType)) continue;
                    object v;
                    try { v = f.GetValue(null); }
                    catch { continue; }
                    ulong id;
                    if (U.LooksLikeSteamId64(v, out id))
                    {
                        Line("      HIT   : static " + t.Name + "." + f.Name +
                             " = " + id.ToString(CultureInfo.InvariantCulture));
                        if (SteamId == 0) { SteamId = id; Route = "B:staticsweep." + t.Name + "." + f.Name; }
                    }
                }
            }
            catch { }
        }

        private static void RouteC_SteamModule()
        {
            Line("[C] loaded-module check (is Steam present in this process?)");
            try
            {
                Process p = Process.GetCurrentProcess();
                bool any = false;
                foreach (ProcessModule m in p.Modules)
                {
                    string mn;
                    try { mn = m.ModuleName; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(mn)) continue;
                    if (mn.IndexOf("steam", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    any = true;
                    Line("    module  : " + mn);
                }
                if (!any) Line("    result  : no steam* module loaded");
                Line("    process : " + p.ProcessName + " (pid " + p.Id + ")");
            }
            catch (Exception e)
            {
                Line("    ERROR   : " + U.Short(e));
            }
            Line("");
        }
    }
}
