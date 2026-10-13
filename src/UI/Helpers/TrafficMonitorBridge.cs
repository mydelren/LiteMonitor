using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 与 TrafficMonitor 引擎的桥（2026-10-13 集成方案）：
    /// 展开面板上的"单击"→ 向 TrafficMonitor 的（隐藏）主窗投递一个左键 →
    /// ProcessNetMonitor 插件收到 MT_LCLICKED 后开/关"每程序详情"窗。
    /// · TM 主窗类名带随机后缀，只能按"进程 + 标题 TrafficMonitor"查找
    /// · 点击坐标 (25,15) 对应引擎皮肤 PNMEngine 中 SpdUp01 项的矩形内（x 0..118, y 5..25）
    /// · 双方均为提权进程（LiteMonitor_Autostart / TrafficMonitor_Autostart），无 UIPI 限制
    /// </summary>
    internal static class TrafficMonitorBridge
    {
        private const string TmProcessName = "TrafficMonitor";
        private const string TmWindowTitle = "TrafficMonitor";
        private const int ClickX = 25;
        private const int ClickY = 15;

        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;

        private static IntPtr _cachedHwnd = IntPtr.Zero;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>开/关 TrafficMonitor 的"每程序详情"窗（找不到 TM 时静默返回）</summary>
        public static void ToggleDetail()
        {
            var hwnd = FindTmMainWindow();
            if (hwnd == IntPtr.Zero) return;
            var lp = (IntPtr)((ClickY << 16) | (ClickX & 0xFFFF));
            PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)1, lp);
            PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lp);
        }

        private static IntPtr FindTmMainWindow()
        {
            if (_cachedHwnd != IntPtr.Zero && IsWindow(_cachedHwnd)) return _cachedHwnd;
            _cachedHwnd = IntPtr.Zero;

            uint pid;
            try
            {
                var procs = Process.GetProcessesByName(TmProcessName);
                if (procs.Length == 0) return IntPtr.Zero;
                pid = (uint)procs[0].Id;
            }
            catch { return IntPtr.Zero; }

            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint wpid);
                if (wpid == pid)
                {
                    var sb = new StringBuilder(64);
                    GetWindowText(h, sb, sb.Capacity);
                    if (sb.ToString() == TmWindowTitle) { found = h; return false; }
                }
                return true;
            }, IntPtr.Zero);

            _cachedHwnd = found;
            return found;
        }
    }
}
