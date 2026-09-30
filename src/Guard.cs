// Guard.cs -- tamper detection and self-disable.
// REQUIREMENT (verbatim from the user):
//   "core anti-cheat: guarantee no other mod can be installed. If one is,
//    our mod dies immediately."
// So the mod refuses to produce data at all when another mod is present.
//
// TWO TIERS:
// Tier 1 (fatal, self-disable): extra DLL in BepInEx\plugins\, foreign
//   loader directory/file, doorstop no longer pointing at BepInEx, cheat-tool
//   process running. No false positives from legitimate software.
// Tier 2 (recorded, keep running): unknown non-system modules loaded into the
//   process (overlays/drivers). Advisory only -- honest players must not be
//   disabled over Discord/Steam/NVIDIA/RTSS.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SotfClientProbe
{
    internal static class Guard
    {
        public static bool Disabled;
        public static string Reason = "";
        public static string Report;

        private static readonly string[] AllowedPlugins =
        {
            "sotfclientprobe.dll",
        };

        private static readonly string[] ForeignLoaderDirs =
        {
            "Mods", "mods", "scripts", "Scripts",
            "Redloader", "RedLoader", "redloader",
            "plugins", "Plugins",
            "shaderfix", "ue4ss",
        };

        private static readonly string[] ForeignLoaderFiles =
        {
            "UE4SS-settings.ini", "ue4ss.dll", "dwmapi.dll",
            "RedLoader.dll", "redloader.dll",
        };

        private static readonly string[] CheatProcessHints =
        {
            "fling", "angel", "cheatengine", "cheat engine",
            "artmoney", "wemod", "trainer", "hack", "injector",
        };

        private static readonly string[] TolerateModuleParts =
        {
            "system32", "syswow64", "winsxs",
            "nvidia", "nvapi", "amd", "ati", "intel",
            "discord", "steam", "overlay",
            "rtss", "afterburner", "msi",
            "xinput", "dinput", "dsound", "xaudio",
            "bepinex", "il2cpp", "harmony", "mono",
            "unity", "gameassembly", "unityplayer",
            "sotfclientprobe",
            "kernel32", "user32", "gdi32", "advapi32", "ole32", "oleaut32",
            "msvcp", "vcruntime", "ucrtbase", "ntdll", "ws2_32", "winhttp",
            "version", "dbghelp", "crypt32", "bcrypt", "shell32", "shlwapi",
            "combase", "rpcrt4", "sechost", "imm32", "msctf", "dwmapi",
            "dxgi", "d3d11", "d3d12", "opengl32", "glu32", "winmm",
            "setupapi", "cfgmgr32", "propsys", "oleacc", "uxtheme",
        };

        public static void RunFullCheck()
        {
            var fatal = new List<string>();
            var notes = new List<string>();

            CheckPluginDir(fatal, notes);
            CheckForeignLoaders(fatal, notes);
            CheckDoorstop(fatal, notes);
            CheckProcesses(fatal, notes);
            CheckModules(notes);

            var sb = new StringBuilder();
            sb.Append("=== SotF Client Probe -- environment guard ===\n");
            sb.Append("time   : ").Append(U.Now()).Append('\n');
            sb.Append("game   : ").Append(GameRoot).Append('\n');
            sb.Append('\n');

            sb.Append("-- fatal findings (mod present) --\n");
            if (fatal.Count == 0) sb.Append("   none\n");
            foreach (string f in fatal) sb.Append("   [FATAL] ").Append(f).Append('\n');

            sb.Append('\n');
            sb.Append("-- advisory findings (recorded, not fatal) --\n");
            if (notes.Count == 0) sb.Append("   none\n");
            foreach (string n in notes) sb.Append("   [note]  ").Append(n).Append('\n');

            sb.Append('\n');
            sb.Append("=============================================\n");
            if (fatal.Count > 0)
            {
                Disabled = true;
                Reason = string.Join(" | ", fatal.ToArray());
                sb.Append("STATE  : DISABLED -- no data will be written\n");
                sb.Append("REASON : ").Append(Reason).Append('\n');
                sb.Append("ACTION : remove the offending mod and restart the game\n");
            }
            else
            {
                sb.Append("STATE  : clean -- collection permitted\n");
            }

            Report = sb.ToString();
        }

        public static void ReCheck()
        {
            if (Disabled) return;
            var fatal = new List<string>();
            var notes = new List<string>();
            CheckPluginDir(fatal, notes);
            CheckProcesses(fatal, notes);
            if (fatal.Count > 0)
            {
                Disabled = true;
                Reason = string.Join(" | ", fatal.ToArray());
            }
        }

        public static string GameRoot
        {
            get
            {
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var di = new DirectoryInfo(baseDir);
                    for (int i = 0; i < 4 && di != null; i++)
                    {
                        if (File.Exists(Path.Combine(di.FullName, "SonsOfTheForest.exe")) ||
                            File.Exists(Path.Combine(di.FullName, "GameAssembly.dll")))
                            return di.FullName;
                        di = di.Parent;
                    }
                    return baseDir;
                }
                catch { return ""; }
            }
        }

        private static void CheckPluginDir(List<string> fatal, List<string> notes)
        {
            try
            {
                string dir = Path.Combine(GameRoot, "BepInEx", "plugins");
                if (!Directory.Exists(dir))
                {
                    notes.Add("plugins dir missing (fresh install?)");
                    return;
                }
                var files = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories);
                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    if (string.IsNullOrEmpty(name)) continue;
                    bool ok = false;
                    foreach (string allow in AllowedPlugins)
                    {
                        if (string.Equals(name, allow, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                    }
                    if (!ok) fatal.Add("extra plugin installed: " + name);
                }
            }
            catch (Exception e)
            {
                notes.Add("plugin dir check threw " + U.Short(e));
            }
        }

        private static void CheckForeignLoaders(List<string> fatal, List<string> notes)
        {
            try
            {
                string root = GameRoot;
                if (string.IsNullOrEmpty(root)) return;

                foreach (string d in ForeignLoaderDirs)
                {
                    string p = Path.Combine(root, d);
                    if (Directory.Exists(p))
                    {
                        if (string.Equals(d, "plugins", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(d, "Plugins", StringComparison.OrdinalIgnoreCase)) continue;
                        fatal.Add("foreign mod-loader directory present: " + d);
                    }
                }
                foreach (string f in ForeignLoaderFiles)
                {
                    string p = Path.Combine(root, f);
                    if (File.Exists(p)) fatal.Add("foreign loader file present: " + f);
                }
            }
            catch (Exception e)
            {
                notes.Add("loader check threw " + U.Short(e));
            }
        }

        private static void CheckDoorstop(List<string> fatal, List<string> notes)
        {
            try
            {
                string p = Path.Combine(GameRoot, "doorstop_config.ini");
                if (!File.Exists(p))
                {
                    notes.Add("doorstop_config.ini not found");
                    return;
                }
                string txt = File.ReadAllText(p);
                if (txt.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) < 0)
                    fatal.Add("doorstop_config.ini no longer targets BepInEx");
            }
            catch (Exception e)
            {
                notes.Add("doorstop check threw " + U.Short(e));
            }
        }

        private static void CheckProcesses(List<string> fatal, List<string> notes)
        {
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    string n;
                    try { n = p.ProcessName; } catch { continue; }
                    if (string.IsNullOrEmpty(n)) continue;
                    string lower = n.ToLowerInvariant();
                    foreach (string hint in CheatProcessHints)
                    {
                        if (lower.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            fatal.Add("cheat tool process running: " + n);
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                notes.Add("process check threw " + U.Short(e));
            }
        }

        private static void CheckModules(List<string> notes)
        {
            try
            {
                Process p = Process.GetCurrentProcess();
                foreach (ProcessModule m in p.Modules)
                {
                    string fn;
                    try { fn = m.FileName; } catch { continue; }
                    if (string.IsNullOrEmpty(fn)) continue;
                    string lower = fn.ToLowerInvariant();

                    bool tolerated = false;
                    foreach (string part in TolerateModuleParts)
                    {
                        if (lower.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            tolerated = true;
                            break;
                        }
                    }
                    if (!tolerated) notes.Add("unfamiliar module: " + fn);
                }
            }
            catch (Exception e)
            {
                notes.Add("module check threw " + U.Short(e));
            }
        }
    }
}
