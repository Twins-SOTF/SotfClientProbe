// Link.cs -- A1: plain HTTP channel to the dedicated server (port 11008).
//
// Why DS listens and the client dials:
//   Players sit behind NAT / home routers, so the server can never open a
//   connection back to them. The direction is forced.
//
// Why the server address needs no config:
//   BoltProbe already resolves it from BoltNetwork.Server -> RemoteEndPoint
//   (measured: "<SERVER_IP>:11005"). We only swap the port.
//
// Handshake policy (as specified):
//   - up to 10 probes, 500 ms apart
//   - a failed probe waits 1 s before the next attempt
//   - all 10 fail  -> OFFLINE: banner "SERVER OFFLINE", client keeps
//                     collecting locally and never blocks the game
//
// Clock: Cristian's algorithm. The server answers with its own UTC ms; we
//   take offset = srv - (t0+t1)/2 and keep the sample with the smallest RTT.
//   Offset is stored, never applied to the local clock -- the two machines
//   keep their own time and every record carries what is needed to align.
//
// Threading rule: every network call runs on a background thread. Nothing
//   here touches Unity objects, and a hung server can never stall a frame.

using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SotfClientProbe
{
    internal static class Link
    {
        // ---- config ----
        public const int HttpPort = 11008;
        public const int ProbeCount = 10;
        public const int ProbeGapMs = 500;
        public const int ProbeRetryWaitMs = 1000;
        public const int TimeoutMs = 1500;
        public const int ReportEveryMs = 1000;

        // ---- state ----
        private static string _host = "";
        private static int _port = HttpPort;
        private static int _state;          // 0 idle, 1 handshaking, 2 done
        private static bool _online;
        private static double _offsetMs;
        private static double _bestRtt = double.MaxValue;
        private static int _probes;
        private static int _fails;
        private static string _lastErr = "";
        private static long _lastReportMs;
        private static readonly object _lk = new object();
        private static readonly object _commLk = new object();
        private static string _commPath = "";

        public static bool Online { get { lock (_lk) { return _online; } } }
        public static bool Tried { get { lock (_lk) { return _state == 2; } } }
        public static bool Busy { get { lock (_lk) { return _state == 1; } } }
        /// <summary>server_utc - client_utc, in ms. Only meaningful when Online.</summary>
        public static double OffsetMs { get { lock (_lk) { return _offsetMs; } } }
        public static double BestRttMs { get { lock (_lk) { return _bestRtt == double.MaxValue ? -1 : _bestRtt; } } }
        public static int Probes { get { lock (_lk) { return _probes; } } }
        public static int Fails { get { lock (_lk) { return _fails; } } }
        public static string Host { get { lock (_lk) { return _host; } } }
        public static string LastErr { get { lock (_lk) { return _lastErr; } } }

        /// <summary>A1：换服 / 重新开始时清空状态，允许再次握手。</summary>
        public static void Reset()
        {
            lock (_lk)
            {
                _state = 0;
                _online = false;
                _offsetMs = 0;
                _bestRtt = double.MaxValue;
                _probes = 0;
                _fails = 0;
                _lastErr = "";
                _host = "";
                _lastReportMs = 0;
            }
        }

        public static void SetCommPath(string dir)
        {
            try
            {
                _commPath = Path.Combine(dir, "link_comm.txt");
            }
            catch { }
        }

        private static void Comm(string line)
        {
            try
            {
                if (string.IsNullOrEmpty(_commPath)) return;
                string s = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                           + " " + line + "\n";
                lock (_commLk) { File.AppendAllText(_commPath, s, new UTF8Encoding(false)); }
            }
            catch { }
        }

        // ==================== 握手 ====================

        /// <summary>
        /// Called when a server endpoint becomes known. Spawns the handshake
        /// once; later calls are ignored until it finishes.
        /// </summary>
        public static void Begin(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return;

            // A1.07 修复：BoltProbe 交上来的不是干净的 "1.2.3.4:11005"，而是
            //   RemoteEndPoint.ToString() 的原始形态
            //   "[EndPoint <SERVER_IP>:11005 | 7483222601876122365]"
            // 上一版用 LastIndexOf(':') 直接切，切出来的是
            //   "[EndPoint <SERVER_IP>"
            // 带前缀的非法主机名 —— TcpClient 解析失败并在约 24ms 内返回，
            // 表现就是"十次探测全部 no response / 服务器不在线"。
            // 实测证据：link_comm.txt 里打印的正是
            //   handshake start -> [EndPoint <SERVER_IP>:11008
            // 现在改用正则抓 IPv4，外面套多少层都能拿到干净 IP。
            string host = ExtractHost(endpoint);
            if (string.IsNullOrEmpty(host))
            {
                Comm("handshake skipped -- no usable IPv4 in endpoint: " + endpoint);
                return;
            }
            int port = HttpPort;   // 游戏端口(11005)换 HTTP 端口，IP 不变

            lock (_lk)
            {
                if (_state != 0) return;
                _host = host; _port = port;
                _state = 1;
            }

            Comm("handshake start -> " + host + ":" + port);
            var th = new Thread(HandshakeLoop);
            th.IsBackground = true;
            th.Name = "SotfLinkHandshake";
            th.Start();
        }

        private static void HandshakeLoop()
        {
            try
            {
                for (int i = 0; i < ProbeCount; i++)
                {
                    lock (_lk) { _probes++; }
                    long t0 = NowMs();
                    string body = "{\"nonce\":" + i.ToString(CultureInfo.InvariantCulture)
                                  + ",\"cli_utc_ms\":" + t0 + "}";
                    string resp = Request("POST", "/clock", body);
                    long t1 = NowMs();

                    if (resp.Length > 0)
                    {
                        long srv = JsonLong(resp, "srv_utc_ms");
                        if (srv > 0)
                        {
                            double rtt = t1 - t0;
                            double off = srv - (t0 + t1) / 2.0;
                            lock (_lk)
                            {
                                if (rtt < _bestRtt)
                                {
                                    _bestRtt = rtt;
                                    _offsetMs = off;
                                }
                                _online = true;
                            }
                            Comm("probe " + (i + 1) + " OK  rtt=" + rtt.ToString("0.0", CultureInfo.InvariantCulture)
                                 + "ms  offset=" + off.ToString("0.0", CultureInfo.InvariantCulture) + "ms");
                            // 再探两次取最小 RTT，然后收工 —— 不把 10 次全耗在握手上。
                            if (i >= 2) break;
                            Thread.Sleep(ProbeGapMs);
                            continue;
                        }
                    }

                    lock (_lk) { _fails++; }
                    Comm("probe " + (i + 1) + " FAIL " + (resp.Length == 0 ? "(no response)" : "(bad body)"));
                    Thread.Sleep(ProbeRetryWaitMs);
                }

                lock (_lk)
                {
                    _state = 2;
                    if (!_online)
                    {
                        _lastErr = "server did not answer after " + _probes + " probe(s)";
                        Comm("HANDSHAKE FAILED -> OFFLINE MODE");
                    }
                    else
                    {
                        Comm("handshake done  online=1  best_rtt="
                             + BestRttMs.ToString("0.0", CultureInfo.InvariantCulture)
                             + "ms  offset=" + OffsetMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms");
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_lk)
                {
                    _state = 2; _online = false;
                    _lastErr = ex.Message;
                }
                Comm("handshake threw: " + ex.Message);
            }
        }

        // ==================== 上报 ====================

        /// <summary>
        /// 周期上报。payload 是若干行 JSON（不含外层数组）。
        /// 内部节流到 ReportEveryMs，且在后台线程发送，绝不卡帧。
        /// </summary>
        public static void Report(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            lock (_lk)
            {
                if (!_online) return;
            }
            long now = Environment.TickCount64;
            if (now - _lastReportMs < ReportEveryMs) return;
            _lastReportMs = now;

            string host; int port;
            lock (_lk) { host = _host; port = _port; }
            if (string.IsNullOrEmpty(host)) return;

            var th = new Thread(() =>
            {
                try
                {
                    string r = Request("POST", "/report", payload);
                    if (r.Length == 0) Comm("report FAIL (no response)");
                }
                catch (Exception ex) { Comm("report threw: " + ex.Message); }
            });
            th.IsBackground = true;
            th.Name = "SotfLinkReport";
            th.Start();
        }

        // ==================== 极简 HTTP ====================

        /// <summary>
        /// 对外暴露的原始请求，供 CrossCheck 拉 /events。
        /// 与内部 Request 同一实现，只是公开 —— 避免为一条 GET 复制一份超时
        /// 与异常处理逻辑。
        /// </summary>
        public static string Raw(string method, string path, string body)
        {
            return Request(method, path, body);
        }

        private static string Request(string method, string path, string body)
        {
            string host; int port;
            lock (_lk) { host = _host; port = _port; }
            if (string.IsNullOrEmpty(host)) return "";

            TcpClient cli = null;
            try
            {
                cli = new TcpClient();
                var ar = cli.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeoutMs)) { try { cli.Close(); } catch { } return ""; }
                cli.EndConnect(ar);
                cli.ReceiveTimeout = TimeoutMs;
                cli.SendTimeout = TimeoutMs;

                using (NetworkStream ns = cli.GetStream())
                {
                    byte[] bb = Encoding.UTF8.GetBytes(body ?? "");
                    var sb = new StringBuilder();
                    sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
                    sb.Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n");
                    sb.Append("Content-Type: application/json\r\n");
                    sb.Append("Content-Length: ").Append(bb.Length).Append("\r\n");
                    sb.Append("Connection: close\r\n\r\n");
                    byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
                    ns.Write(head, 0, head.Length);
                    if (bb.Length > 0) ns.Write(bb, 0, bb.Length);
                    ns.Flush();

                    var ms = new MemoryStream();
                    byte[] buf = new byte[4096];
                    int n;
                    while ((n = ns.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    string resp = Encoding.UTF8.GetString(ms.ToArray());
                    int i = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    return i >= 0 ? resp.Substring(i + 4) : resp;
                }
            }
            catch (Exception ex)
            {
                lock (_lk) { _lastErr = ex.Message; }
                return "";
            }
            finally
            {
                try { if (cli != null) cli.Close(); } catch { }
            }
        }

        /// <summary>
        /// 从任意形态的端点串里抓出 IPv4。宁可返回空也不要返回脏串 ——
        /// 空会让握手跳过并明确记日志，脏串会让 TcpClient 静默失败。
        /// </summary>
        private static readonly Regex RxIpv4 =
            new Regex(@"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})", RegexOptions.Compiled);

        private static string ExtractHost(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            Match m = RxIpv4.Match(s);
            if (!m.Success) return "";
            string ip = m.Groups[1].Value;
            string[] parts = ip.Split('.');
            if (parts.Length != 4) return "";
            for (int i = 0; i < 4; i++)
            {
                int v;
                if (!int.TryParse(parts[i], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out v)) return "";
                if (v < 0 || v > 255) return "";
            }
            return ip;
        }

        // ==================== 小工具 ====================

        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long NowMs()
        {
            return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
        }

        /// <summary>从 {"srv_utc_ms":123} 里取整数。不引第三方 JSON 库。</summary>
        private static long JsonLong(string json, string key)
        {
            try
            {
                string pat = "\"" + key + "\"";
                int i = json.IndexOf(pat, StringComparison.Ordinal);
                if (i < 0) return 0;
                i += pat.Length;
                while (i < json.Length && (json[i] == ' ' || json[i] == ':')) i++;
                int j = i;
                while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-' || json[j] == '+')) j++;
                if (j == i) return 0;
                long v;
                long.TryParse(json.Substring(i, j - i), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out v);
                return v;
            }
            catch { return 0; }
        }

        /// <summary>HUD / 日志用的一行状态。</summary>
        public static string StatusText()
        {
            lock (_lk)
            {
                if (_state == 0 || _state == 1)
                    return "SYNCING CLOCK...";
                if (!_online)
                    return "SERVER OFFLINE";
                return "LINK OK  off=" + _offsetMs.ToString("0.0", CultureInfo.InvariantCulture)
                       + "ms  rtt=" + BestRttMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms";
            }
        }

        public static string DiagText()
        {
            lock (_lk)
            {
                return "link host=" + (_host.Length == 0 ? "-" : _host + ":" + _port)
                     + " online=" + (_online ? 1 : 0)
                     + " tried=" + (_state == 2 ? 1 : 0)
                     + " probes=" + _probes + " fails=" + _fails
                     + " offset_ms=" + _offsetMs.ToString("0.0", CultureInfo.InvariantCulture)
                     + " best_rtt=" + BestRttMs.ToString("0.0", CultureInfo.InvariantCulture)
                     + (_lastErr.Length > 0 ? " err=" + _lastErr : "");
            }
        }
    }
}
