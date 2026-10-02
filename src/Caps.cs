// Caps.cs -- C 端静态容器容量兜底（39.94）
// ===================== 为什么写这个 =====================
// 审计（39.93）发现 C 端有 9 个 static 容器只增不减：
//   _hpSamples(ProbeE1) / _map+_order(Diag) / _okMap+_okNames(PlayerInfoProbe)
//   _pieceName(ArmourProbe) / _dumped(ItemDecode) / _unknownLogged(ItemDecode)
//   _found(WeaponBlock)
//
// C 端与 S 端的差别在于：C 端是玩家进程，玩家一局一局地打，
// 进程可能连着开十几个小时不重启。这些容器虽然单条很小，
// 但 _map / _order 里存的是诊断条目（含字符串），长跑同样会涨。
//
// ===================== 两段式治理（用户 39.94 定调）=====================
//   第一步 保命：加容量上限。（本文件）
//   第二步 治本：按事件精确清理。（后续版本）
//
// ===================== 与 S 端 Caps 的差异 =====================
// S 端用的是 Dictionary 的"删最早一批"；C 端这里还要处理 List / HashSet，
// 两者没有稳定的插入序保证（HashSet 尤其没有），所以：
//   - List    -> 删前段（RemoveRange(0, drop)），List 的插入序是可靠的
//   - HashSet -> 不做"删最老"，超限直接 Clear 重建一半。
//                理由：HashSet 无法廉价地挑出"最老的一批"，
//                而它在本项目里的语义是"已记录过 / 已 dump 过"的去重集合，
//                Clear 的代价是"同一个 id 可能被再记一次"，这比内存无界可接受。
//                且这几个 Set 的基数受物品 id 总数限制，实际上很难触顶。

using System;
using System.Collections.Generic;

namespace SotfClientProbe
{
    internal static class Caps
    {
        /// 诊断条目（_map / _order）。诊断项有限，给 512 极宽松。
        public const int Diag = 512;

        /// 物品名缓存（_pieceName）。物品 id 全集是千级，给 4096 足够。
        public const int Item = 4096;

        /// 玩家名白名单（_okMap / _okNames）。
        public const int Name = 512;

        /// 血量采样串（_hpSamples）。仅调试用，给 1024。
        public const int Sample = 1024;

        /// 已 dump 类型名 / 已记录未知 id。给 2048。
        public const int Seen = 2048;

        /// 武器扫描结果（_found）。扫描结果一旦产出就会写文件，给 1024。
        public const int Found = 1024;

        // ---------------- Dictionary ----------------

        /// <summary>超限裁掉最早的一批（依赖从未删除所保持的插入序）。</summary>
        public static int Trim<K, V>(Dictionary<K, V> d, int max)
        {
            if (d == null || max <= 0) return 0;
            int n = d.Count;
            if (n <= max) return 0;

            int drop = n - max;
            int quarter = n / 4;
            if (quarter > drop) drop = quarter;
            if (drop < 1) drop = 1;
            if (drop > n) drop = n;

            var keys = new List<K>(drop);
            int i = 0;
            foreach (KeyValuePair<K, V> kv in d)
            {
                if (i++ >= drop) break;
                keys.Add(kv.Key);
            }
            for (int j = 0; j < keys.Count; j++) d.Remove(keys[j]);
            return keys.Count;
        }

        // ---------------- List ----------------

        /// <summary>超限删掉最前面的一批。List 的插入序可靠，前段即最老。</summary>
        public static int Trim<T>(List<T> l, int max)
        {
            if (l == null || max <= 0) return 0;
            int n = l.Count;
            if (n <= max) return 0;

            int drop = n - max;
            int quarter = n / 4;
            if (quarter > drop) drop = quarter;
            if (drop < 1) drop = 1;
            if (drop > n) drop = n;

            l.RemoveRange(0, drop);
            return drop;
        }

        // ---------------- HashSet ----------------

        /// <summary>
        /// 【保留但已不推荐】超限直接清空。
        /// 39.94 补充说明：这个做法正是"重复采样"的来源 ——
        ///   清空后所有已记过的 id 全部失忆，下一帧同一批对象被当成新东西
        ///   重新探测、重新 dump、重新写一遍，日志里就出现大段重复。
        /// 新代码请改用下面的 Caps.Set<T>，它按插入序淘汰最老的一批，
        ///   不会整体失忆。本方法仅为尚未迁移的调用点保留。
        /// </summary>
        public static int Trim<T>(HashSet<T> s, int max)
        {
            if (s == null || max <= 0) return 0;
            int n = s.Count;
            if (n <= max) return 0;
            s.Clear();
            return n;
        }

        // ---------------- 有序去重集合 ----------------

        /// <summary>
        /// 按插入序淘汰的去重集合（39.94）。
        ///
        /// 为什么不用裸 HashSet：
        ///   HashSet 没有顺序，超限时唯一的廉价办法是 Clear —— 而 Clear 会让
        ///   "已探测过 / 已记录过"的记忆整体失忆，导致同一批对象被反复处理，
        ///   这就是日志里大段重复采样的根因。
        ///
        /// 本类内部 = HashSet（O(1) 判重）+ Queue（记录插入序）。
        ///   超限时只从队首淘汰最老的一批（25%），保留其余记忆。
        ///
        /// 用法与 HashSet 基本一致：Add 返回"是否是新元素"，Contains 判重。
        ///   不需要外部再调 Caps.Trim，Add 内部自动维护容量。
        /// </summary>
        public sealed class Set<T>
        {
            private readonly HashSet<T> _set;
            private readonly Queue<T> _order;
            private readonly int _max;

            public Set(int max)
            {
                _max = max > 0 ? max : 1;
                _set = new HashSet<T>();
                _order = new Queue<T>();
            }

            /// <summary>加入一个元素。返回 true 表示此前不存在（是新的）。</summary>
            public bool Add(T v)
            {
                if (!_set.Add(v)) return false;
                _order.Enqueue(v);
                if (_set.Count > _max) Drop();
                return true;
            }

            public bool Contains(T v) { return _set.Contains(v); }

            public int Count { get { return _set.Count; } }

            /// <summary>淘汰最老的一批（25%），不是整体清空。</summary>
            private void Drop()
            {
                int drop = _set.Count / 4;
                if (drop < 1) drop = 1;
                for (int i = 0; i < drop && _order.Count > 0; i++)
                {
                    T old = _order.Dequeue();
                    _set.Remove(old);
                }
            }
        }
    }
}
