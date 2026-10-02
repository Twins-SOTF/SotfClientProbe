using System;

namespace SotfClientProbe
{
    /// <summary>
    /// v2.40 入服对时窗口（客户端侧）。
    ///
    /// 为什么需要：服务器端与客户端各写各的时间戳，两台机器没对过表。
    ///   实测偏移每场都不同（14:30 场 +10.503s、17:42 场 +11.224s、20:00 场约 +11.2s），
    ///   所以固定常数不能当真，必须每场重测。
    ///
    /// 做不到什么：客户端插件与专用服插件之间没有直接通信通道，
    ///   不能像 NTP 那样握手。两端只能在同一窗口内各自密集打带 UTC 的时间信标，
    ///   事后用这些信标精确配对求偏移。
    ///
    /// 开销：不开线程。窗口状态只由环境时钟推算，HUD 每帧读一次整数，可忽略。
    ///   客户端资源充足，这点开销无需顾虑。
    /// </summary>
    internal static class ClockSync
    {
        public const int WindowSec = 30;

        private static long _openedAt = long.MinValue;

        public static bool IsOpen
        {
            get
            {
                long t = _openedAt;
                if (t == long.MinValue) return false;
                return (Environment.TickCount64 - t) < (long)WindowSec * 1000L;
            }
        }

        public static int ElapsedSec
        {
            get
            {
                long t = _openedAt;
                if (t == long.MinValue) return -1;
                long e = (Environment.TickCount64 - t) / 1000L;
                return e < 0 ? 0 : (e > WindowSec ? WindowSec : (int)e);
            }
        }

        public static int RemainSec
        {
            get
            {
                if (!IsOpen) return -1;
                return WindowSec - ElapsedSec;
            }
        }

        /// <summary>只开一次；重复调用不会重置窗口。</summary>
        public static void Open()
        {
            if (_openedAt == long.MinValue) _openedAt = Environment.TickCount64;
        }

        /// <summary>写进 sample 事件的字段；窗口未开启时返回空串。</summary>
        public static string JsonField()
        {
            if (!IsOpen) return "";
            return ",\"sync_window\":1,\"sync_remain\":" + RemainSec.ToString();
        }
    }
}
