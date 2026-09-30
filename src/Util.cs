// Util.cs -- safe reflection + JSON helpers.
// DESIGN RULE: NEVER touch UnityEngine.Object from a background thread;
// NEVER deep-walk an object graph. Every helper is depth-limited, counted,
// and individually try/caught.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SotfClientProbe
{
    internal static class U
    {
        public static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Num(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return "null";
            return f.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            return d.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string Num(int i) { return i.ToString(CultureInfo.InvariantCulture); }
        public static string Num(long i) { return i.ToString(CultureInfo.InvariantCulture); }
        public static string Bool(bool b) { return b ? "true" : "false"; }

        public static bool IsUnityObject(Type t)
        {
            if (t == null) return false;
            try
            {
                for (Type c = t; c != null; c = c.BaseType)
                {
                    if (c.FullName == "UnityEngine.Object") return true;
                }
            }
            catch { }
            return false;
        }

        public static bool IsSafeValue(Type t)
        {
            if (t == null) return false;
            try
            {
                if (t.IsPrimitive || t.IsEnum) return true;
                if (t == typeof(string) || t == typeof(decimal)) return true;
                if (t == typeof(DateTime) || t == typeof(Guid)) return true;
                if (t == typeof(IntPtr) || t == typeof(UIntPtr)) return true;
            }
            catch { }
            return false;
        }

        /// A SteamID64 is always 17 digits and starts with 7656.
        /// Detect by VALUE SHAPE, never by member name.
        public static bool LooksLikeSteamId64(object v, out ulong id)
        {
            id = 0;
            if (v == null) return false;
            try
            {
                ulong n = 0;
                if (v is ulong u) n = u;
                else if (v is long l) n = l > 0 ? (ulong)l : 0;
                else if (v is string s)
                {
                    s = s.Trim();
                    if (s.Length != 17) return false;
                    ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
                }
                else return false;

                if (n < 76561190000000000UL || n > 76561210000000000UL) return false;
                id = n;
                return true;
            }
            catch { return false; }
        }

        /// Reads Count (IL2CPP list/dict) or Length (IL2CPP array).
        public static int SeqLen(object seq)
        {
            if (seq == null) return -1;
            object v; string err;
            if (TryGet(seq, "Count", out v, out err) && v is int) return (int)v;
            if (TryGet(seq, "Length", out v, out err) && v is int) return (int)v;
            if (seq is System.Collections.ICollection) return ((System.Collections.ICollection)seq).Count;
            return -1;
        }

        /// Returns up to max elements of an IL2CPP list / array, or null.
        public static List<object> Seq(object seq, int max)
        {
            var outp = new List<object>();
            if (seq == null) return null;

            System.Collections.IEnumerable en = seq as System.Collections.IEnumerable;
            if (en != null && !(seq is string))
            {
                try
                {
                    foreach (object o in en)
                    {
                        outp.Add(o);
                        if (outp.Count >= max) break;
                    }
                    if (outp.Count > 0) return outp;
                }
                catch { }
            }

            int n = SeqLen(seq);
            if (n <= 0) return null;

            MethodInfo idx = null;
            try
            {
                Type t = seq.GetType();
                idx = t.GetMethod("get_Item", BF, null, new Type[] { typeof(int) }, null);
                if (idx == null) idx = t.GetMethod("GetValue", BF, null, new Type[] { typeof(int) }, null);
            }
            catch { }
            if (idx == null) return null;

            for (int i = 0; i < n && outp.Count < max; i++)
            {
                try
                {
                    object o = idx.Invoke(seq, new object[] { i });
                    if (o != null) outp.Add(o);
                }
                catch { }
            }
            return outp;
        }

        private static readonly Dictionary<string, Type> TypeCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        public static Type TypeNamed(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            lock (TypeCache)
            {
                Type cached;
                if (TypeCache.TryGetValue(fullName, out cached)) return cached;
            }
            Type found = null;
            try
            {
                foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = a.GetType(fullName, false, false); } catch { }
                    if (t != null) { found = t; break; }
                }
            }
            catch { }
            lock (TypeCache) { TypeCache[fullName] = found; }
            return found;
        }

        public static readonly BindingFlags BF =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.FlattenHierarchy;

        public static bool TryCall(object o, string name, out object val, out string err)
        {
            val = null; err = null;
            if (o == null) { err = "null_target"; return false; }
            try
            {
                Type t = o.GetType();
                MethodInfo m = t.GetMethod(name, BF, null, Type.EmptyTypes, null);
                if (m == null) { err = "no_method"; return false; }
                val = m.Invoke(o, null);
                return true;
            }
            catch (Exception e)
            {
                err = Short(e);
                return false;
            }
        }

        public static bool TryGet(object o, string name, out object val, out string err)
        {
            val = null; err = null;
            if (o == null) { err = "null_target"; return false; }
            try
            {
                Type t = o.GetType();
                PropertyInfo p = t.GetProperty(name, BF);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                {
                    val = p.GetValue(o, null);
                    return true;
                }
                FieldInfo f = t.GetField(name, BF);
                if (f != null)
                {
                    val = f.GetValue(o);
                    return true;
                }
                err = "no_member";
                return false;
            }
            catch (Exception e)
            {
                err = Short(e);
                return false;
            }
        }

        public static bool TryGetStatic(Type t, string name, out object val, out string err)
        {
            val = null; err = null;
            if (t == null) { err = "null_type"; return false; }
            try
            {
                PropertyInfo p = t.GetProperty(name, BF);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                {
                    val = p.GetValue(null, null);
                    return true;
                }
                FieldInfo f = t.GetField(name, BF);
                if (f != null)
                {
                    val = f.GetValue(null);
                    return true;
                }
                err = "no_static_member";
                return false;
            }
            catch (Exception e)
            {
                err = Short(e);
                return false;
            }
        }

        public static bool TryCallStatic(Type t, string name, out object val, out string err)
        {
            val = null; err = null;
            if (t == null) { err = "null_type"; return false; }
            try
            {
                MethodInfo m = t.GetMethod(name, BF, null, Type.EmptyTypes, null);
                if (m == null) { err = "no_static_method"; return false; }
                val = m.Invoke(null, null);
                return true;
            }
            catch (Exception e)
            {
                err = Short(e);
                return false;
            }
        }

        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            try
            {
                Type direct = Type.GetType(fullName, false);
                if (direct != null) return direct;
            }
            catch { }
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = asm.GetType(fullName, false); } catch { }
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        public static bool AsFloat(object v, out float f)
        {
            f = float.NaN;
            if (v == null) return false;
            try
            {
                if (v is float fl) { f = fl; return true; }
                if (v is double d) { f = (float)d; return true; }
                if (v is int i) { f = i; return true; }
                if (v is long lg) { f = lg; return true; }
                if (v is short sh) { f = sh; return true; }
                if (v is byte by) { f = by; return true; }
                if (v is string s)
                {
                    return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
                }
                return false;
            }
            catch { return false; }
        }

        public static string TypeName(object o)
        {
            if (o == null) return "null";
            try
            {
                Type asType = o as Type;
                if (asType != null) return asType.FullName ?? asType.Name;
                return o.GetType().FullName ?? o.GetType().Name;
            }
            catch { return "?"; }
        }

        public static string Short2(object v)
        {
            if (v == null) return "null";
            try
            {
                string s = v as string;
                if (s == null) s = v.ToString();
                if (string.IsNullOrEmpty(s)) return "(empty)";
                if (s.Length > 40) s = s.Substring(0, 40) + "...";
                return s;
            }
            catch { return "?"; }
        }

        public static string Short(Exception e)
        {
            if (e == null) return "?";
            try
            {
                string m = e.Message ?? "";
                if (m.Length > 160) m = m.Substring(0, 160);
                return e.GetType().Name + ":" + m.Replace("\n", " ").Replace("\r", " ");
            }
            catch { return "?"; }
        }

        public static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        public static string NowUtc()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        public static string FileStamp()
        {
            return DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        }
    }
}
