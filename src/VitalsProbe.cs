// VitalsProbe.cs -- find and read the LOCAL player's real health + position.
//
// WHY THIS FILE EXISTS
// Server-side, the player's Vitals component is a frozen placeholder that
// always reports 176/176 no matter how much damage is taken (proven across
// two players reading the identical dead value). Real health is the one thing
// the server cannot obtain, and the reason a client mod is needed at all.
//
// THE v3.21 TRAP WE MUST NOT REPEAT
// Reading the "Health" PROPERTY returns a Sons.StatSystem.HealthStat OBJECT,
// not a number. float.TryParse on it always failed, the value became NaN, and
// the collector reported hp_reads = 0 while a perfectly good component sat in
// hand. The number only comes back from the GetHealth() METHOD.
//
// So this probe does not trust any single name. It enumerates candidates,
// tries each one, and records WHAT ACTUALLY RETURNED A NUMBER. AsFloat()
// refuses non-numeric types on purpose -- that refusal is the signal that
// distinguishes "read an object" from "read a value".
//
// SAFETY (v3.23 crash): main thread only, no object-graph walking, every
// candidate capped and individually try/caught.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SotfClientProbe
{
    internal static class VitalsProbe
    {
        // Cap applied AFTER sorting, never during collection.
        private const int MAX_CANDIDATES = 60;

        // True when we resolved through LocalPlayer (the hard-wired path)
        // rather than by scanning type names. Changes how we re-acquire the
        // live object each sample.
        private static bool _viaLocalPlayer;

        // Retry bookkeeping: the old code ran discovery exactly once, at plugin
        // load, while the game was still in the main menu -- where no player
        // object exists. It then concluded "NOT FOUND" and never looked again.
        private static long _lastAttemptMs;
        private const long RETRY_GAP_MS = 3000;
        public static int Attempts;

        // Filled by the one-time probe, used by every later sample.
        private static Type _ownerType;        // type that owns the health member
        private static string _memberName;     // e.g. "GetHealth"
        private static bool _isMethod;         // method vs property/field
        private static bool _resolved;

        public static bool Resolved { get { return _resolved; } }
        public static string ResolvedPath
        {
            get
            {
                if (!_resolved) return "unresolved";
                return U.TypeName(_ownerType) + (_isMethod ? "." + _memberName + "()" : "." + _memberName);
            }
        }

        /// Cached instance of the owner type, refreshed by the sampler.
        private static object _cachedOwner;
        private static DateTime _cacheAt;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

        public static string Report;

        // --------------------------------------------------------------- probe

        /// One-time discovery. MUST be called on the Unity main thread.
        public static void Discover()
        {
            var sb = new StringBuilder();
            sb.Append("=== SotF Client Probe -- vitals discovery ===\n");
            sb.Append("time : ").Append(U.Now()).Append('\n');
            sb.Append('\n');

            // ---- v2.4: try the known entry point FIRST ----
            //
            // Scanning type names is a last resort, not a strategy. The
            // interop dump shows the game exposes a singleton with the Vitals
            // component already attached, so walk it directly.
            sb.Append("--- direct path: TheForest.Utils.LocalPlayer ---\n");
            if (ResolveViaLocalPlayer(sb))
            {
                sb.Append("     >> RESOLVED via LocalPlayer (no type scanning needed)\n");
                sb.Append('\n');
                sb.Append("=============================================\n");
                sb.Append("RESULT : health readable at ").Append(ResolvedPath).Append('\n');
                sb.Append("VERDICT: real client-side health is AVAILABLE\n");
                Report = sb.ToString();
                return;
            }
            sb.Append('\n');

            var candidates = new List<Type>();
            int typesSeen = 0, asmSeen = 0, asmFailed = 0;

            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                asmSeen++;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { asmFailed++; continue; }   // IL2CPP throws on some assemblies

                foreach (Type t in types)
                {
                    if (t == null) continue;
                    string fn;
                    try { fn = t.FullName; } catch { continue; }
                    if (string.IsNullOrEmpty(fn)) continue;
                    typesSeen++;

                    // v2.4: collect EVERYTHING, cap only after sorting.
                    //
                    // THE BUG THIS FIXES: the cap used to live here, inside the
                    // collection loop. Low-score types from whichever assemblies
                    // happened to be enumerated first filled all 40 slots, and
                    // Sons.dll -- which holds the real Vitals type -- was walked
                    // later, so it never got in. Sorting ran afterwards and was
                    // therefore pointless. The probe reported "no candidate
                    // produced a numeric health" while the type sat right there.
                    if (ScoreType(fn) > 0)
                        candidates.Add(t);
                }
            }

            sb.Append("assemblies scanned : ").Append(U.Num(asmSeen))
              .Append(" (").Append(U.Num(asmFailed)).Append(" threw)\n");
            sb.Append("types seen         : ").Append(U.Num(typesSeen)).Append('\n');
            sb.Append("candidates         : ").Append(U.Num(candidates.Count)).Append('\n');
            sb.Append('\n');

            // Prefer the strongest hints first, THEN take the top slice.
            candidates.Sort((a, b) => ScoreType(b.FullName).CompareTo(ScoreType(a.FullName)));
            if (candidates.Count > MAX_CANDIDATES)
            {
                sb.Append("note               : ").Append(U.Num(candidates.Count))
                  .Append(" scored, keeping top ").Append(U.Num(MAX_CANDIDATES)).Append('\n');
                candidates.RemoveRange(MAX_CANDIDATES, candidates.Count - MAX_CANDIDATES);
            }

            foreach (Type t in candidates)
            {
                sb.Append("---- ").Append(t.FullName).Append("  (score ").Append(U.Num(ScoreType(t.FullName))).Append(")\n");
                ProbeType(t, sb);
                if (_resolved) { sb.Append("     >> RESOLVED via this type, stopping.\n"); break; }
            }

            sb.Append('\n');
            sb.Append("=============================================\n");
            if (_resolved)
            {
                sb.Append("RESULT : health readable at ").Append(ResolvedPath).Append('\n');
                sb.Append("VERDICT: real client-side health is AVAILABLE\n");
            }
            else
            {
                sb.Append("RESULT : no candidate produced a numeric health\n");
                sb.Append('\n');
                sb.Append("VERDICT: INCONCLUSIVE at this moment.\n");
                sb.Append("         This run happened at plugin load; if the game\n");
                sb.Append("         was still in the main menu then no player object\n");
                sb.Append("         existed yet and this result means nothing.\n");
                sb.Append("         The probe now RETRIES every ").Append(U.Num((int)(RETRY_GAP_MS / 1000)))
                  .Append("s while a session is\n");
                sb.Append("         running -- see the jsonl for the real answer.\n");
                if (!string.IsNullOrEmpty(LastFailNote))
                {
                    sb.Append('\n');
                    sb.Append("last direct-path attempt:\n");
                    sb.Append(LastFailNote);
                }
            }

            Report = sb.ToString();
        }

        /// Walks LocalPlayer -> Vitals -> GetHealth(). Returns true on success.
        ///
        /// Nothing here is guessed: TheForest.Utils.LocalPlayer, its _instance
        /// singleton, and the Vitals member are all named in the interop dump.
        private static bool ResolveViaLocalPlayer(StringBuilder sb)
        {
            try
            {
                object lp = LocalPlayer.Instance();
                if (lp == null)
                {
                    sb.Append("     LocalPlayer instance is null\n");
                    sb.Append("     (expected in the main menu -- will retry in game)\n");
                    return false;
                }
                sb.Append("     instance  : ").Append(U.TypeName(lp)).Append('\n');

                object vit = LocalPlayer.Vitals();
                if (vit == null)
                {
                    sb.Append("     Vitals member is null\n");
                    return false;
                }
                sb.Append("     vitals    : ").Append(U.TypeName(vit)).Append('\n');

                // GetHealth is the METHOD. The Health PROPERTY returns a
                // HealthStat object and would be rejected by AsFloat anyway --
                // that is the v3.21 trap, recorded here so it is not repeated.
                foreach (string mn in new string[] { "GetHealth", "GetMaxHealth" })
                {
                    object val; string err;
                    if (!U.TryCall(vit, mn, out val, out err))
                    {
                        sb.Append("     ").Append(mn).Append("() -> ").Append(err).Append('\n');
                        continue;
                    }
                    float f;
                    bool ok = U.AsFloat(val, out f);
                    sb.Append("     ").Append(mn).Append("() -> ")
                      .Append(ok ? U.Num(f) : "[" + U.TypeName(val) + "]").Append('\n');

                    if (ok && mn == "GetHealth" && IsPlausibleHealth(f))
                    {
                        _ownerType = vit.GetType();
                        _memberName = mn;
                        _isMethod = true;
                        _resolved = true;
                        _viaLocalPlayer = true;
                        _cachedOwner = vit;
                        _cacheAt = DateTime.Now;
                        return true;
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                sb.Append("     direct path threw: ").Append(U.Short(e)).Append('\n');
                return false;
            }
        }

        /// Called from the frame hook. Retries the cheap direct path while
        /// unresolved -- the player object only exists once a session starts,
        /// and the old one-shot-at-load probe always missed it.
        /// Throttled: reflection is not free and this runs every frame.
        public static void Tick()
        {
            if (_resolved) return;
            long now = Environment.TickCount64;
            if (_lastAttemptMs != 0 && (now - _lastAttemptMs) < RETRY_GAP_MS) return;
            _lastAttemptMs = now;
            Attempts++;
            try
            {
                var sb = new StringBuilder();
                if (ResolveViaLocalPlayer(sb))
                    RetryNote = "vitals resolved on retry #" + U.Num(Attempts)
                                + " at " + ResolvedPath;
                else
                    LastFailNote = sb.ToString();
            }
            catch { }
        }

        /// Set when a retry succeeds. ProbePlugin prints and clears it, because
        /// this class has no logger of its own.
        public static string RetryNote;

        /// Why the last retry failed -- written into the report so a failed
        /// probe still tells us something.
        public static string LastFailNote;

        /// Rough relevance score. Higher = tried earlier.
        ///
        /// v1.4: the exact type is now KNOWN, not guessed. The client interop
        /// assemblies (235 dlls, generated by Il2CppInterop from the client's
        /// own GameAssembly.dll) contain "Sons.Vitals", and its method table
        /// reads:
        ///
        ///     GetHealth_Public_Single_0               <- float GetHealth()
        ///     get_Health_Private_get_HealthStat_0     <- returns HealthStat OBJECT
        ///     GetMaxHealth_Public_Single_0
        ///     SetHealth_Public_Void_Single_0
        ///
        /// So "Sons.Vitals" gets the top score and is tried first. Every other
        /// type stays as a fallback in case a future patch renames it -- we
        /// never again bet the whole probe on one guessed name (that is how
        /// IsParrying was read as "always false" for versions).
        private static int ScoreType(string fn)
        {
            // Exact, interop-confirmed owner type. Nothing beats this.
            //
            // v2.4 correction: the dump lists it as "Vitals" with NO namespace,
            // even though it lives in Sons.dll. The old code matched
            // "Sons.Vitals" and could therefore never hit. We now match the
            // real name and, separately, try LocalPlayer first anyway -- so
            // even if this line is wrong on a future patch, health still works.
            if (fn == "Vitals") return 10000;
            if (fn != null && fn.EndsWith(".Vitals", StringComparison.Ordinal)) return 9000;

            int s = 0;
            if (fn.IndexOf("Vitals", StringComparison.OrdinalIgnoreCase) >= 0) s += 100;
            if (fn.IndexOf("LocalPlayer", StringComparison.OrdinalIgnoreCase) >= 0) s += 60;
            if (fn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0) s += 30;
            if (fn.IndexOf("Health", StringComparison.OrdinalIgnoreCase) >= 0) s += 25;
            if (fn.IndexOf("StatSystem", StringComparison.OrdinalIgnoreCase) >= 0) s += 20;
            if (fn.IndexOf("Vail", StringComparison.OrdinalIgnoreCase) >= 0) s += 10;
            // Skip framework noise.
            if (fn.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase)) s = 0;
            if (fn.StartsWith("Il2Cpp", StringComparison.OrdinalIgnoreCase)) s = 0;
            if (fn.StartsWith("Harmony", StringComparison.OrdinalIgnoreCase)) s = 0;
            if (fn.StartsWith("System", StringComparison.OrdinalIgnoreCase)) s = 0;
            if (fn.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)) s = 0;
            if (fn.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase)) s = 0;
            if (fn.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) s = 0;
            return s;
        }

        private static void ProbeType(Type t, StringBuilder sb)
        {
            // 1) collect zero-arg members that could carry health
            var methodNames = new List<string>();
            var memberNames = new List<string>();

            try
            {
                foreach (MethodInfo m in t.GetMethods(U.BF))
                {
                    if (m == null) continue;
                    if (m.GetParameters().Length != 0) continue;
                    if (m.IsGenericMethodDefinition) continue;
                    if (m.ReturnType == typeof(void)) continue;
                    string n = m.Name;
                    if (Healthish(n)) methodNames.Add(n);
                }
                // v2.14: never call a mutator. Healthish() matches anything
                // containing "health", which includes SetFullHealth() --
                // zero-argument, and a probe that runs it refills the player
                // as a side effect of looking for a number. The void filter
                // above happens to catch it today; this makes it structural.
                for (int i = methodNames.Count - 1; i >= 0; i--)
                {
                    if (IsMutatorName(methodNames[i])) methodNames.RemoveAt(i);
                }
            }
            catch (Exception e) { sb.Append("     methods enum threw ").Append(U.Short(e)).Append('\n'); }

            try
            {
                foreach (PropertyInfo p in t.GetProperties(U.BF))
                {
                    if (p == null || !p.CanRead) continue;
                    if (p.GetIndexParameters().Length != 0) continue;
                    if (Healthish(p.Name)) memberNames.Add(p.Name);
                }
                foreach (FieldInfo f in t.GetFields(U.BF))
                {
                    if (f == null) continue;
                    if (Healthish(f.Name)) memberNames.Add(f.Name);
                }
            }
            catch (Exception e) { sb.Append("     members enum threw ").Append(U.Short(e)).Append('\n'); }

            sb.Append("     health-ish methods : ")
              .Append(methodNames.Count == 0 ? "(none)" : string.Join(", ", methodNames.ToArray())).Append('\n');
            sb.Append("     health-ish members : ")
              .Append(memberNames.Count == 0 ? "(none)" : string.Join(", ", memberNames.ToArray())).Append('\n');

            if (methodNames.Count == 0 && memberNames.Count == 0) return;

            // 2) get live instances (main thread, capped)
            object[] instances;
            if (!TryGetInstances(t, out instances, sb)) return;

            sb.Append("     instances          : ").Append(U.Num(instances.Length)).Append('\n');

            // 3) try methods first -- that is where the real number lives.
            //    v1.4: interop shows "GetHealth" returns Single on Sons.Vitals,
            //    so try that exact name before the rest. The property "Health"
            //    returns a HealthStat object and will be rejected by AsFloat
            //    anyway -- it cannot win, it can only waste time.
            if (methodNames.Contains("GetHealth"))
            {
                methodNames.Remove("GetHealth");
                methodNames.Insert(0, "GetHealth");
            }

            foreach (object inst in instances)
            {
                if (inst == null) continue;
                foreach (string mn in methodNames)
                {
                    object val; string err;
                    if (!U.TryCall(inst, mn, out val, out err))
                    {
                        sb.Append("       call ").Append(mn).Append("() -> FAIL ").Append(err).Append('\n');
                        continue;
                    }
                    float f;
                    bool ok = U.AsFloat(val, out f);
                    sb.Append("       call ").Append(mn).Append("() -> ").Append(U.TypeName(val))
                      .Append(" = ").Append(ok ? U.Num(f) : "[not a number]").Append('\n');

                    if (ok && !_resolved && IsPlausibleHealth(f))
                    {
                        _ownerType = t; _memberName = mn; _isMethod = true;
                        _resolved = true;
                        _cachedOwner = inst; _cacheAt = DateTime.Now;
                        sb.Append("       >> ACCEPTED as health\n");
                        return;
                    }
                }

                // 4) properties/fields as fallback
                foreach (string pn in memberNames)
                {
                    object val; string err;
                    if (!U.TryGet(inst, pn, out val, out err))
                    {
                        sb.Append("       read ").Append(pn).Append(" -> FAIL ").Append(err).Append('\n');
                        continue;
                    }
                    float f;
                    bool ok = U.AsFloat(val, out f);
                    sb.Append("       read ").Append(pn).Append(" -> ").Append(U.TypeName(val))
                      .Append(" = ").Append(ok ? U.Num(f) : "[not a number]").Append('\n');

                    if (ok && !_resolved && IsPlausibleHealth(f))
                    {
                        _ownerType = t; _memberName = pn; _isMethod = false;
                        _resolved = true;
                        _cachedOwner = inst; _cacheAt = DateTime.Now;
                        sb.Append("       >> ACCEPTED as health\n");
                        return;
                    }
                }
            }
        }

        /// Locates UnityEngine.Object across the IL2CPP interop assemblies.
        /// Tries the usual assembly names before falling back to a scan.
        private static Type FindUnityObjectType()
        {
            string[] probes =
            {
                "UnityEngine.Object, UnityEngine.CoreModule",
                "UnityEngine.Object, UnityEngine",
                "UnityEngine.Object, UnityEngine.CoreModule.dll",
            };
            foreach (string p in probes)
            {
                Type t;
                try { t = Type.GetType(p, false); }
                catch { t = null; }
                if (t != null) return t;
            }

            // Fallback: scan for it. Only the type is touched, never an instance.
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }
                    foreach (Type t in types)
                    {
                        if (t == null) continue;
                        if (t.Name == "Object" && t.Namespace == "UnityEngine") return t;
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool Healthish(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            if (n.IndexOf("health") >= 0) return true;
            if (n.IndexOf("_hp") >= 0) return true;
            if (n == "hp") return true;
            if (n.IndexOf("currenthp") >= 0) return true;
            return false;
        }

        /// v2.14: true for names that change the game. Calling one to see what
        /// it returns is how a diagnostic turns into an exploit.
        private static bool IsMutatorName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (n.StartsWith("Set", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Apply", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Trigger", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Spend", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Gain", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Remove", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Clear", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Reset", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Modify", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Kill", StringComparison.Ordinal)) return true;
            if (n.StartsWith("Force", StringComparison.Ordinal)) return true;
            return false;
        }

        /// Health is a small positive number. 176 is the known server placeholder;
        /// we accept a wide band so a modded maxhp still passes, but we reject
        /// 0/absurd values that are obviously an uninitialised read.
        private static bool IsPlausibleHealth(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return false;
            return f > 0f && f < 100000f;
        }

        /// Instances via FindObjectsOfType. Capped hard -- an unbounded query on
        /// a broad type can return thousands and stall the frame.
        private static bool TryGetInstances(Type t, out object[] found, StringBuilder sb)
        {
            found = null;
            try
            {
                // Resolved by NAME at runtime rather than with typeof(), so this
                // project compiles without referencing the game's interop
                // assemblies. Fewer build-time dependencies = fewer ways for the
                // user's first build to fail.
                Type unityObject = FindUnityObjectType();
                if (unityObject == null)
                {
                    sb.Append("     UnityEngine.Object type not found in this process\n");
                    return false;
                }

                MethodInfo fot = unityObject.GetMethod(
                    "FindObjectsOfType", U.BF, null, new Type[] { typeof(Type) }, null);
                if (fot == null)
                {
                    sb.Append("     FindObjectsOfType(Type) not available\n");
                    return false;
                }
                object raw = fot.Invoke(null, new object[] { t });
                var arr = raw as object[];
                if (arr == null) { found = new object[0]; return true; }
                if (arr.Length > 32)
                {
                    var cut = new object[32];
                    Array.Copy(arr, cut, 32);
                    sb.Append("     note    : capped ").Append(U.Num(arr.Length)).Append(" -> 32\n");
                    arr = cut;
                }
                found = arr;
                return true;
            }
            catch (Exception e)
            {
                sb.Append("     instances threw ").Append(U.Short(e)).Append('\n');
                return false;
            }
        }

        // -------------------------------------------------------------- sample

        /// Per-tick read using the resolved path. Cheap: one cached instance,
        /// refreshed at most every 10s, one member read.
        public static bool Sample(out float hp)
        {
            hp = float.NaN;
            if (!_resolved) return false;
            try
            {
                if (_cachedOwner == null || DateTime.Now - _cacheAt > CacheTtl)
                {
                    if (_viaLocalPlayer)
                    {
                        // Re-acquire through the singleton. Cheaper and far more
                        // reliable than FindObjectsOfType, which can return a
                        // different (wrong) instance of the same component.
                        object vit = LocalPlayer.Vitals();
                        if (vit == null) return false;
                        _cachedOwner = vit;
                        _cacheAt = DateTime.Now;
                    }
                    else
                    {
                        object[] arr;
                        var sb = new StringBuilder();
                        if (!TryGetInstances(_ownerType, out arr, sb) || arr.Length == 0) return false;
                        _cachedOwner = arr[0];
                        _cacheAt = DateTime.Now;
                    }
                }
                object val; string err;
                bool got = _isMethod
                    ? U.TryCall(_cachedOwner, _memberName, out val, out err)
                    : U.TryGet(_cachedOwner, _memberName, out val, out err);
                if (!got) return false;
                return U.AsFloat(val, out hp);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------- position

        /// Local player position, read from any candidate that exposes a
        /// transform. Returns false when not in a world yet.
        public static bool SamplePosition(out float x, out float y, out float z)
        {
            x = y = z = float.NaN;
            try
            {
                // v2.4: position must work even when health never resolved.
                //
                // It used to bail out on _cachedOwner == null, which meant that
                // in any session where health was not found we also threw away
                // every position sample. That is precisely the data the server
                // side CAN corroborate (server player_hit carries player_pos),
                // so discarding it lost the one cross-check available without
                // Bolt IDs. Health and position are now sourced independently.
                object src = _cachedOwner;
                if (_viaLocalPlayer || src == null)
                {
                    object pb = LocalPlayer.PlayerBase();
                    if (pb != null) src = pb;
                    else if (_viaLocalPlayer)
                    {
                        object lp = LocalPlayer.Instance();
                        if (lp != null) src = lp;
                    }
                }
                if (src == null) return false;

                // Walk exactly ONE level to transform, then read position.
                // v3.23 crashed by walking many levels; one is deliberate.
                object tf; string err;
                if (!U.TryGet(src, "transform", out tf, out err) &&
                    !U.TryGet(src, "Transform", out tf, out err))
                    return false;
                if (tf == null) return false;

                object pos;
                if (!U.TryGet(tf, "position", out pos, out err) &&
                    !U.TryGet(tf, "Position", out pos, out err))
                    return false;
                if (pos == null) return false;

                object vx, vy, vz;
                if (!U.TryGet(pos, "x", out vx, out err)) return false;
                if (!U.TryGet(pos, "y", out vy, out err)) return false;
                if (!U.TryGet(pos, "z", out vz, out err)) return false;

                float fx, fy, fz;
                if (!U.AsFloat(vx, out fx) || !U.AsFloat(vy, out fy) || !U.AsFloat(vz, out fz))
                    return false;

                x = fx; y = fy; z = fz;
                return true;
            }
            catch { return false; }
        }

        /// v2.17: horizontal facing, in degrees.
        ///
        /// Position alone cannot tell "back turned" from "facing the attack",
        /// and that difference is most of what separates a player who was
        /// caught out of position from one who simply ate a hit he could see
        /// coming. It also gives the dodge question something to lean on: a
        /// player whose facing tracks the attacker is behaving differently from
        /// one who never turned, and no amount of position data shows that.
        ///
        /// The transform is already resolved by the position read, so this is
        /// one further member lookup on an object we are holding.
        ///
        /// eulerAngles.y is world-space yaw. forward would be a direction
        /// vector; yaw is what survives a JSON round trip and what can be
        /// compared against a bearing without re-deriving it.
        ///
        /// Unreadable means absent, never zero -- a yaw of 0 is a real facing
        /// (north), and writing it when nothing was read would invent a
        /// direction the player may never have pointed in.
        public static bool SampleFacing(out float yaw)
        {
            yaw = float.NaN;
            try
            {
                object src = _cachedOwner;
                if (_viaLocalPlayer || src == null)
                {
                    object pb = LocalPlayer.PlayerBase();
                    if (pb != null) src = pb;
                    else if (_viaLocalPlayer)
                    {
                        object lp = LocalPlayer.Instance();
                        if (lp != null) src = lp;
                    }
                }
                if (src == null) return false;

                // One level, same as the position read. v3.23 crashed by
                // walking deep object graphs; one hop is deliberate.
                object tf; string err;
                if (!U.TryGet(src, "transform", out tf, out err) &&
                    !U.TryGet(src, "Transform", out tf, out err))
                    return false;
                if (tf == null) return false;

                // eulerAngles rather than rotation: a quaternion would need
                // converting, and the conversion is a place to get the handedness
                // wrong in a way that looks plausible in the output.
                object eu, ey;
                if (!U.TryGet(tf, "eulerAngles", out eu, out err) &&
                    !U.TryGet(tf, "EulerAngles", out eu, out err))
                    return false;
                if (eu == null) return false;
                if (!U.TryGet(eu, "y", out ey, out err) &&
                    !U.TryGet(eu, "Y", out ey, out err))
                    return false;

                float f;
                if (!U.AsFloat(ey, out f)) return false;
                if (float.IsNaN(f) || float.IsInfinity(f)) return false;

                yaw = f;
                return true;
            }
            catch { return false; }
        }
    }
}
