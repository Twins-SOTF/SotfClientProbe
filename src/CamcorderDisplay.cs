// CamcorderDisplay.cs -- 手持录像机数据显示
// F5 开关模式，Q/R 切换页面

using System;
using UnityEngine;

namespace SotfClientProbe
{
    internal static class CamcorderDisplay
    {
        private static bool _active = false;
        private static int _page = 0;
        private static readonly string[] PageNames = {
            "个人数据",
            "全场排行榜",
            "战斗记录",
            "服务器信息"
        };

        private static KeyCode _lastQ = KeyCode.None;
        private static KeyCode _lastR = KeyCode.None;

        public static void Toggle() { _active = !_active; }
        public static bool Active { get { return _active; } }

        public static void Update()
        {
            if (!_active) return;

            // Q 键上一页
            if (Input.GetKeyDown(KeyCode.Q))
            {
                _page--;
                if (_page < 0) _page = PageNames.Length - 1;
                Save("Switched to page: " + PageNames[_page]);
            }

            // R 键下一页
            if (Input.GetKeyDown(KeyCode.R))
            {
                _page++;
                if (_page >= PageNames.Length) _page = 0;
                Save("Switched to page: " + PageNames[_page]);
            }
        }

        public static void OnGUI()
        {
            if (!_active) return;

            // 全屏录像机屏幕：整个屏幕都是取景器
            float w = Screen.width;
            float h = Screen.height;
            float x = 0;
            float y = 0;

            // 半透明背景（深色，模拟摄像机屏幕）
            GUI.color = new Color(0.05f, 0.05f, 0.08f, 0.95f);
            GUI.Box(new Rect(x, y, w, h), "");
            GUI.color = Color.white;

            // 边框：绿色，模拟摄像机取景器框
            GUI.color = Color.green;
            GUI.Box(new Rect(x + 10, y + 10, w - 20, 2), "");
            GUI.Box(new Rect(x + 10, y + h - 12, w - 20, 2), "");
            GUI.Box(new Rect(x + 10, y + 10, 2, h - 20), "");
            GUI.Box(new Rect(x + w - 12, y + 10, 2, h - 20), "");
            GUI.color = Color.white;

            // 顶部栏：页面名 + 页码
            var headerStyle = new GUIStyle(GUI.skin.label);
            headerStyle.fontSize = 24;
            headerStyle.normal.textColor = Color.green;
            headerStyle.alignment = TextAnchor.UpperCenter;
            GUI.Label(new Rect(x + 20, y + 20, w - 40, 40), PageNames[_page] + "  (" + (_page + 1) + "/" + PageNames.Length + ")", headerStyle);

            // 底部提示
            var hintStyle = new GUIStyle(GUI.skin.label);
            hintStyle.fontSize = 14;
            hintStyle.normal.textColor = Color.gray;
            hintStyle.alignment = TextAnchor.LowerCenter;
            GUI.Label(new Rect(x + 20, y + h - 40, w - 40, 25), "Q/R 切换页面  F5 关闭", hintStyle);

            // 页面内容
            var contentStyle = new GUIStyle(GUI.skin.label);
            contentStyle.fontSize = 20;
            contentStyle.normal.textColor = Color.white;
            contentStyle.padding = new RectOffset(40, 40, 60, 60);

            string content = GetPageContent();
            GUI.Label(new Rect(x + 40, y + 70, w - 80, h - 140), content, contentStyle);
        }

        private static string GetPageContent()
        {
            switch (_page)
            {
                case 0: // 个人数据
                    return "=== 个人数据 ===\n\n" +
                           "生命值: 100/100\n" +
                           " stamina: 80/100\n" +
                           "击杀数: 12\n" +
                           "死亡数: 2\n" +
                           "弹反次数: 5\n" +
                           "游戏时长: 01:23:45";
                case 1: // 排行榜
                    return "=== 全场排行榜 ===\n\n" +
                           "1. 玩家A - 32杀\n" +
                           "2. 玩家B - 28杀\n" +
                           "3. 玩家C - 19杀\n" +
                           "4. 你 - 12杀\n" +
                           "5. 玩家D - 8杀";
                case 2: // 战斗记录
                    return "=== 战斗记录 ===\n\n" +
                           "[刚刚] 击杀野人 x3\n" +
                           "[2分钟前] 被野人攻击 -20HP\n" +
                           "[5分钟前] 击杀BOSS x1\n" +
                           "[10分钟前] 弹反成功 x2";
                case 3: // 服务器信息
                    return "=== 服务器信息 ===\n\n" +
                           "服务器: 079\n" +
                           "在线玩家: 5/8\n" +
                           "延迟: 23ms\n" +
                           "服务器时间: 12:34:56";
                default:
                    return "";
            }
        }

        private static void Save(string msg)
        {
            try
            {
                string path = System.IO.Path.Combine(Out.Dir, "camcorder_probe.txt");
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\n");
            }
            catch { }
        }
    }
}
