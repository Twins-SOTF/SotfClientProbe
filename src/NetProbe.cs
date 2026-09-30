// NetProbe.cs -- which server am I connected to?
//
// Sons of the Forest uses Photon Bolt over UDP. A connected UDP socket
// exposes its remote endpoint; we enumerate the process's UDP table via
// GetExtendedUdpTable and keep rows owned by this pid that have a non-zero
// remote address. Pure Win32 -- a failure here is a returned null, never a
// crash. IP:port is the stable join key for ranking-server identity.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SotfClientProbe
{
    internal static class NetProbe
    {
        public static string RemoteEndPoint = "";
        public static string LocalEndPoint = "";
        public static int RemoteCount;
        public static string Report;

        private const uint AF_INET = 2;
        private const uint UDP_TABLE_OWNER_PID = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDPROW_OWNER_PID
        {
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
            public uint dwOwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(
            IntPtr pUdpTable, ref int pdwSize, bool bOrder,
            uint ulAf, uint tableClass, uint reserved);

        private static int Ntohs(uint net)
        {
            return (int)(((net & 0xFFu) << 8) | ((net >> 8) & 0xFFu));
        }

        private static string IpToString(uint addr)
        {
            var b = BitConverter.GetBytes(addr);
            return b[0] + "." + b[1] + "." + b[2] + "." + b[3];
        }

        public static void Probe()
        {
            var sb = new StringBuilder();
            RemoteEndPoint = "";
            LocalEndPoint = "";
            RemoteCount = 0;

            sb.Append("=== SotF Client Probe -- network endpoints ===\n");
            sb.Append("time : ").Append(U.Now()).Append('\n');
            sb.Append('\n');

            int myPid;
            try { myPid = System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { myPid = -1; }
            sb.Append("pid  : ").Append(U.Num(myPid)).Append('\n');
            sb.Append('\n');

            var remote = new List<string>();
            var local = new List<string>();

            try
            {
                int size = 0;
                uint r = GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
                if (r != 0 && r != 122 /* ERROR_INSUFFICIENT_BUFFER */)
                {
                    sb.Append("GetExtendedUdpTable sizing failed, code=").Append(r).Append('\n');
                }
                else if (size <= 0)
                {
                    sb.Append("GetExtendedUdpTable returned size 0\n");
                }
                else
                {
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        r = GetExtendedUdpTable(buf, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
                        if (r != 0)
                        {
                            sb.Append("GetExtendedUdpTable fill failed, code=").Append(r).Append('\n');
                        }
                        else
                        {
                            int count = Marshal.ReadInt32(buf);
                            sb.Append("udp rows : ").Append(U.Num(count)).Append('\n');
                            sb.Append('\n');

                            int rowSize = Marshal.SizeOf(typeof(MIB_UDPROW_OWNER_PID));
                            int cap = Math.Min(count, 512);

                            for (int i = 0; i < cap; i++)
                            {
                                IntPtr p = new IntPtr(buf.ToInt64() + 4 + (long)i * rowSize);
                                var row = (MIB_UDPROW_OWNER_PID)Marshal.PtrToStructure(
                                    p, typeof(MIB_UDPROW_OWNER_PID));

                                bool mine = myPid < 0 || row.dwOwningPid == (uint)myPid;
                                if (!mine) continue;

                                string lo = IpToString(row.dwLocalAddr) + ":" + U.Num(Ntohs(row.dwLocalPort));
                                local.Add(lo);

                                if (row.dwRemoteAddr != 0)
                                {
                                    string re = IpToString(row.dwRemoteAddr) + ":" + U.Num(Ntohs(row.dwRemotePort));
                                    remote.Add(re);
                                    sb.Append("  REMOTE  ").Append(lo).Append("  ->  ").Append(re).Append('\n');
                                }
                                else
                                {
                                    sb.Append("  listen  ").Append(lo).Append("   (no remote peer)\n");
                                }
                            }
                            if (count > cap) sb.Append("  ... capped at ").Append(U.Num(cap)).Append(" rows\n");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append("EXCEPTION : ").Append(U.Short(e)).Append('\n');
            }

            RemoteCount = remote.Count;
            if (remote.Count > 0)
            {
                RemoteEndPoint = PickBest(remote);
            }
            if (local.Count > 0) LocalEndPoint = local[0];

            sb.Append('\n');
            sb.Append("=============================================\n");
            if (remote.Count > 0)
            {
                sb.Append("RESULT : remote peers found = ").Append(U.Num(remote.Count)).Append('\n');
                sb.Append("CHOSEN : ").Append(RemoteEndPoint).Append('\n');
                sb.Append("VERDICT: server identity AVAILABLE -- use this as the join key\n");
                sb.Append("         with the server-side collector's events.\n");
            }
            else
            {
                sb.Append("RESULT : no connected UDP socket with a remote peer\n");
                sb.Append("VERDICT: Bolt may use an unconnected socket. Fall back to\n");
                sb.Append("         time+position fingerprint matching, or read the endpoint\n");
                sb.Append("         out of Bolt's own session object in a later version.\n");
            }

            Report = sb.ToString();
        }

        private static string PickBest(List<string> peers)
        {
            try
            {
                var tally = new Dictionary<string, int>();
                foreach (string p in peers)
                {
                    int n;
                    tally.TryGetValue(p, out n);
                    tally[p] = n + 1;
                }
                string best = peers[0];
                int bestN = 0;
                foreach (var kv in tally)
                {
                    if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
                }
                return best;
            }
            catch { return peers.Count > 0 ? peers[0] : ""; }
        }

        public static void Refresh()
        {
            try
            {
                var saved = Report;
                Probe();
                Report = saved;
            }
            catch { }
        }
    }
}
