using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 主窗口底层助手 (Windows Helper)
    /// 职责：封装 Win32 API、窗口样式、圆角、穿透、透明度等底层操作
    /// </summary>
    public class MainFormWinHelper
    {
        private readonly Form _form;

        public MainFormWinHelper(Form form)
        {
            _form = form;
        }

        public void InitializeStyle(bool topMost, bool clickThrough)
        {
            _form.FormBorderStyle = FormBorderStyle.None;
            _form.ShowInTaskbar = false;
            _form.TopMost = topMost;


            
            // 解决 DoubleBuffered 访问权限问题
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(_form, true, null);

            // 原始逻辑还原：
            // 在原始代码中，Opacity 是在构造函数末尾启动异步任务来渐变的
            // 而不是在 InitializeStyle 这种早期阶段直接设为 0
            // 这里的 Opacity = 0 会导致窗体在 OnShown 之前就是透明的，
            // 但如果 SetStyle(ControlStyles.SupportsTransparentBackColor, true) 还没生效或冲突
            // 可能会导致这一瞬间的绘制异常（如黑色闪烁）
            // 
            // 修正：删除这里的 _form.Opacity = 0; 
            // 改回完全依赖 StartFadeIn 方法来控制，并且那个方法是在 OnShown 之后调用的
            
            ApplyRoundedCorners();
            EnableDwmShadow();
            if (clickThrough) SetClickThrough(true);

            // 绑定 Resize 事件以自动重绘圆角
            _form.Resize += (_, __) => ApplyRoundedCorners();
        }

        public bool IsTopMostStyleApplied()
        {
            try
            {
                if (_form.IsDisposed || !_form.IsHandleCreated) return false;

                int ex = GetWindowLong(_form.Handle, GWL_EXSTYLE);
                return (ex & WS_EX_TOPMOST) != 0;
            }
            catch
            {
                return false;
            }
        }

        public void RefreshTopMost(bool enabled, bool forceReinsert = false)
        {
            try
            {
                if (_form.IsDisposed || !_form.IsHandleCreated) return;

                _form.TopMost = enabled;
                uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;

                if (enabled)
                {
                    // 仅在明确恢复/用户操作时重插 Z 序，避免定时器频繁抢其它置顶窗口。
                    if (forceReinsert)
                    {
                        SetWindowPos(_form.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                    }
                    SetWindowPos(_form.Handle, HWND_TOPMOST, 0, 0, 0, 0, flags);
                }
                else
                {
                    SetWindowPos(_form.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                }
            }
            catch { }
        }

        // =================================================================
        // 透明度渐变
        // =================================================================
        public void StartFadeIn(double targetOpacity)
        {
            targetOpacity = Math.Clamp(targetOpacity, 0.1, 1.0);
            _ = Task.Run(async () =>
            {
                try
                {
                    double current = 0;
                    while (current < targetOpacity)
                    {
                        await Task.Delay(16).ConfigureAwait(false);
                        _form.BeginInvoke(new Action(() => 
                        {
                            current += 0.05;
                            if (current > targetOpacity) current = targetOpacity;
                            _form.Opacity = current;
                        }));
                        if (current >= targetOpacity) break;
                    }
                }
                catch { }
            });
        }

        // =================================================================
        // 鼠标穿透
        // =================================================================
        public void SetClickThrough(bool enable)
        {
            try
            {
                int ex = GetWindowLong(_form.Handle, GWL_EXSTYLE);
                if (enable)
                    SetWindowLong(_form.Handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED);
                else
                    SetWindowLong(_form.Handle, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
            }
            catch { }
        }

        // =================================================================
        // 圆角处理 (Hybrid: Win11 DWM / Win10 Region)
        // =================================================================
        public void ApplyRoundedCorners()
        {
            try
            {
                bool isWin11 = Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

                if (isWin11)
                {
                    _form.Region = null;
                    int preference = DWMWCP_ROUND;
                    DwmSetWindowAttribute(_form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                    int borderColor = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(_form.Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
                }
                else
                {
                    var t = ThemeManager.Current;
                    int r = Math.Max(0, t.Layout.CornerRadius);

                    if (r == 0)
                    {
                        _form.Region = null;
                        return;
                    }

                    using var gp = new GraphicsPath();
                    int d = r * 2;
                    gp.AddArc(0, 0, d, d, 180, 90);
                    gp.AddArc(_form.Width - d, 0, d, d, 270, 90);
                    gp.AddArc(_form.Width - d, _form.Height - d, d, d, 0, 90);
                    gp.AddArc(0, _form.Height - d, d, d, 90, 90);
                    gp.CloseFigure();

                    _form.Region?.Dispose();
                    _form.Region = new Region(gp);
                }
            }
            catch { }
        }

        // =================================================================
        // Win11 原生阴影（无边框窗口启用 DWM 阴影/描边）
        // =================================================================
        public void EnableDwmShadow()
        {
            try
            {
                if (!_form.IsHandleCreated) return;
                var margins = new MARGINS { cxLeftWidth = 1, cxRightWidth = 1, cyTopHeight = 1, cyBottomHeight = 1 };
                DwmExtendFrameIntoClientArea(_form.Handle, ref margins);
            }
            catch { }
        }

        // =================================================================
        // 卡片圆角档位（收起态用小圆角）与磨砂材质（亚克力 · 试验）
        // =================================================================
        public const int CornerRound = 2;       // DWMWCP_ROUND（≈8px，常规面板）
        public const int CornerRoundSmall = 3;  // DWMWCP_ROUNDSMALL（≈4px，收起小卡片）

        public void SetCornerPreference(int pref)
        {
            try
            {
                if (!_form.IsHandleCreated) return;
                int p = pref;
                DwmSetWindowAttribute(_form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref p, sizeof(int));
            }
            catch { }
        }

        /// <summary>亚克力磨砂：系统合成器在窗口背后画模糊+色调。需窗口自身留出透明像素（Form.TransparencyKey）才可见。
        /// gradientColor 格式 0xAABBGGRR。非官方 API，失败静默（退化 = 透明键区域直接见桌面）。</summary>
        public void SetAcrylic(bool enable, uint gradientColor)
        {
            try
            {
                if (!_form.IsHandleCreated) return;
                var accent = new AccentPolicy
                {
                    AccentState = enable ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_DISABLED,
                    AccentFlags = 2,
                    GradientColor = enable ? unchecked((int)gradientColor) : 0,
                    AnimationId = 0
                };
                int size = Marshal.SizeOf(accent);
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, ptr, false);
                    var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
                    SetWindowCompositionAttribute(_form.Handle, ref data);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
        [StructLayout(LayoutKind.Sequential)] private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_DISABLED = 0;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        // =================================================================
        // Win32 API
        // =================================================================
        public static int RegisterTaskbarCreatedMessage()
        {
            int msgId = RegisterWindowMessage("TaskbarCreated");
            if (msgId != 0)
            {
                ChangeWindowMessageFilter((uint)msgId, MSGFLT_ADD);
            }
            return msgId;
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
        [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern int RegisterWindowMessage(string lpString);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

        public const uint MSGFLT_ADD = 1;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        public static void ActivateWindow(IntPtr handle) => SetForegroundWindow(handle);
    }
}
