// Diag.cs -- one registry of what worked and what did not.
//
// v2.17.1 had a probe_diag.txt that listed every module in one screen, so a
// failed run said WHICH module failed and WHY. This is that gathering point,
// and it feeds three consumers:
//   probe_diag.txt   the one file to read after a session
//   F1 panel         in-game list of everything that WORKED
//   F2 panel         in-game list of everything that FAILED, with the reason
//
// REGISTRATION IS IDEMPOTENT: every Set() overwrites the previous state for
// that key, so a module that resolves late flips from pending to ok without
// leaving a duplicate.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SotfClientProbe
{
    internal static class Diag
    {
        public enum St { Pending, Ok, Warn, Fail }

        public sealed class Item
        {
            public string Group;
            public string Name;
            public St State;
            public string Detail;
            public long SetMs;
        }

        private static readonly Dictionary<string, Item> _map =
            new Dictionary<string, Item>(StringComparer.Ordinal);
        private static readonly List<string> _order = new List<string>();
        private static long _lastWriteMs;
        private static int _writes;

        public static void Set(string group, string name, St state, string detail)
        {
            try
            {
                string key = group + "|" + name;
                Item it;
                if (!_map.TryGetValue(key, out it))
                {
                    it = new Item();
                    it.Group = group;
                    it.Name = name;
                    _map[key] = it;
                    _order.Add(key);
                }
                it.State = state;
                it.Detail = detail ?? "";
                it.SetMs = Environment.TickCount64;
            }
            catch { }
        }

        public static void Ok(string group, string name, string detail) { Set(group, name, St.Ok, detail); }
        public static void Fail(string group, string name, string detail) { Set(group, name, St.Fail, detail); }
        public static void Warn(string group, string name, string detail) { Set(group, name, St.Warn, detail); }

        public static void Check(string group, string name, bool ok,
                                 string okDetail, string failDetail)
        {
            Set(group, name, ok ? St.Ok : St.Fail, ok ? okDetail : failDetail);
        }

        public static List<Item> All()
        {
            var list = new List<Item>();
            try
            {
                foreach (string k in _order)
                {
                    Item it;
                    if (_map.TryGetValue(k, out it)) list.Add(it);
                }
            }
            catch { }
            return list;
        }

        public static List<Item> Succeeded() { return Where(St.Ok); }

        public static List<Item> Failed()
        {
            var list = Where(St.Fail);
            list.AddRange(Where(St.Warn));
            return list;
        }

        private static List<Item> Where(St st)
        {
            var list = new List<Item>();
            try
            {
                foreach (string k in _order)
                {
                    Item it;
                    if (_map.TryGetValue(k, out it) && it.State == st) list.Add(it);
                }
            }
            catch { }
            return list;
        }

        public static int CountOk()
        {
            int n = 0;
            try { foreach (Item it in _map.Values) if (it.State == St.Ok) n++; } catch { }
            return n;
        }

        public static int CountBad()
        {
            int n = 0;
            try
            {
                foreach (Item it in _map.Values)
                    if (it.State == St.Fail || it.State == St.Warn) n++;
            }
            catch { }
            return n;
        }

        public static string Line(Item it)
        {
            if (it == null) return "";
            string d = it.Detail ?? "";
            if (d.Length > 46) d = d.Substring(0, 45) + "...";
            return it.Name + " : " + d;
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.Append("=== probe diag -- what worked, what did not ===\n");
            sb.Append("time : ").Append(U.Now()).Append('\n');
            sb.Append("ok   : ").Append(U.Num(CountOk()))
              .Append("     failed/warn: ").Append(U.Num(CountBad())).Append('\n');
            sb.Append('\n');

            sb.Append("SUCCEEDED (F1)\n");
            foreach (Item it in Succeeded())
                sb.Append("  [OK]   ").Append(it.Group).Append('/').Append(it.Name)
                  .Append(it.Detail.Length > 0 ? "  " + it.Detail : "").Append('\n');
            if (CountOk() == 0) sb.Append("  (none)\n");

            sb.Append('\n');
            sb.Append("FAILED (F2)\n");
            foreach (Item it in Failed())
                sb.Append("  [").Append(Tag(it.State)).Append("] ")
                  .Append(it.Group).Append('/').Append(it.Name)
                  .Append(it.Detail.Length > 0 ? "  " + it.Detail : "").Append('\n');
            if (CountBad() == 0) sb.Append("  (none)\n");

            sb.Append('\n');
            sb.Append("PENDING\n");
            bool any = false;
            foreach (Item it in Where(St.Pending))
            {
                sb.Append("  [..]   ").Append(it.Group).Append('/')
                  .Append(it.Name).Append('\n');
                any = true;
            }
            if (!any) sb.Append("  (none)\n");

            return sb.ToString();
        }

        public static string Tag(St st)
        {
            switch (st)
            {
                case St.Ok: return "OK";
                case St.Warn: return "WARN";
                case St.Fail: return "FAIL";
                default: return "..";
            }
        }

        public static void Flush(bool force)
        {
            try
            {
                long now = Environment.TickCount64;
                if (!force)
                {
                    if (_writes > 0 && now - _lastWriteMs < 20000) return;
                }
                _lastWriteMs = now;
                _writes++;
                Out.WriteReport("probe_diag.txt", Report());
            }
            catch { }
        }
    }
}
