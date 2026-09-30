// SessLog.cs -- A1: unified session log naming for the client.
//
// Format: C_<login time>_<stop time>.txt
//   e.g.  C_20260910_114455_20260910_115230.txt
//   The server writes S_ with the same rule, so a pair taken from the same
//   match sorts together and is obvious at a glance.

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SotfClientProbe
{
    internal static class SessLog
    {
        public static string Write(string dir, string body)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return "";
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture);
                string name = "C_" + Sess.LoginStamp() + "_" + stamp + ".txt";
                string path = Path.Combine(dir, name);

                var sb = new StringBuilder();
                sb.Append("SotF Client Probe  " + ProbePlugin.VERSION + "\n");
                sb.Append("session login : " + Sess.LoginText() + "\n");
                sb.Append("session stop  : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture) + "\n");
                sb.Append(new string('=', 60) + "\n");
                sb.Append(body ?? "");
                if ((body ?? "").Length > 0 &&
                    !body.EndsWith("\n", StringComparison.Ordinal)) sb.Append('\n');

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return name;
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>
    /// A1: remembers when the player joined this server, so the C_ log name
    /// can carry the login time even though the file is written at the end.
    /// </summary>
    internal static class Sess
    {
        private static DateTime _login;
        private static bool _has;

        public static void NoteJoin()
        {
            if (_has) return;
            _login = DateTime.Now;
            _has = true;
        }

        public static void Reset()
        {
            _has = false;
        }

        public static string LoginStamp()
        {
            return _has ? _login.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
                        : "nologin";
        }

        public static string LoginText()
        {
            return _has ? _login.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : "(not captured)";
        }
    }
}
