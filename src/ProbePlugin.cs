// ProbePlugin.cs -- BepInEx 6 (IL2CPP) entry point for SotF Client Probe v1.0
//
// WHAT THIS MOD IS FOR
// The server-side collector cannot see real player health: the Vitals
// component on a dedicated server is a frozen placeholder (176/176 forever,
// confirmed with two players on the same box reading the identical dead
// value). This mod runs in the CLIENT, where the real numbers live, and
// answers three questions that decide whether the whole three-component
// architecture is viable:
//
//   1. identity  -- can we read the player's SteamID64?
//   2. vitals    -- can we read REAL, moving health?
//   3. network   -- can we tell which server we are on?
//
// If (2) comes back "no", the architecture has no foundation and should be
// abandoned rather than iterated on. That is why this first version is a
// probe and not a collector.
//
// THREADING RULE (this is what crashed v3.23 server-side)
// UnityEngine.Object may only be touched on the Unity main thread. v3.23
// walked gameObject->transform->parent from a background thread and took the
// server down with a NATIVE crash -- the try/catch did nothing because it was
// not a managed exception. Therefore: all game-object access happens inside
// the Harmony postfix below, which runs on the main thread. Load() only does
// config, files, and plain .NET reflection.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
#if HAS_INTEROP
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
#endif

namespace SotfClientProbe
{
    [BepInPlugin(GUID, NAME, BEP_VERSION)]
    public class ProbePlugin : BasePlugin
    {
        public const string GUID = "sotf.clientprobe";
        public const string NAME = "SotF Client Probe";
        public const string VERSION = "A1.07";
        /// <summary>
        /// 给 BepInPlugin 特性用的版本号，必须是 System.Version 可解析的纯数字格式
        /// （major.minor[.build[.revision]]）。
        /// A1 系列把显示版本改成 "A1.03" 这类带字母的串后，直接塞进 BepInPlugin
        /// 会让特性构造时抛 FormatException，BepInEx 静默跳过整个插件，
        /// 日志表现为 "0 plugins to load" 且无任何报错。
        /// 显示仍用 VERSION，此处只给 BepInEx 用。
        /// </summary>
        public const string BEP_VERSION = "1.7.0";
        /// <summary>
        /// A1：部署校验用的唯一标记。
        /// 版本号 "A1" 太短，在 DLL 二进制里随手就能撞上，
        /// 拿它当"版本是否编进去"的判据等于没判。用这个长串才 unambiguous。
        /// </summary>
        public static readonly string BUILD_MARK = "SOTFPROBE-A1.07-MARK";

        internal static ConfigEntry<int> CfgIntervalMs;
        internal static ConfigEntry<bool> CfgEnabled;

        // ---- v2.6 in-game overlay ----
        internal static ConfigEntry<bool> CfgHud;
        internal static ConfigEntry<bool> CfgHudRequireServer;
        internal static ConfigEntry<int> CfgHudFontSize;
        internal static ConfigEntry<int> CfgHudRefreshMs;

        private static bool _discovered;
        private static long _lastTickMs;
        private static long _lastGuardMs;
        // v2.29: throttle for the player-info rediscovery below.
        private static long _lastPinfoDiscoverMs;
        private static bool _serverResolved;
        private static long _lastFlushMs;
        private static int _tick;
        private static int _hpReads;
        private static int _hpChanges;
        private static float _lastHp = float.NaN;
        private static bool _hooked;
        private static bool _invReportWritten;
        // v2.26: was a write-once bool. A gear report that says "FAILED:
        // LocalPlayer is null" would then be permanent -- the file was written
        // on the main menu and never revisited once the session started, so
        // the one thing worth reporting (the failure) hid the recovery.
        // Remembering the text instead lets a later, better report overwrite
        // an earlier, worse one.
        private static string _gearReportLast;
        private static string _worldReportLast;
        private static bool _hudAvailable;
        private static bool _deathReportWritten;

        /// v2.9: how long a session must be live before anything is collected.
        /// 30 s is deliberately longer than the load-in and spawn settle, and
        /// short enough that a normal round still produces a usable trace.
        private const long ArmDelayMs = 30000L;

        /// Tick timestamp of the first tick where a server endpoint was known.
        private static long _serverSinceMs;
        private static bool _armedLogged;

        /// v2.9: which endpoint the current totals belong to.
        private static string _serverKey = "";
        private static long _sessionStartMs;

        /// v2.9: bank the last session on the way out. Without this, quitting
        /// straight to desktop loses totals that only ever existed in memory.
        /// v2.12: that banking now happens in ProbeRunner.OnDestroy() below,
        /// NOT here. BepInEx 6's BasePlugin is not a MonoBehaviour and exposes
        /// no OnDestroy, so `override void OnDestroy()` here is CS0115 -- the
        /// exact error v2.10 and v2.11 failed with. The injected ProbeRunner
        /// IS a MonoBehaviour, so it receives Unity's destroy message.

        public override void Load()
        {
            try
            {
                CfgEnabled = Config.Bind("General", "Enabled", true,
                    "Master switch. False = the mod writes nothing at all.");
                CfgIntervalMs = Config.Bind("General", "SampleIntervalMs", 500,
                    "Milliseconds between samples. Game objects are only read inside the main-thread hook.");

                // ---- v2.6: overlay ----
                CfgHud = Config.Bind("Hud", "Enabled", true,
                    "Ten-cell status band across the top. F1 steps the palette: grey / lapis / auto / off. "
                   + "F2 lists what worked, F3 lists what failed, F4 cycles the old charts, F9 clears "
                   + "the cumulative death count and play time.");
                CfgHudRequireServer = Config.Bind("Hud", "RequireServer", true,
                    "Only show the overlay once a server endpoint is known, i.e. after joining a game.");
                CfgHudFontSize = Config.Bind("Hud", "FontSize", 16, "Overlay font size in pixels.");
                CfgHudRefreshMs = Config.Bind("Hud", "RefreshMs", 1000,
                    "How often the overlay text is rebuilt (drawing itself follows the frame rate).");

                Log.LogInfo("=== " + NAME + " v" + VERSION + " ===");

                if (!CfgEnabled.Value)
                {
                    Log.LogInfo("disabled by config -- nothing will be written");
                    return;
                }

                // ---- environment guard runs FIRST ----
                Guard.RunFullCheck();

                if (!Out.Init())
                {
                    Log.LogError("could not create output directory -- aborting");
                    return;
                }

                Out.WriteReport("guard_report.txt", Guard.Report);

                // A1：链路通讯日志（握手/对时/上报/故障）单独一份，便于定位。
                Link.SetCommPath(Out.Dir);

                // ---- v2.39: clock anchor ----
                //
                // 服务器端 v3.28 起带 ClockOffsetSec（实测 10.503 秒）用于把两条
                // 时间轴对齐，但那个常数是**某一台机器**上测出来的，换机器就会变。
                // 这里把本机的"本地时间 vs UTC"对应关系写死，让对账脚本能自己算，
                // 不必假设时区、也不必每次人工拿命中事件去配。
                //
                // 用法：客户端 ts_local 与服务器端 ts_local 都是本地时间，
                // 若两台机器时区不同，先各自换算到 UTC 再比。
                try
                {
                    DateTime loc = DateTime.Now, utc = DateTime.UtcNow;
                    double offH = (loc - utc).TotalHours;
                    Out.WriteReport("clock_anchor.txt",
                        "Clock anchor (v2.39) — written at plugin start\r\n"
                        + "Generated : " + loc.ToString("yyyy-MM-dd HH:mm:ss.fff",
                                            CultureInfo.InvariantCulture) + "\r\n"
                        + "\r\n"
                        + "local_time  = " + loc.ToString("yyyy-MM-dd HH:mm:ss.fff",
                                            CultureInfo.InvariantCulture) + "\r\n"
                        + "utc_time    = " + utc.ToString("yyyy-MM-dd HH:mm:ss.fff",
                                            CultureInfo.InvariantCulture) + "\r\n"
                        + "utc_offset_hours = " + offH.ToString("0.##",
                                            CultureInfo.InvariantCulture) + "\r\n"
                        + "\r\n"
                        + "Why this exists\r\n"
                        + "---------------\r\n"
                        + "Server and client write BOTH ts (UTC) and ts_local (wall clock).\r\n"
                        + "Merging the two logs needs a common timeline. Compare on UTC, or\r\n"
                        + "convert both sides to UTC first using the offsets above.\r\n"
                        + "\r\n"
                        + "Measured 2026-09-09: client local + 10.503 s = server local, on\r\n"
                        + "machines that share a timezone. That residual is clock SKEW, not a\r\n"
                        + "timezone gap — it is baked into the server config as ClockOffsetSec\r\n"
                        + "but must be re-measured whenever the machine changes.\r\n");
                }
                catch { }

                // ---- v2.16: install integrity (directory Merkle hash) ----
                // Started BEFORE the Disabled check on purpose. Guard treats
                // "another plugin present" as fatal and would otherwise return
                // here with nothing written -- but the leaderboard rules call
                // that a grey state (allowed to play, simply not ranked), and
                // a grey state still needs evidence of WHY it is grey.
                // Purely local: computes hashes, uploads nothing. Lands in
                // probe_out\integrity_probe.txt when the scan finishes.
                try
                {
                    IntegrityProbe.RunAsync();
                    Log.LogInfo("integrity: scan started (rules v" +
                                IntegrityProbe.RULES_VERSION + ")");
                }
                catch (Exception e)
                {
                    Log.LogWarning("integrity scan failed: " + U.Short(e));
                }

                if (Guard.Disabled)
                {
                    Log.LogWarning("ENVIRONMENT CHECK FAILED -- probe disabled.");
                    Log.LogWarning("reason: " + Guard.Reason);
                    Log.LogWarning("see BepInEx\\probe_out\\guard_report.txt");
                    Out.Event("\"type\":\"disabled\",\"ts\":\"" + U.Esc(U.Now()) +
                              "\",\"reason\":\"" + U.Esc(Guard.Reason) + "\"");
                    Out.Close();
                    return;
                }

                Log.LogInfo("environment guard: clean");

                // ---- identity + network (no Unity objects involved) ----
                Identity.Probe();
                
                // v2.35: cumulative deaths / play time, restored before the first tick
                // so the very first frame of the band already shows the right letter.
                SessionStore.Load();
                Out.WriteReport("identity_probe.txt", Identity.Report);
                Log.LogInfo("identity: " + (Identity.SteamId != 0
                    ? "steamid64=" + Identity.SteamId + " via " + Identity.Route
                    : "NOT FOUND (see identity_probe.txt)"));

                NetProbe.Probe();
                Out.WriteReport("network_probe.txt", NetProbe.Report);
                Log.LogInfo("network: " + (NetProbe.RemoteCount > 0
                    ? "remote=" + NetProbe.RemoteEndPoint
                    : "no connected peer (see network_probe.txt)"));

                // ---- v2.5: server identity straight from Bolt ----
                // NetProbe cannot answer this: Bolt holds an unconnected UDP
                // socket, so the OS table has no remote peer for it and every
                // sample carried an empty "server". Bolt keeps the answer on
                // the connection object itself (RemoteEndPoint + ConnectionId),
                // which is the same pair the game prints to its own log.
                try
                {
                    BoltProbe.Refresh();
                    Out.WriteReport("bolt_probe.txt", BoltProbe.BuildReport());
                    Log.LogInfo("bolt: " + (BoltProbe.EndPoint.Length > 0
                        ? "server=" + BoltProbe.EndPoint + " conn=" + BoltProbe.ConnectionId
                        : "no bolt connection yet (retried while playing)"));
                }
                catch (Exception e)
                {
                    Log.LogWarning("bolt probe failed: " + U.Short(e));
                }

                // ---- v2.4: the known entry point ----
                // Written unconditionally so that even a session where health
                // never resolves still tells us how far the walk got.
                try
                {
                    Out.WriteReport("localplayer_probe.txt", LocalPlayer.Report());
                    Log.LogInfo("localplayer: " + (LocalPlayer.Instance() != null
                        ? "instance OK"
                        : "no instance yet (expected in main menu)"));
                }
                catch (Exception e)
                {
                    Log.LogWarning("localplayer report failed: " + U.Short(e));
                }

                // ---- main-thread hook for anything touching game objects ----
                _hooked = InstallFrameHook();
                if (!_hooked)
                {
                    Log.LogError("could not install a main-thread hook.");
                    Log.LogError("Health CANNOT be sampled safely without one -- see vitals_probe.txt");
                    Out.WriteReport("vitals_probe.txt",
                        "=== vitals discovery ===\n" +
                        "RESULT : SKIPPED\n" +
                        "REASON : no main-thread hook could be installed, so game objects\n" +
                        "         were never touched (touching them off-thread crashes IL2CPP).\n" +
                        "         Identity and network probes above already succeeded, so the\n" +
                        "         install itself is fine -- this is a hook-targeting problem.\n");
                    Out.Event("\"type\":\"hook_failed\",\"ts\":\"" + U.Esc(U.Now()) + "\"");
                }

                // ---- v2.5: event-driven damage timestamps ----
                // Independent of the frame hook: it patches two game methods
                // and reads health the moment one of them fires. If it cannot
                // patch anything, periodic sampling still carries on.
                try
                {
                    DamageHook.Install();
                    Out.WriteReport("damage_hook.txt", DamageHook.Report);
                    Log.LogInfo("damage hook: patched=" + DamageHook.Patched +
                                " missed=" + DamageHook.Missed);
                }
                catch (Exception e)
                {
                    Log.LogWarning("damage hook failed: " + U.Short(e));
                }

                // ---- v2.26: first diagnostic snapshot, written AT STARTUP ----
                // The periodic refresh below only runs from the frame hook, so
                // without this a session that never leaves the main menu would
                // leave probe_diag.txt missing -- precisely the file that is
                // meant to explain why nothing else was collected. Everything
                // knowable this early is recorded; the periodic pass refines it.
                try
                {
                    Diag.Check("core", "guard", !Guard.Disabled, "clean",
                        Guard.Reason.Length > 0 ? Guard.Reason
                                                : "environment check failed");
                    Diag.Check("core", "steamid", Identity.SteamId != 0,
                        "steamid64=" + Identity.SteamId
                            .ToString(CultureInfo.InvariantCulture)
                        + " via " + Identity.Route,
                        "not found -- see identity_probe.txt");
                    Diag.Check("core", "server", BoltProbe.EndPoint.Length > 0,
                        BoltProbe.EndPoint + " conn=" + BoltProbe.ConnectionId,
                        "not connected yet (expected in main menu)");
                    Diag.Check("hook", "frame", _hooked, "installed",
                        "NOT installed -- see vitals_probe.txt");
                    Diag.Check("hook", "damage", DamageHook.Patched > 0,
                        "patched=" + DamageHook.Patched
                            .ToString(CultureInfo.InvariantCulture),
                        "nothing patched -- see damage_hook.txt");
                    Diag.Flush(true);
                }
                catch { }

                Log.LogInfo("output: " + Out.JsonlPath);
                Log.LogInfo("ready.");
            }
            catch (Exception e)
            {
                try { Log.LogError("Load() threw: " + U.Short(e)); }
                catch { }
            }
        }

        // ---------------------------------------------------------------- hook

        /// Gets a per-frame callback on the Unity main thread.
        ///
        /// Two routes, tried in this order:
        ///
        ///   1. ClassInjector -- register OUR OWN MonoBehaviour and let Unity
        ///      call its Update(). This depends on no game type at all, so it
        ///      cannot fail because a class was renamed or inlined.
        ///
        ///   2. Harmony on a game Update() -- the fallback. It bets on a game
        ///      type existing by name (VailWorldSimulation and friends), which
        ///      is exactly the kind of guess that has bitten us before.
        ///
        /// Route 1 is the standard pattern for BepInEx 6 IL2CPP: the public
        /// EnableFullScreenToggleIL2CPP_net6 plugin, confirmed working on this
        /// game's client, uses ClassInjector.RegisterTypeInIl2Cpp + AddComponent
        /// in precisely this way.
        private bool InstallFrameHook()
        {
#if HAS_INTEROP
            try
            {
                // BasePlugin.AddComponent<T>() registers the type with the
                // Il2Cpp type system if needed, then mounts it. Using BepInEx's
                // own entry point means we never call GameObject's constructor
                // or the generic AddComponent<T>() from interop, whose exact
                // signatures we cannot verify from here.
                AddComponent<ProbeRunner>();

                // v2.6: the overlay is drawn from OnGUI, which only exists on
                // an injected MonoBehaviour. If we end up on the Harmony route
                // instead there is no safe way to paint, so the HUD stays off
                // rather than pretending to work.
                _hudAvailable = true;

                Log.LogInfo("frame hook: ClassInjector (own MonoBehaviour, no game type assumed)");
                Log.LogInfo("hud: " + (CfgHud.Value
                    ? "enabled (top-left overlay)"
                    : "disabled by config"));
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning("ClassInjector route failed (" + U.Short(e) +
                               ") -- falling back to Harmony");
            }
#else
            Log.LogWarning("interop assemblies were not present at compile time -- " +
                           "ClassInjector route unavailable, using Harmony only");
#endif
            return InstallHook();
        }

        /// Fallback: patch a game Update() with Harmony. See InstallFrameHook.
        /// Tries several candidate types; the server-side collector already
        /// proved VailWorldSimulation.Update exists in this engine, so it is
        /// tried first.
        private bool InstallHook()
        {
            string[] typeHints = { "VailWorldSimulation", "WorldSimulation", "GameManager" };
            string[] methodNames = { "Update", "LateUpdate", "FixedUpdate" };

            foreach (string hint in typeHints)
            {
                Type t = FindType(hint);
                if (t == null) continue;

                foreach (string mn in methodNames)
                {
                    MethodInfo target = null;
                    try
                    {
                        target = t.GetMethod(mn, U.BF, null, Type.EmptyTypes, null);
                    }
                    catch { }
                    if (target == null) continue;

                    try
                    {
                        var harmony = new Harmony(GUID);
                        MethodInfo postfix = typeof(ProbePlugin).GetMethod(
                            nameof(OnFrame), BindingFlags.Static | BindingFlags.Public);
                        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                        Log.LogInfo("hook installed on " + t.FullName + "." + mn);
                        return true;
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning("patch " + t.Name + "." + mn + " failed: " + U.Short(e));
                    }
                }
            }
            return false;
        }

        private static Type FindType(string hint)
        {
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (Type t in types)
                    {
                        string fn;
                        try { fn = t.FullName; } catch { continue; }
                        if (string.IsNullOrEmpty(fn)) continue;
                        if (fn.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return t;
                    }
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------- sampling

        /// Harmony postfix. Runs on the Unity main thread -- the ONLY place
        /// this mod is allowed to touch game objects.
        public static void OnFrame()
        {
            try
            {
                if (!CfgEnabled.Value) return;
                if (Guard.Disabled) return;

                long now = Environment.TickCount64;
                // v2.35: captured before _lastTickMs is overwritten, because the play
                // clock is built from measured gaps, not from the configured interval.
                long prevTickMs = _lastTickMs;
                int interval = CfgIntervalMs != null ? CfgIntervalMs.Value : 500;
                if (interval < 50) interval = 50;

                if (now - _lastTickMs < interval) return;
                _lastTickMs = now;
                _tick++;

                // one-time discovery, main thread
                if (!_discovered)
                {
                    _discovered = true;
                    try
                    {
                        VitalsProbe.Discover();
                        Out.WriteReport("vitals_probe.txt", VitalsProbe.Report);
                        BepInEx.Logging.Logger.CreateLogSource(NAME).LogInfo(
                            "vitals: " + (VitalsProbe.Resolved
                                ? "RESOLVED at " + VitalsProbe.ResolvedPath
                                : "NOT FOUND (see vitals_probe.txt)"));
                    }
                    catch (Exception e)
                    {
                        Out.WriteReport("vitals_probe.txt",
                            "vitals discovery threw: " + U.Short(e) + "\n");
                    }

                    // v2.14: the rest of the survival state -- stamina, food,
                    // water, rest, strength, sickness, cold, and the flags on
                    // PlayerStats. Discovered here, in the same one-shot pass,
                    // because it needs a live player just like health does.
                    try
                    {
                        PlayerInfoProbe.Discover();
                        if (!string.IsNullOrEmpty(PlayerInfoProbe.Report))
                            Out.WriteReport("player_info_probe.txt", PlayerInfoProbe.Report);
                        BepInEx.Logging.Logger.CreateLogSource(NAME).LogInfo(
                            PlayerInfoProbe.Status());
                    }
                    catch (Exception e)
                    {
                        Out.WriteReport("player_info_probe.txt",
                            "player info discovery threw: " + U.Short(e) + "\n");
                    }
                }

                // v2.4: retry the direct path while unresolved.
                //
                // The one-shot discovery above runs at plugin load, often while
                // the game is still in the main menu where no player exists.
                // It therefore nearly always reported NOT FOUND and never tried
                // again. This keeps trying (throttled) once a session is live.
                if (!VitalsProbe.Resolved)
                {
                    VitalsProbe.Tick();
                    if (!string.IsNullOrEmpty(VitalsProbe.RetryNote))
                    {
                        BepInEx.Logging.Logger.CreateLogSource(NAME).LogInfo(
                            VitalsProbe.RetryNote);
                        VitalsProbe.RetryNote = null;
                        Out.WriteReport("vitals_probe.txt",
                            "resolved on retry at " + VitalsProbe.ResolvedPath + "\n"
                            + (VitalsProbe.Report ?? ""));
                    }
                }

                // Screen display probe
                try { ScreenDisplay.Tick(); } catch { }

                // v2.29: the survival-state probe is a one-shot too, and it
                // runs at plugin load when there is no player yet, so a whole
                // session could report "no player-info fields" and never
                // recover. Keep rediscovering while it still holds nothing.
                if (!PlayerInfoProbe.HasData && now - _lastPinfoDiscoverMs > 5000)
                {
                    _lastPinfoDiscoverMs = now;
                    try
                    {
                        // v2.30: force, otherwise the one-shot guard inside
                        // Discover() swallows every retry.
                        PlayerInfoProbe.Discover(true);
                        if (PlayerInfoProbe.HasData)
                        {
                            if (!string.IsNullOrEmpty(PlayerInfoProbe.Report))
                                Out.WriteReport("player_info_probe.txt", PlayerInfoProbe.Report);
                            BepInEx.Logging.Logger.CreateLogSource(NAME).LogInfo(
                                "playerinfo: recovered on retry -- " + PlayerInfoProbe.Status());
                        }
                    }
                    catch { }
                }

                // v2.4 REMOVED: "if (!VitalsProbe.Resolved) return;"
                //
                // That line threw away every position sample in any session
                // where health was not found. Position is the one thing the
                // server can corroborate independently (server player_hit
                // carries player_pos), so dropping it lost the only cross-check
                // available without Bolt IDs. Health and position are now
                // sampled independently -- either can work alone.
                // v2.4.1: must be initialised. The && below short-circuits, so
                // when Resolved is false Sample() never runs and hp would stay
                // unassigned -> CS0165. The compiler cannot prove the out param
                // was written on that path, even though we only read hp when
                // ok is true.
                float hp = 0f;
                bool ok = VitalsProbe.Resolved && VitalsProbe.Sample(out hp);
                if (ok)
                {
                    _hpReads++;
                    if (!float.IsNaN(_lastHp) && Math.Abs(hp - _lastHp) > 0.001f) _hpChanges++;
                    _lastHp = hp;
                }

                float x = 0f, y = 0f, z = 0f;
                bool havePos = VitalsProbe.SamplePosition(out x, out y, out z);

                // v2.17: facing, tracked separately from position. A session
                // can read coordinates and still fail on rotation, and folding
                // the two together would throw away good position data because
                // of an unrelated rotation miss.
                float yaw = 0f;
                bool haveYaw = VitalsProbe.SampleFacing(out yaw);

                // ---- v2.9: server identity, resolved BEFORE anything is counted ----
                if (!_serverResolved || BoltProbe.EndPoint.Length == 0) BoltProbe.Refresh();
                string server = BoltProbe.EndPoint;
                if (server.Length == 0) server = NetProbe.RemoteEndPoint;
                if (server.Length > 0)
                {
                    // v2.9: the trigger is "the endpoint changed", not "an
                    // endpoint was seen". The old test fired once and never
                    // again, so joining a second server in one game session
                    // carried the previous server's hits, damage and distance
                    // straight across. Deaths looked right only because the
                    // game zeroes its own counter on a new save.
                    if (!_serverResolved ||
                        !string.Equals(server, _serverKey, StringComparison.Ordinal))
                    {
                        // Bank what the previous server accumulated before it
                        // is thrown away, so the totals still exist somewhere.
                        CloseSession(now, "server_changed");

                        Stats.Reset();
                        DeathProbe.Reset();
                        Series.Reset(now);
                        // v2.14: the two new probes keep baselines of their own
                        // -- a backpack snapshot and a throttled stat reading --
                        // and both would otherwise be carried into a server
                        // they were not taken on.
                        InventoryProbe.Reset();
                        PlayerInfoProbe.Reset();
                        _invReportWritten = false;
                        GearProbe.Reset();          // v2.17: armour verdict
                        WorldProbe.Reset();         // v2.17: world singleton
                        _gearReportLast = null;
                        _worldReportLast = null;

                        _serverKey = server;
                        _serverSinceMs = now;
                        _sessionStartMs = now;
                        _armedLogged = false;
                        // A1：换服了，旧链路作废，重新握手。
                        Link.Reset();
                        // A1：比对状态 likewise —— 上一场的命中不能配到这一场。
                        CrossCheck.Reset();
                        // A1：记录登录时刻，供 C_ 日志文件名。
                        Sess.Reset();
                        Sess.NoteJoin();
                    }
                    _serverResolved = true;

                    // A1：拿到服务器地址就开握手。内部只跑一次，失败自动降级离线。
                    Link.Begin(server);
                }
                else
                {
                    // ---- v2.10: offline / main menu ----
                    //
                    // No server endpoint means there is no session: the player
                    // left the server, was dropped, or is sitting on the main
                    // menu. All six HUD numbers belong to a server, so all six
                    // are cleared here instead of being left on screen as a
                    // stale souvenir of the last one.
                    //
                    // Banked first: CloseSession writes sessions.log, so the
                    // totals are not lost -- they are just no longer live.
                    if (_serverKey.Length > 0 || _sessionStartMs != 0 || _serverSinceMs != 0)
                    {
                        CloseSession(now, "offline");

                        Stats.Reset();          // hits, hp loss, distance, coords
                        DeathProbe.Reset();     // deaths (and the 60 s window)
                        Series.Reset(now);      // waveform history
                        InventoryProbe.Reset(); // v2.14: backpack baseline
                        PlayerInfoProbe.Reset();// v2.14: stat baseline
                        _invReportWritten = false;
                        GearProbe.Reset();          // v2.17: armour verdict
                        WorldProbe.Reset();         // v2.17: world singleton
                        _gearReportLast = null;
                        _worldReportLast = null;

                        _serverKey = "";
                        _serverSinceMs = 0;
                        _sessionStartMs = 0;
                        _armedLogged = false;
                        _serverResolved = false;
                    }

                    // v2.10: the overlay is gated on a live session. Visible is
                    // the F1 toggle; Connected says whether the numbers behind
                    // it mean anything. Offline, they do not. v2.11: an F2
                    // slideshow would keep cycling behind the hidden overlay,
                    // so it is cancelled here as well.
#if HAS_INTEROP
                    Hud.Connected = false;
                    HudBand.Connected = false;
                    Hud.StopAuto();
#endif
                    return;
                }

                // ---- v2.9: the 30 second collection threshold ----
                //
                // Nothing is counted or written until the player has been in a
                // live session for ArmDelayMs. The first seconds of a session
                // are the noisiest part of it: spawn teleports, health being
                // initialised, the load-in stall. The 80.9 phantom HP loss in
                // an earlier build was exactly this -- a reading taken while
                // the session was still settling, booked as damage.
                bool armed = _serverSinceMs != 0 && (now - _serverSinceMs >= ArmDelayMs);

                if (armed && !_armedLogged)
                {
                    _armedLogged = true;
                    Out.Event("\"type\":\"armed\",\"ts\":\"" + U.Esc(U.Now()) +
                              "\",\"server\":\"" + U.Esc(server) +
                              "\",\"delay_ms\":" + U.Num((long)ArmDelayMs));
                }

                if (!armed)
                {
                    // Still settling. Count nothing, write nothing, and leave
                    // the overlay hidden -- it has no honest numbers to show.
                    //
                    // v2.10: actually hide it. The comment always claimed the
                    // overlay was left hidden, but Connected was only ever set
                    // true and never back, so an overlay opened on the previous
                    // server stayed on screen through the whole 30 s arm delay
                    // of the next one, painting the reset (zero) counters.
#if HAS_INTEROP
                    Hud.Connected = false;
                    HudBand.Connected = false;
                    Hud.StopAuto();
#endif
                    return;
                }

                // v2.6: feed the HUD counters. Every number the overlay shows
                // is produced right here, on the main thread, once per sample --
                // OnGUI then only paints the cached string.
                //
                // The damage hook is the primary hit source. If it could not be
                // installed at all, a health drop is used instead: less precise
                // (it misses parried hits), but better than showing a zero.
                bool hpDropped = Stats.OnSample(ok, hp, havePos, x, y, z, now);
                if (DamageHook.Patched == 0 && hpDropped) Stats.OnLocalHit(now, Series.Unknown);

                // v2.8: feed the chart history. ResolvePending gives a hit with
                // an unreadable damage argument its height from the health it
                // actually cost, which is only known now, one sample later.
                Series.ResolvePending(Stats.HpLoss);
                Series.AddSpeed(now, havePos ? Stats.SpeedKmh : 0f);
                Series.AddLoss(now, Stats.HpLoss);

                // v2.7: death counter. Runs on the same tick and the same
                // thread as the other counters; it reaches PlayerStats through
                // LocalPlayer, which means UnityEngine access.
                DeathProbe.Update(ok, hp);

                var sb = new System.Text.StringBuilder(256);
                sb.Append("\"type\":\"sample\"");
                // v2.40：第一条 sample 说明本地玩家已进入世界，
                //   这与服务器端 player_join 基本同时 —— 两端各开一个 30 秒窗口。
                ClockSync.Open();
                sb.Append(ClockSync.JsonField());
                sb.Append(",\"ts\":\"").Append(U.Esc(U.Now())).Append('"');
                sb.Append(",\"ts_utc\":\"").Append(U.Esc(U.NowUtc())).Append('"');
                // v2.39: live downed state. Server cannot see this.
                sb.Append(",\"downed\":").Append(DeathProbe.IsDowned ? '1' : '0');
                sb.Append(",\"tick\":").Append(U.Num(_tick));
                sb.Append(",\"steamid\":\"").Append(
                    Identity.SteamId.ToString(CultureInfo.InvariantCulture)).Append('"');
                // v2.5: server identity. Bolt is the primary source (the
                // UDP table has no remote peer for Bolt's unconnected
                // socket), NetProbe is the fallback.
                // (server identity is resolved above, before the arm gate)
                sb.Append(",\"server\":\"").Append(U.Esc(server)).Append('"');
                sb.Append(",\"conn\":\"").Append(U.Esc(BoltProbe.ConnectionId)).Append('"');
                sb.Append(",\"hp\":").Append(ok ? U.Num(hp) : "null");
                sb.Append(",\"hp_reads\":").Append(U.Num(_hpReads));
                sb.Append(",\"hp_changes\":").Append(U.Num(_hpChanges));
                if (havePos)
                {
                    sb.Append(",\"pos\":[").Append(U.Num(x)).Append(',')
                      .Append(U.Num(y)).Append(',').Append(U.Num(z)).Append(']');
                    // v2.17: speed was already computed for the overlay and the
                    // chart but never reached the stream. It is the cheapest
                    // discriminator between "moved" and "was moved", and the
                    // distance total alone cannot separate a fast short dash
                    // from a slow long walk.
                    sb.Append(",\"spd\":").Append(U.Num(Stats.SpeedKmh));
                }
                else
                {
                    sb.Append(",\"pos\":null");
                }
                // v2.17: yaw in degrees, null when unreadable. Written outside
                // the position branch on purpose -- the two are independent
                // reads and one failing must not blank the other.
                if (haveYaw) sb.Append(",\"facing\":").Append(U.Num(yaw));
                else sb.Append(",\"facing\":null");
                sb.Append(",\"hp_path\":\"").Append(U.Esc(VitalsProbe.ResolvedPath)).Append('"');
                sb.Append(",\"hp_attempts\":").Append(U.Num(VitalsProbe.Attempts));
                // v2.6: the same counters the overlay shows, so the HUD can be
                // cross-checked against the recorded stream.
                sb.Append(",\"hits\":").Append(U.Num(Stats.Hits));
                sb.Append(",\"hp_loss\":").Append(U.Num(Stats.HpLoss));
                sb.Append(",\"dist\":").Append(U.Num(Stats.Distance));
                // v2.7: deaths, plus which source produced the number. Without
                // the source a 0 is ambiguous: no deaths, or unreadable
                // counter. With it, the two are told apart downstream.
                sb.Append(",\"deaths\":");
                if (DeathProbe.Source == "none") sb.Append("null");
                else sb.Append(U.Num(DeathProbe.Deaths));
                // v2.9: the pre-window figure rides along. A gap between the
                // two is the 60 s window doing its job, and it is only visible
                // if both numbers are in the stream.
                sb.Append(",\"deaths_pre\":").Append(U.Num(DeathProbe.Raw));
                sb.Append(",\"deaths_src\":\"").Append(U.Esc(DeathProbe.Source)).Append('"');
                if (DeathProbe.HaveNative)
                {
                    sb.Append(",\"deaths_field\":").Append(U.Num(DeathProbe.NativeField));
                    sb.Append(",\"deaths_base\":").Append(U.Num(DeathProbe.Baseline));
                }

                // v2.4: backpack. Written only when something actually changed,
                // so an idle player produces no inventory traffic.
                string inv = InventoryProbe.Sample();
                if (!string.IsNullOrEmpty(inv))
                {
                    sb.Append(',').Append(inv);
                }

                // v2.14: the survival stats. Throttled inside the probe --
                // stamina and fullness do not need to be written at 2 Hz.
                string pinfo = PlayerInfoProbe.Sample(now);
                if (!string.IsNullOrEmpty(pinfo))
                {
                    sb.Append(',').Append(pinfo);
                }

                // v2.17: worn and held. Sampled every tick because the damage
                // hook reads it at hit time, but written to the stream only on
                // change. This is what turns "armour voids the session" from a
                // rule into something the ranking backend can actually check.
                //
                // Absent from v2.16 -- that build was cut from the v2.14 tree
                // and lost this file entirely, so the leaderboard gate had no
                // input. Restored here.
                string gear = GearProbe.Sample();
                if (!string.IsNullOrEmpty(gear))
                {
                    sb.Append(',').Append(gear);
                }
                if (!string.IsNullOrEmpty(GearProbe.Report) &&
                    GearProbe.Report != _gearReportLast)
                {
                    _gearReportLast = GearProbe.Report;
                    Out.WriteReport("gear_probe.txt", GearProbe.Report);
                }

                // v2.17: world clock and population. Throttled inside the probe
                // to once every ten seconds -- a per-sample read would be cost
                // without data, since the clock moves once a second at most.
                string world = WorldProbe.Sample(now);
                if (!string.IsNullOrEmpty(world))
                {
                    sb.Append(',').Append(world);
                }
                if (!string.IsNullOrEmpty(WorldProbe.Report) &&
                    WorldProbe.Report != _worldReportLast)
                {
                    _worldReportLast = WorldProbe.Report;
                    Out.WriteReport("world_probe.txt", WorldProbe.Report);
                }
                // v2.5 FIX: the shape report used to be written only inside
                // the block above, i.e. only when a change was detected. When
                // the container member is never found, Sample() returns null
                // forever and the report -- the one thing that would explain
                // WHY -- was never written. That is exactly what happened in
                // the last session: 0 inventory output and no diagnostics.
                // The report is now written unconditionally once available.
                if (!_invReportWritten && !string.IsNullOrEmpty(InventoryProbe.Report))
                {
                    _invReportWritten = true;
                    Out.WriteReport("inventory_probe.txt", InventoryProbe.Report);
                }

                // v2.7: written once we know SOMETHING about deaths. The second
                // condition matters: if all three sources fail, Source stays
                // "none" forever and the report would never be written -- yet
                // that is exactly the case that most needs explaining. After
                // ~30s of ticks (2 Hz) we write it anyway, saying so.
                if (!_deathReportWritten &&
                    (DeathProbe.Source != "none" || _tick > 60))
                {
                    _deathReportWritten = true;
                    Out.WriteReport("death_probe.txt", DeathProbe.BuildReport());
                }

                // A1：链路状态写进本地流，供事后核对"这一段到底有没有同步上"。
                sb.Append(",\"lk\":").Append(Link.Online ? '1' : '0');
                if (Link.Online)
                    sb.Append(",\"off_ms\":").Append(U.Num(Link.OffsetMs));

                Out.Event(sb.ToString());

                // A1：把这一拍的关键数据同步给服务器端（服务器 HTTP 端口）。
                //   服务器端读不到玩家血量、也没有 SteamID（专用服无 PlayerNetwork
                //   类型），只有客户端能补上 —— 这正是"受害者归属"缺的那一半。
                try
                {
                    var lb = new System.Text.StringBuilder(160);
                    lb.Append("{\"cli_ms\":").Append(Link.NowMs());
                    lb.Append(",\"ts_utc\":\"").Append(U.Esc(U.NowUtc())).Append('"');
                    lb.Append(",\"sid\":\"").Append(
                        Identity.SteamId.ToString(CultureInfo.InvariantCulture)).Append('"');
                    lb.Append(",\"hp\":").Append(ok ? U.Num(hp) : "null");
                    lb.Append(",\"dn\":").Append(DeathProbe.IsDowned ? '1' : '0');
                    if (havePos)
                        lb.Append(",\"p\":[").Append(U.Num(x)).Append(',')
                          .Append(U.Num(y)).Append(',').Append(U.Num(z)).Append(']');
                    lb.Append('}');
                    Link.Report(lb.ToString());
                }
                catch { }

                // A1：周期拉服务器端的 player_hit 做交叉比对（内部节流 10s）。
                //   自己开线程，服务器不响应也不会卡住这一拍。
                try { CrossCheck.Tick(); } catch { }

                // v2.26: refresh the registry at the end of every tick, so a
                // module that only resolves once the player is in the world
                // flips from failed to ok while the F2 panel is open, instead
                // of leaving a stale reason on screen.
                RegisterDiag();

                // v2.6: rebuild the overlay text at 1 Hz. The painting itself
                // happens in OnGUI; nothing here touches Unity.
                //
                // Guarded: the Hud class only exists when the interop
                // assemblies were present at compile time. Without them there
                // is no OnGUI to paint from, so there is nothing to build.
#if HAS_INTEROP
                if (CfgHud != null && CfgHud.Value && _hudAvailable)
                {
                    // v2.9: armed, not merely connected. Reaching this line at
                    // all means the arm gate above has been passed, so the
                    // overlay can only ever appear inside a live session that
                    // has been running for at least ArmDelayMs. It no longer
                    // exists on the main menu, and the numbers it shows are
                    // the same ones the stream carries.
                    Hud.Connected = true;
                    HudBand.Connected = true;
                    
                    // v2.35: the two figures that outlive a session. dt is the measured
                    // gap between ticks rather than the configured interval, so a stalled
                    // frame does not inflate the play clock; gaps above the sanity limit
                    // are dropped inside SessionStore.Tick.
                    SessionStore.Tick(now, prevTickMs == 0 ? 0 : now - prevTickMs,
                                      DeathProbe.Deaths);
                    // v2.9: no per-frame Hud tick exists -- F1 is polled from
                    // ProbeRunner.Update() and the view is repainted from
                    // ProbeRunner.OnGUI(). Passing a refresh interval here was
                    // a leftover from the text-HUD design, which rebuilt its
                    // string once a second; the charts redraw every frame.
                }
#endif

                // periodic re-check + flush
                if (now - _lastGuardMs > 30000)
                {
                    _lastGuardMs = now;
                    Guard.ReCheck();
                    if (Guard.Disabled)
                    {
                        Out.Event("\"type\":\"disabled\",\"ts\":\"" + U.Esc(U.Now()) +
                                  "\",\"reason\":\"" + U.Esc(Guard.Reason) + "\"");
                        Out.WriteReport("guard_report.txt", Guard.Report);
                        Out.Close();
                    }
                }

                if (now - _lastFlushMs > 5000)
                {
                    _lastFlushMs = now;
                    Out.Flush();
                }
            }
            catch
            {
                // Never let sampling throw into the game's update loop.
            }
        }

        /// v2.26: the single place that decides, for every module, whether it
        /// worked. Feeds probe_diag.txt, the F1 panel and the F2 panel.
        ///
        /// Written as checks rather than as logging: each line states what
        /// success looks like AND what the failure means, because "false" is
        /// not a diagnosis. The point of this method is that the answer to
        /// "is the gear probe working?" is already on screen and in a file
        /// before anyone opens the BepInEx log.
        private static void RegisterDiag()
        {
            try
            {
                // ---- identity and connection ----
                Diag.Check("core", "steamid", Identity.SteamId != 0,
                    Identity.SteamId.ToString(CultureInfo.InvariantCulture) +
                        " via " + Identity.Route,
                    "no steamid resolved");

                Diag.Check("core", "server",
                    BoltProbe.EndPoint.Length > 0 || NetProbe.RemoteEndPoint.Length > 0,
                    (BoltProbe.EndPoint.Length > 0 ? BoltProbe.EndPoint
                        : NetProbe.RemoteEndPoint) +
                        (BoltProbe.ConnectionId.Length > 0
                            ? " conn=" + BoltProbe.ConnectionId : ""),
                    "no connection yet");

                // ---- the player object ----
                Diag.Check("core", "localplayer", LocalPlayer.Instance() != null,
                    "instance ok",
                    "null -- expected on the main menu");

                Diag.Check("core", "hp", VitalsProbe.Resolved,
                    "via " + VitalsProbe.ResolvedPath,
                    "no readable health path");

                // ---- damage ----
                Diag.Check("hook", "damage", DamageHook.Patched > 0,
                    "patched=" + U.Num(DamageHook.Patched) +
                    " fired=" + U.Num(DamageHook.Fired) +
                    " dups_dropped=" + U.Num(DamageHook.Dups),
                    "no damage hook could be patched");

                if (DamageHook.Missed > 0)
                    Diag.Warn("hook", "damage_missed",
                        U.Num(DamageHook.Missed) + " target(s) did not resolve");

                // ---- items: the v2.26 point of the whole exercise ----
                bool weaponDecoded =
                    !string.IsNullOrEmpty(GearProbe.Weapon) &&
                    GearProbe.Weapon.IndexOf("ItemInstance",
                        StringComparison.OrdinalIgnoreCase) < 0;

                Diag.Check("item", "decode", weaponDecoded,
                    weaponDecoded ? "ids resolved: " + GearProbe.Weapon
                                  : "still a type name",
                    "item id not read -- see item_probe.txt");

                Diag.Check("item", "weapon", !string.IsNullOrEmpty(GearProbe.Weapon),
                    GearProbe.Weapon, "no held item seen");

                Diag.Check("item", "armor", GearProbe.Snapshots > 0,
                    "route=" + GearProbe.ArmorRoute +
                    "  armor=" + U.Bool(GearProbe.HasArmor) +
                    "  pieces=" + U.Num(GearProbe.ArmorPieces) +
                    (GearProbe.ArmorPoints >= 0f
                        ? "  pts=" + U.Num(GearProbe.ArmorPoints) : "") +
                    (string.IsNullOrEmpty(GearProbe.ArmorDetail)
                        ? "" : "  [" + GearProbe.ArmorDetail + "]"),
                    "equipment never sampled");

                // v2.32 -- the armour system is the only thing on this patch
                // that can see a worn plate at all. If we cannot reach it the
                // session must say so, because a "no armour" verdict produced
                // without it is not trustworthy.
                Diag.Check("item", "armoursys", ArmourProbe.Found,
                    "wearing=" + U.Bool(ArmourProbe.Wearing) +
                    "  pieces=" + U.Num(ArmourProbe.PieceCount) +
                    "  kinds=" + U.Num(ArmourProbe.KindCount) +
                    "  ids=[" + ArmourProbe.IdList + "]" +
                    (string.IsNullOrEmpty(ArmourProbe.Labels)
                        ? "" : "  " + ArmourProbe.Labels) +
                    (ArmourProbe.Points >= 0f ? "  pts=" + U.Num(ArmourProbe.Points) : "") +
                    (ArmourProbe.Rating >= 0f ? "  rating=" + U.Num(ArmourProbe.Rating) : ""),
                    "unreachable (" + ArmourProbe.Why +
                    ") -- falling back to the inventory, which cannot see worn armour");

                // v2.34 -- the measured reduction. Reported separately so a
                // session where armour was never worn still says so plainly,
                // instead of hiding behind "wearing=false".
                Diag.Check("item", "armourcalc", ArmourProbe.Reduction >= 0f,
                    ArmourProbe.Reduction >= 0f
                        ? ("100 dmg -> " + U.Num(ArmourProbe.Through) +
                           "  reduction=" + U.Num(ArmourProbe.Reduction * 100f) + "%" +
                           (ArmourProbe.ReductionDemonic >= 0f
                               ? ("  demonic=" + U.Num(ArmourProbe.ReductionDemonic * 100f) + "%")
                               : ""))
                        : "",
                    string.IsNullOrEmpty(ArmourProbe.CalcWhy)
                        ? "CalculateRemainingDamageAfterArmourHit not callable"
                        : ArmourProbe.CalcWhy);

                Diag.Check("item", "backpack", InventoryProbe.ItemKinds > 0,
                    U.Num(InventoryProbe.ItemKinds) + " kinds",
                    "0 items read (or empty backpack)");

                // ---- world and stats ----
                Diag.Check("world", "clock", WorldProbe.HaveData,
                    "day=" + U.Num(WorldProbe.Days) +
                    " season=" + (WorldProbe.Season.Length > 0 ? WorldProbe.Season : "?") +
                    " actors=" + U.Num(WorldProbe.Actors),
                    "no world singleton");

                Diag.Check("stat", "vitals", PlayerInfoProbe.HasData,
                    PlayerInfoProbe.Status(), "no player-info fields");

                Diag.Check("stat", "deaths", DeathProbe.Source != "none",
                    "src=" + DeathProbe.Source, "no death source");

                Diag.Check("file", "integrity", IntegrityProbe.Done,
                    "root=" + (IntegrityProbe.RootHash.Length > 0
                        ? IntegrityProbe.RootHash.Substring(0,
                            IntegrityProbe.RootHash.Length > 12
                                ? 12 : IntegrityProbe.RootHash.Length) : "?"),
                    "scan still running");

                // Written often enough to be current, throttled inside so a
                // 2 Hz loop cannot hammer the disk.
                Diag.Flush(false);
            }
            catch { }
        }

        /// v2.9: banks the running totals for the server being left and writes
        /// them to sessions.log before the counters are cleared.
        ///
        /// A per-server record is the only way to answer "was that number from
        /// this server or the last one?" after the fact. The HUD is live and
        /// forgets; this is the small local trace that does not.
        private static void CloseSession(long now, string reason)
        {
            if (_serverKey.Length == 0) return;

            long durMs = now - _sessionStartMs;
            if (durMs < 0) durMs = 0;

            Out.Append("sessions.log",
                U.Now()
                + "  server=" + _serverKey
                + "  dur=" + (durMs / 1000L).ToString(CultureInfo.InvariantCulture) + "s"
                + "  hits=" + Stats.Hits.ToString(CultureInfo.InvariantCulture)
                + "  hp_loss=" + Stats.Fmt(Stats.HpLoss, 1)
                + "  dist=" + Stats.Fmt(Stats.Distance, 1) + "m"
                + "  deaths=" + DeathProbe.Deaths.ToString(CultureInfo.InvariantCulture)
                + "  deaths_pre=" + DeathProbe.Raw.ToString(CultureInfo.InvariantCulture)
                + "  src=" + DeathProbe.Source
                + "  reason=" + reason);

            Out.Event("\"type\":\"session\",\"ts\":\"" + U.Esc(U.Now())
                + "\",\"server\":\"" + U.Esc(_serverKey)
                + "\",\"dur_ms\":" + U.Num(durMs)
                + ",\"hits\":" + U.Num(Stats.Hits)
                + ",\"hp_loss\":" + U.Num(Stats.HpLoss)
                + ",\"dist\":" + U.Num(Stats.Distance)
                + ",\"deaths\":" + U.Num(DeathProbe.Deaths)
                + ",\"deaths_pre\":" + U.Num(DeathProbe.Raw)
                + ",\"deaths_src\":\"" + U.Esc(DeathProbe.Source)
                + "\",\"reason\":\"" + U.Esc(reason) + "\"");

            // v2.39: a one-line digest meant to be read NEXT TO the server
            // collector's own record, not instead of it.
            //
            // The dedicated server cannot read player health at all, so the
            // damage figure it reports is the attacker's raw output, never the
            // health the player actually lost. Ground truth for "how much did
            // this really cost" only exists here, on the client. Without this
            // line a submission carries raw damage with nothing to check it
            // against, and armour / block / parry reductions stay invisible.
            //
            // Appended, not overwritten: a day of play has several sessions and
            // every one of them needs to survive for the after-action review.
            try
            {
                Out.Append("client_digest.txt",
                    "ts=" + U.Now()
                    + "  ts_utc=" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff",
                                        CultureInfo.InvariantCulture)
                    + "  steamid=" + Identity.SteamId.ToString(CultureInfo.InvariantCulture)
                    + "  server=" + _serverKey
                    + "  dur=" + (durMs / 1000L).ToString(CultureInfo.InvariantCulture) + "s"
                    + "  hits=" + Stats.Hits.ToString(CultureInfo.InvariantCulture)
                    + "  hp_loss=" + Stats.Fmt(Stats.HpLoss, 1)
                    + "  blocks=" + Stats.Blocks.ToString(CultureInfo.InvariantCulture)
                    + "  deaths=" + DeathProbe.Deaths.ToString(CultureInfo.InvariantCulture)
                    + "  dist=" + Stats.Fmt(Stats.Distance, 1) + "m"
                    + "  src=client_real_hp_loss");
            }
            catch { }

            // A1：统一的会话日志 C_<登录>_<停止>.txt。
            //   带上链路状态与对时结果，服务器端故障时一眼可见。
            try
            {
                var lb = new System.Text.StringBuilder();
                lb.Append("server=" + _serverKey + "\n");
                lb.Append("dur=" + (durMs / 1000L).ToString(CultureInfo.InvariantCulture) + "s\n");
                lb.Append("hits=" + Stats.Hits.ToString(CultureInfo.InvariantCulture)
                          + "  hp_loss=" + Stats.Fmt(Stats.HpLoss, 1)
                          + "  blocks=" + Stats.Blocks.ToString(CultureInfo.InvariantCulture) + "\n");
                lb.Append("deaths=" + DeathProbe.Deaths.ToString(CultureInfo.InvariantCulture)
                          + "  dist=" + Stats.Fmt(Stats.Distance, 1) + "m"
                          + "  reason=" + reason + "\n");
                lb.Append(new string('-', 60) + "\n");
                lb.Append("link: " + Link.DiagText() + "\n");
                // A1：交叉比对摘要。服务器端记了多少次"挨打"、其中几次玩家真的
                //   掉了血，是判断这份数据能不能用的第一道关。
                lb.Append("xchk: " + CrossCheck.DiagText() + "\n");
                lb.Append("steamid=" + Identity.SteamId.ToString(CultureInfo.InvariantCulture) + "\n");
                SessLog.Write(Out.Dir, lb.ToString());

                // 完整配对明细单独成文：崩溃在半途也留下已经配上的部分。
                try
                {
                    CrossCheck.WriteReport(Out.Dir,
                        DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
                }
                catch { }
            }
            catch { }
        }

        /// Called from the plugin's OnDestroy so a straight quit still banks
        /// the last session instead of losing it.
        public static void FlushSession()
        {
            // v2.35: bank the cumulative figures too, or a straight quit loses
            // everything accrued since the last five-minute save.
            try { SessionStore.Save(); } catch { }
            try { CloseSession(Environment.TickCount64, "shutdown"); }
            catch { }
        }
    }

#if HAS_INTEROP
    /// A MonoBehaviour injected into the IL2CPP domain so that Unity's own
    /// Update() loop drives our sampling. Registered by InstallFrameHook.
    ///
    /// The IntPtr constructor is mandatory: IL2CPP creates the managed half of
    /// an injected type through it. Without it, AddComponent<T>() throws.
    public class ProbeRunner : MonoBehaviour
    {
        public ProbeRunner(IntPtr ptr) : base(ptr) { }

        /// v2.12: Unity's destroy message. Plain method, not an override --
        /// MonoBehaviour exposes OnDestroy as a message, not a virtual, so
        /// adding `override` here would be CS0115 as well. This is the only
        /// place that is guaranteed to be called when the component dies
        // (returning to the main menu, quitting), so it is where the last
        /// session gets banked instead of vanishing with the process.
        public void OnDestroy()
        {
            try { ProbePlugin.FlushSession(); } catch { }
        }

        public void Update()
        {
            // v2.8: F1 is polled here rather than only inside OnGUI, because
            // this hook is the one that is guaranteed to run. The in-game
            // console or a modal panel can swallow an OnGUI key event, and a
            // HUD whose only way to change view silently stopped responding is
            // exactly the kind of failure that costs an evening to find.
            try
            {
                if (ProbePlugin.CfgHud != null && ProbePlugin.CfgHud.Value) Hud.PollKey();
            }
            catch { }

            ProbePlugin.OnFrame();
        }

        /// v2.6: top-left overlay. Unity calls this during the GUI phase, the
        /// only moment drawing is legal. It touches nothing but the cached
        /// string built by the sampler, so it cannot destabilise the game.
        private UnityEngine.GUIStyle _syncStyle;
        private UnityEngine.GUIStyle _linkStyle;
        private UnityEngine.GUIStyle _xchkStyle;

        public void OnGUI()
        {
            try
            {
                // v2.40：入服对时窗口期间，在屏幕顶部画一行英文倒计时。
                //   服务器端同一时刻在日志里打 [CLOCK SYNC] Syncing clock... T-Ns，
                //   事后把两端的 UTC 信标配起来就能算出本场的时钟偏移。
                if (ClockSync.IsOpen)
                {
                    if (_syncStyle == null)
                    {
                        _syncStyle = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.label);
                        _syncStyle.fontSize = 22;
                        _syncStyle.fontStyle = UnityEngine.FontStyle.Bold;
                        _syncStyle.normal.textColor = new UnityEngine.Color(1f, 0.85f, 0.2f, 1f);
                    }
                    int left = ClockSync.RemainSec;
                    UnityEngine.GUI.Label(new UnityEngine.Rect(24f, 16f, 700f, 40f),
                        left > 0 ? ("SYNCING CLOCK... " + left.ToString())
                                 : "SYNCING CLOCK...",
                        _syncStyle);
                }

                // A1：链路状态。握手结束且失败时常驻 SERVER OFFLINE ——
                //   明确告知已降级离线，本地采集照常进行，不会无声失败。
                if (Link.Tried)
                {
                    if (_linkStyle == null)
                    {
                        _linkStyle = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.label);
                        _linkStyle.fontSize = 18;
                        _linkStyle.fontStyle = UnityEngine.FontStyle.Bold;
                    }
                    // 颜色随状态变，每次都设，避免连上/断开后颜色停留在旧值。
                    _linkStyle.normal.textColor = Link.Online
                        ? new UnityEngine.Color(0.4f, 1f, 0.5f, 1f)
                        : new UnityEngine.Color(1f, 0.35f, 0.3f, 1f);
                    float ly = ClockSync.IsOpen ? 56f : 16f;
                    UnityEngine.GUI.Label(new UnityEngine.Rect(24f, ly, 760f, 32f),
                        Link.StatusText(), _linkStyle);

                    // A1：交叉比对结果，画在链路行下面。
                    //   这一行是"服务器端数据能不能用"的直接读数：
                    //   real 是玩家真掉血的命中，phantom 是服务器端记了但没掉血的。
                    if (Link.Online && CrossCheck.Ran)
                    {
                        if (_xchkStyle == null)
                        {
                            _xchkStyle = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.label);
                            _xchkStyle.fontSize = 16;
                        }
                        _xchkStyle.normal.textColor = new UnityEngine.Color(0.85f, 0.9f, 1f, 1f);
                        UnityEngine.GUI.Label(new UnityEngine.Rect(24f, ly + 28f, 760f, 28f),
                            CrossCheck.StatusText(), _xchkStyle);
                    }
                }

                if (ProbePlugin.CfgHud != null && ProbePlugin.CfgHud.Value)
                {
                    Hud.Draw(
                        ProbePlugin.CfgHudRequireServer == null || ProbePlugin.CfgHudRequireServer.Value,
                        ProbePlugin.CfgHudFontSize != null ? ProbePlugin.CfgHudFontSize.Value : 16);
                }
            }
            catch { }
        }
    }
#endif
}
