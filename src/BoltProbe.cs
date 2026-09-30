// BoltProbe.cs -- which server am I on? (Bolt-level answer)
//
// NetProbe reads the process UDP table, but Photon Bolt uses an UNCONNECTED
// socket, so the OS table has no remote peer. The game itself already prints
// the answer to its own log:
//
//   This client connected to server (bolt):
//       [Connection [EndPoint <SERVER_IP>:11005 | 7483222601876122365]]
//
// This file reads it from Bolt directly, all by reflection on names confirmed
// against the interop assemblies: BoltNetwork.Server -> BoltConnection ->
// RemoteEndPoint / ConnectionId. Address:port is the primary join key;
// connection id is carried alongside because the server side can also
// produce it. Every step is optional -- a failure is never fatal.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SotfClientProbe
{
    internal static class BoltProbe
    {
        public static string EndPoint = "";
        public static string ConnectionId = "";
        public static string ServerName = "";
        public static int Reads;
        public static int Failures;
        public static string Report;

        private static Type _bnType;
        private static bool _bnLooked;
        private static long _lastTryMs;
        private const long RetryGapMs = 3000;

        private static readonly Regex RxEndpoint =
            new Regex(@"EndPoint\s+([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+):([0-9]+)",
                      RegexOptions.Compiled);
        private static readonly Regex RxId =
            new Regex(@"\|\s*([0-9]{6,})\s*\]", RegexOptions.Compiled);
        private static readonly Regex RxCleanEp =
            new Regex(@"^\s*\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d+\s*$",
                      RegexOptions.Compiled);

        private static Type BoltNetworkType()
        {
            if (_bnLooked) return _bnType;
            _bnLooked = true;
            _bnType = U.FindType("BoltNetwork");
            return _bnType;
        }

        public static void Refresh()
        {
            try
            {
                long now = Environment.TickCount64;
                if (EndPoint.Length > 0 && (now - _lastTryMs) < 30000) return;
                if ((now - _lastTryMs) < RetryGapMs) return;
                _lastTryMs = now;

                Type bn = BoltNetworkType();
                if (bn == null) { Failures++; return; }

                object conn = StaticServer(bn);
                if (conn == null)
                {
                    conn = FirstOf(StaticList(bn, "connections"))
                        ?? FirstOf(StaticList(bn, "clients"));
                }
                if (conn == null) { Failures++; return; }

                string ep = ReadMemberString(conn, "RemoteEndPoint", "remoteEndPoint");
                string id = ReadMemberString(conn, "ConnectionId", "connectionId");

                // Cross-check: BoltConnection.ToString() already contains both
                // values in the exact shape the game logs.
                string text = "";
                try { text = conn.ToString() ?? ""; } catch { }
                if (text.Length > 0)
                {
                    Match m = RxEndpoint.Match(text);
                    // A1.07: ep from RemoteEndPoint.ToString() may carry the
                    // "[EndPoint " prefix -- clean it whenever it is not a
                    // bare IP:port.
                    if (m.Success && !RxCleanEp.IsMatch(ep))
                        ep = m.Groups[1].Value + ":" + m.Groups[2].Value;
                    else if (ep.Length == 0 && m.Success)
                        ep = m.Groups[1].Value + ":" + m.Groups[2].Value;
                    Match mi = RxId.Match(text);
                    if (id.Length == 0 && mi.Success)
                        id = mi.Groups[1].Value;
                }

                if (ep.Length > 0) EndPoint = ep;
                if (id.Length > 0) ConnectionId = id;
                if (ep.Length > 0 || id.Length > 0) Reads++; else Failures++;
            }
            catch { Failures++; }
        }

        private static object StaticServer(Type bn)
        {
            object v; string err;
            if (U.TryGetStatic(bn, "Server", out v, out err) && v != null) return v;
            if (U.TryGetStatic(bn, "server", out v, out err) && v != null) return v;
            if (U.TryCallStatic(bn, "get_Server", out v, out err) && v != null) return v;
            if (U.TryCallStatic(bn, "get_server", out v, out err) && v != null) return v;
            return null;
        }

        private static System.Collections.IEnumerable StaticList(Type bn, string name)
        {
            object v; string err;
            if (U.TryGetStatic(bn, name, out v, out err) && v is System.Collections.IEnumerable)
                return (System.Collections.IEnumerable)v;
            string cap = char.ToUpperInvariant(name[0]) + name.Substring(1);
            if (U.TryGetStatic(bn, cap, out v, out err) && v is System.Collections.IEnumerable)
                return (System.Collections.IEnumerable)v;
            return null;
        }

        private static object FirstOf(System.Collections.IEnumerable seq)
        {
            if (seq == null) return null;
            try
            {
                System.Collections.IEnumerator it = seq.GetEnumerator();
                if (it != null && it.MoveNext()) return it.Current;
            }
            catch { }
            return null;
        }

        private static string ReadMemberString(object o, string name, string lower)
        {
            object v; string err;
            string[] names = { name, lower, "get_" + name, "get_" + lower };
            foreach (string n in names)
            {
                if (U.TryGet(o, n, out v, out err) && v != null)
                    return Stringify(v);
            }
            return "";
        }

        private static string Stringify(object v)
        {
            try
            {
                if (v is ulong) return ((ulong)v).ToString(CultureInfo.InvariantCulture);
                if (v is long) return ((long)v).ToString(CultureInfo.InvariantCulture);
                if (v is uint) return ((uint)v).ToString(CultureInfo.InvariantCulture);
                if (v is int) return ((int)v).ToString(CultureInfo.InvariantCulture);
                string s = v.ToString();
                return s == null ? "" : s;
            }
            catch { return ""; }
        }

        public static string BuildReport()
        {
            var sb = new StringBuilder();
            sb.Append("=== SotF Client Probe -- bolt server identity ===\n");
            sb.Append("time     : ").Append(U.Now()).Append('\n');

            Type bn = BoltNetworkType();
            sb.Append("BoltNetwork type : ")
              .Append(bn == null ? "NOT FOUND" : bn.FullName).Append('\n');

            object conn = null;
            if (bn != null)
            {
                conn = StaticServer(bn)
                    ?? FirstOf(StaticList(bn, "connections"))
                    ?? FirstOf(StaticList(bn, "clients"));
            }
            sb.Append("server connection: ")
              .Append(conn == null ? "null (not in a session)" : U.TypeName(conn)).Append('\n');

            if (conn != null)
            {
                sb.Append("  RemoteEndPoint : ").Append(
                    ReadMemberString(conn, "RemoteEndPoint", "remoteEndPoint")).Append('\n');
                sb.Append("  ConnectionId   : ").Append(
                    ReadMemberString(conn, "ConnectionId", "connectionId")).Append('\n');
                string text = "";
                try { text = conn.ToString() ?? ""; } catch { }
                sb.Append("  ToString       : ").Append(text).Append('\n');
            }

            sb.Append('\n');
            sb.Append("RESULT : endpoint    = ").Append(EndPoint.Length == 0 ? "(none)" : EndPoint).Append('\n');
            sb.Append("         connection  = ").Append(ConnectionId.Length == 0 ? "(none)" : ConnectionId).Append('\n');
            sb.Append("         reads/fails = ").Append(U.Num(Reads)).Append(" / ").Append(U.Num(Failures)).Append('\n');

            if (EndPoint.Length > 0)
                sb.Append("VERDICT: server identity AVAILABLE via Bolt (address:port is the key)\n");
            else
                sb.Append("VERDICT: no Bolt connection yet -- expected in the main menu; retried while playing\n");

            Report = sb.ToString();
            return Report;
        }
    }
}
