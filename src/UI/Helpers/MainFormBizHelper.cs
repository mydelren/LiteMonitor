using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 主窗口业务助手 (Business Helper)
    /// 职责：自动隐藏、托盘交互、快捷动作、启动流程、布局切换
    /// </summary>
    public class MainFormBizHelper : IDisposable
    {
        private readonly Form _form;
        private readonly Settings _cfg;
        private readonly UIController _ui;
        private readonly MainFormWinHelper _winHelper;
        private readonly NotifyIcon _tray;

        // 自动隐藏相关
        private System.Windows.Forms.Timer? _autoHideTimer;
        private System.Windows.Forms.Timer? _topMostTimer;
        private bool _isHidden = false;
        private const int HideWidthPlain = 4;   // 未启用贴边信息条时的可见宽度
        private int HideWidth => _cfg.EdgeBarEnabled ? Math.Clamp(_cfg.EdgeBarWidth, 10, 48) : HideWidthPlain;
        private readonly int _hideThreshold = 10;
        public enum DockEdge { None, Left, Right, Top, Bottom }
        private DockEdge _dock = DockEdge.None;
        private DateTime _keepVisibleUntil = DateTime.MinValue;
        private IntPtr _edgeRegionHandle = IntPtr.Zero;   // 已应用紧凑区域时的窗口句柄（用于检测句柄重建）

        public bool IsHidden => _isHidden;
        public DockEdge CurrentDock => _dock;
        public bool IsDragging { get; set; } = false;

        public MainFormBizHelper(Form form, Settings cfg, UIController ui, MainFormWinHelper winHelper)
        {
            _form = form;
            _cfg = cfg;
            _ui = ui;
            _winHelper = winHelper;
            _tray = new NotifyIcon();
        }

        public void Initialize()
        {
            InitTray();
            if (_cfg.AutoHide) StartTimer();
            StartTopMostTimer();
        }

        // =================================================================
        // 自动隐藏逻辑
        // =================================================================
        public void StartTimer()
        {
            _autoHideTimer ??= new System.Windows.Forms.Timer { Interval = 250 };
            _autoHideTimer.Tick -= AutoHideTick;
            _autoHideTimer.Tick += AutoHideTick;
            _autoHideTimer.Start();
        }

        public void StopTimer() => _autoHideTimer?.Stop();

        public void KeepVisible(double seconds) => _keepVisibleUntil = DateTime.Now.AddSeconds(seconds);

        public void ForceShow()
        {
            if (_isHidden)
            {
                _isHidden = false;
                _dock = DockEdge.None;
                ApplyEdgeRegion(false);
                ClampToScreen(force: true); // 强制拉回
            }
            KeepVisible(3.0);
        }

        /// <summary>用户拖动结束：若处于收起态则复位为正常显示（防止卡片状态被拖到屏幕中间）</summary>
        public void OnUserDragEnd()
        {
            if (_isHidden)
            {
                _isHidden = false;
                _dock = DockEdge.None;
                ApplyEdgeRegion(false);
            }
            KeepVisible(2.0);
        }

        /// <summary>收起/展开时切换窗体可见区域：收起=紧凑信息条卡片（Region 裁剪，其余部分不可见）</summary>
        private void ApplyEdgeRegion(bool hidden)
        {
            if (hidden && _cfg.EdgeBarEnabled && _dock != DockEdge.None)
            {
                var piece = EdgeBarRenderer.GetPieceRect(_dock, _form.Width, _form.Height, HideWidth);
                if (!piece.IsEmpty)
                {
                    using var path = EdgeBarRenderer.MakeRoundRect(piece, EdgeBarRenderer.PieceRadius);
                    var old = _form.Region;
                    _form.Region = new Region(path);
                    old?.Dispose();
                    _edgeRegionHandle = _form.Handle;
                    _form.Invalidate();
                    // 残影修复：分层窗口收缩后，强制重绘"让出"的屏幕区域（否则上一帧会留在桌面上形成幽灵白条）
                    var scr = _form.RectangleToScreen(_form.ClientRectangle);
                    var rc = new RedrawRect { L = scr.Left - 2, T = scr.Top - 2, R = scr.Right + 2, B = scr.Bottom + 2 };
                    RedrawWindow(IntPtr.Zero, ref rc, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                    return;
                }
            }
            // 恢复常规圆角区域
            (_form as MainForm)?.ApplyRoundedCorners();
        }

        // ---- 残影修复用 P/Invoke ----
        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, ref RedrawRect lprcUpdate, IntPtr hrgnUpdate, uint flags);
        [StructLayout(LayoutKind.Sequential)]
        private struct RedrawRect { public int L, T, R, B; }
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;

        private void AutoHideTick(object? sender, EventArgs e) => CheckAutoHide();

        private void StartTopMostTimer()
        {
            _topMostTimer ??= new System.Windows.Forms.Timer { Interval = 10000 };
            _topMostTimer.Tick -= TopMostTick;
            _topMostTimer.Tick += TopMostTick;
            _topMostTimer.Start();
        }

        private void TopMostTick(object? sender, EventArgs e)
        {
            if (!_form.Visible) return;

            if (_cfg.TopMost)
            {
                if (!_winHelper.IsTopMostStyleApplied())
                {
                    _winHelper.RefreshTopMost(true, forceReinsert: true);
                }
            }
            else if (_winHelper.IsTopMostStyleApplied())
            {
                _winHelper.RefreshTopMost(false);
            }
        }

        private void CheckAutoHide()
        {
            if (!_cfg.AutoHide) return;
            if (!_form.Visible) return;
            if (IsDragging || _form.ContextMenuStrip?.Visible == true) return;
            if (DateTime.Now < _keepVisibleUntil) return;

            var center = new Point(_form.Left + _form.Width / 2, _form.Top + _form.Height / 2);
            var screen = Screen.FromPoint(center);
            var area = screen.WorkingArea;
            var cursor = Cursor.Position;

            bool nearLeft = _form.Left <= area.Left + _hideThreshold;
            bool nearRight = area.Right - _form.Right <= _hideThreshold;
            bool nearTop = _form.Top <= area.Top + _hideThreshold;

            bool shouldHide = nearLeft || nearRight || nearTop;

            // 靠边 -> 隐藏
            if (!_isHidden && shouldHide && !_form.Bounds.Contains(cursor))
            {
                if (nearRight) { _form.Left = area.Right - HideWidth; _dock = DockEdge.Right; }
                else if (nearLeft) { _form.Left = area.Left - (_form.Width - HideWidth); _dock = DockEdge.Left; }
                else if (nearTop) { _form.Top = area.Top - (_form.Height - HideWidth); _dock = DockEdge.Top; }
                _isHidden = true;
                ApplyEdgeRegion(true);
                return;
            }

            // 鼠标靠近 -> 弹出
            if (_isHidden)
            {
                // 保险：Win11 下 ApplyRoundedCorners 会清空 Region（改用 DWM 圆角），句柄重建也会丢自定义区域
                // 收起态每 tick 巡检，发现被清就立刻补回紧凑卡片区域
                if (_cfg.EdgeBarEnabled && (_form.Region == null || _form.Handle != _edgeRegionHandle))
                {
                    ApplyEdgeRegion(true);
                }

                const int hoverBand = 30;
                // 可见区域（启用信息条时为紧凑卡片；未启用时为整条缝隙）
                var piece = _cfg.EdgeBarEnabled
                    ? EdgeBarRenderer.GetPieceRect(_dock, _form.Width, _form.Height, HideWidth)
                    : Rectangle.Empty;
                int segTop = piece.IsEmpty ? _form.Top : _form.Top + piece.Y;
                int segBottom = piece.IsEmpty ? _form.Top + _form.Height : _form.Top + piece.Bottom;
                int segLeft = piece.IsEmpty ? _form.Left : _form.Left + piece.X;
                int segRight = piece.IsEmpty ? _form.Left + _form.Width : _form.Left + piece.Right;

                bool isMouseOnHiddenPanel = false;
                if (_dock == DockEdge.Right) isMouseOnHiddenPanel = cursor.X >= area.Right - HideWidth && cursor.Y >= segTop && cursor.Y <= segBottom;
                else if (_dock == DockEdge.Left) isMouseOnHiddenPanel = cursor.X <= area.Left + HideWidth && cursor.Y >= segTop && cursor.Y <= segBottom;
                else if (_dock == DockEdge.Top) isMouseOnHiddenPanel = cursor.Y <= area.Top + HideWidth && cursor.X >= segLeft && cursor.X <= segRight;

                if (isMouseOnHiddenPanel)
                {
                    bool shown = false;
                    if (_dock == DockEdge.Right && cursor.X >= area.Right - hoverBand) { _form.Left = area.Right - _form.Width; shown = true; }
                    else if (_dock == DockEdge.Left && cursor.X <= area.Left + hoverBand) { _form.Left = area.Left; shown = true; }
                    else if (_dock == DockEdge.Top && cursor.Y <= area.Top + hoverBand) { _form.Top = area.Top; shown = true; }
                    if (shown)
                    {
                        _isHidden = false;
                        _dock = DockEdge.None;
                        ApplyEdgeRegion(false);
                    }
                }
            }
        }

        // =================================================================
        // 托盘管理
        // =================================================================
        private void InitTray()
        {
            try { _tray.Icon = Properties.Resources.AppIcon ?? _form.Icon; } catch { _tray.Icon = _form.Icon; }
            _tray.Text = "LiteMonitor";
            _tray.Visible = !_cfg.HideTrayIcon;

            RebuildMenus();

            _tray.MouseUp += (_, e) => 
            {
                if (e.Button == MouseButtons.Right)
                {
                    MainFormWinHelper.ActivateWindow(_form.Handle);
                    _form.ContextMenuStrip?.Show(Cursor.Position);
                }
            };
            
            _tray.MouseDoubleClick += (_, e) => 
            {
                if (e.Button == MouseButtons.Left) 
                {
                    if (_form.Visible) ((MainForm)_form).HideMainWindow();
                    else ((MainForm)_form).ShowMainWindow();
                }
            };
        }

        public void RebuildMenus()
        {
            if (_form.ContextMenuStrip != null)
            {
                _form.ContextMenuStrip.Dispose();
                _form.ContextMenuStrip = null;
            }
            _form.ContextMenuStrip = MenuManager.Build((MainForm)_form, _cfg, _ui);
            UIUtils.ClearBrushCache();
        }

        public void ShowNotification(string title, string text, ToolTipIcon icon)
        {
            if (_tray.Visible) _tray.ShowBalloonTip(5000, title, text, icon);
        }

        public void SetTrayVisible(bool visible) => _tray.Visible = visible;

        // =================================================================
        // 布局切换与位置管理
        // =================================================================
        public void ToggleLayoutMode()
        {
            _form.SuspendLayout();
            try
            {
                // 记录旧模式
                bool oldMode = _cfg.HorizontalMode;
                
                _cfg.HorizontalMode = !oldMode;
                _cfg.Save();
                
                // ★ 统一使用 AppActions，传入旧模式以触发自动居中
                Core.Actions.AppActions.ApplyThemeAndLayout(_cfg, _ui, (MainForm)_form, oldMode);
            }
            finally
            {
                _form.ResumeLayout(true);
            }
        }

        public void SavePos()
        {
            ClampToScreen(force: false);
            var center = new Point(_form.Left + _form.Width / 2, _form.Top + _form.Height / 2);
            var scr = Screen.FromPoint(center);

            _cfg.ScreenDevice = scr.DeviceName;
            _cfg.Position = new Point(_form.Left, _form.Top);
            _cfg.Save();
        }

        public void RestorePos()
        {
            Screen? savedScreen = null;
            if (!string.IsNullOrEmpty(_cfg.ScreenDevice))
            {
                savedScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _cfg.ScreenDevice);
            }

            if (savedScreen != null)
            {
                var area = savedScreen.WorkingArea;
                int x = _cfg.Position.X;
                int y = _cfg.Position.Y;
                SetSafeLocation(area, x, y);
                
                // [Fix #288] 恢复位置后，重置隐藏状态，并让 Timer 重新接管
                // 防止之前是 Hidden 状态，恢复后 Location 变了但 _isHidden 仍为 true，导致自动隐藏逻辑失效
                _isHidden = false;
                _dock = DockEdge.None;
                ApplyEdgeRegion(false);
                KeepVisible(0.5); // 短暂保持可见，让用户确认位置，随后由 Timer 自动隐藏
            }
            else
            {
                var screen = Screen.FromControl(_form);
                var area = screen.WorkingArea;
                if (_cfg.Position.X >= 0) _form.Location = _cfg.Position;
                else
                {
                    int x = area.Right - _form.Width - 50; 
                    int y = area.Top + (area.Height - _form.Height) / 2;
                    _form.Location = new Point(x, y);
                }
            }
        }

        public void ClampToScreen(bool force = false)
        {
            if (!_cfg.ClampToScreen && !force) return;

            var area = Screen.FromControl(_form).WorkingArea;
            int x = _form.Left;
            int y = _form.Top;

            // 修复逻辑：
            // 如果启用了自动隐藏，允许窗口稍微贴边，而不是强制弹开
            // 只有当窗口完全跑出屏幕外时，才强制拉回
            // 之前的 margin = _hideThreshold + 1 逻辑会导致：用户刚拖到边缘想隐藏，就被弹回来了

            if (x < area.Left) x = area.Left;
            if (x + _form.Width > area.Right) x = area.Right - _form.Width;
            if (y < area.Top) y = area.Top;
            if (y + _form.Height > area.Bottom) y = area.Bottom - _form.Height;

            _form.Location = new Point(x, y);
        }

        private void SetSafeLocation(Rectangle area, int x, int y)
        {
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            if (x + _form.Width > area.Right) x = area.Right - _form.Width;
            if (y + _form.Height > area.Bottom) y = area.Bottom - _form.Height;
            _form.Location = new Point(x, y);
        }

        // =================================================================
        // 快捷动作
        // =================================================================
        public void HandleDoubleClick()
        {
            switch (_cfg.MainFormDoubleClickAction)
            {
                case 1: OpenTaskManager(); break;
                case 2: OpenSettings(); break;
                case 3: OpenTrafficHistory(); break;
                case 4: CleanMemory(); break;
                case 5: WebActions.OpenWebMonitor(_cfg); break;
                case 0: default: ToggleLayoutMode(); break;
            }
        }

        public void OpenTaskManager()
        {
            try { Process.Start(new ProcessStartInfo("taskmgr") { UseShellExecute = true }); } catch { }
        }

        public void OpenSettings()
        {
            foreach (Form f in Application.OpenForms) { if (f is SettingsForm) { f.Activate(); return; } }
            new SettingsForm(_cfg, _ui, (MainForm)_form).Show();
        }

        public void OpenTrafficHistory()
        {
            foreach (Form f in Application.OpenForms) { if (f is TrafficHistoryForm) { f.Activate(); return; } }
            new TrafficHistoryForm(_cfg).Show();
        }

        public async void CleanMemory()
        {
            try { using (var form = new CleanMemoryForm()) await form.StartCleaningAsync(); } catch { }
        }

        // =================================================================
        // 启动流程
        // =================================================================
        public async Task RunStartupChecksAsync()
        {
            try
            {
                if (HardwareMonitor.Instance != null) await HardwareMonitor.Instance.SmartCheckDriver();
                await UpdateChecker.CheckAsync();
                CheckUpdateSuccess();

                // 如果发现新版本，重新构建菜单以显示“发现新版本”按钮
                if (UpdateChecker.IsUpdateFound)
                {
                    _form.BeginInvoke(new Action(() => RebuildMenus()));
                }

                // [Fix] 再次确认窗口属性（置顶、穿透），作为启动后的二次校验，确保功能与 UI 勾选一致
                _form.BeginInvoke(new Action(() => {
                    Core.Actions.AppActions.ApplyWindowAttributes(_cfg, (MainForm)_form);
                }));
            }
            catch { }
        }

        private void CheckUpdateSuccess()
        {
            string tokenPath = Path.Combine(AppContext.BaseDirectory, "update_success");
            if (File.Exists(tokenPath))
            {
                try { File.Delete(tokenPath); } catch { }
                string title = "⚡️LiteMonitor_v" + UpdateChecker.GetCurrentVersion();
                string content = _cfg.Language == "zh" ? "🎉 软件已成功更新到最新版本！" : "🎉 Software updated to latest version!";
                ShowNotification(title, content, ToolTipIcon.Info);
            }
        }

        public void Dispose()
        {
            _tray.Visible = false;
            _tray.Dispose();
            _autoHideTimer?.Stop();
            _autoHideTimer?.Dispose();
            _topMostTimer?.Stop();
            _topMostTimer?.Dispose();
        }
    }
}
