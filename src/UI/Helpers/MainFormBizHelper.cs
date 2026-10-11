using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
        private int EdgeVisibleWidth => Math.Clamp(_cfg.EdgeBarWidth, 18, 48);
        private readonly int _hideThreshold = 10;
        public enum DockEdge { None, Left, Right, Top, Bottom }
        private DockEdge _dock = DockEdge.None;
        private DateTime _keepVisibleUntil = DateTime.MinValue;
        private Size _normalClientSize = Size.Empty;   // 收起前的正常窗口尺寸（收起态 = 小卡片窗口）
        private int _preHideTop = 0;                   // 收起前的窗口 Top（左/右缘展开时还原）
        private int _preHideLeft = 0;                  // 收起前的窗口 Left（顶缘展开时还原）

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

            // 磨砂模式：透明键只需启动时设置一次（运行中切换 TransparencyKey 有重建窗口句柄之虞）
            if (_cfg.EdgeBarMaterial >= 2) _form.TransparencyKey = EdgeBarRenderer.GetGapColor(_cfg.EdgeBarMaterial);
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
                ExpandToNormal();
                ClampToScreen(force: true); // 强制拉回
            }
            KeepVisible(3.0);
        }

        /// <summary>用户拖动结束：若处于收起态则展开回完整面板（并贴回该缘）</summary>
        public void OnUserDragEnd()
        {
            if (_isHidden)
            {
                _isHidden = false;
                ExpandToNormal();
            }
            KeepVisible(2.0);
        }

        /// <summary>从收起卡片展开回完整面板：恢复尺寸并把窗口贴回停靠的屏幕缘</summary>
        private void ExpandToNormal()
        {
            var area = Screen.FromControl(_form).WorkingArea;
            if (_normalClientSize != Size.Empty)
            {
                _form.ClientSize = _normalClientSize;
                _normalClientSize = Size.Empty;
            }
            // 必须还原收起前的位置：改 ClientSize 时窗口顶边不动，
            // 若不还原，面板中心每展开一次就下移 (正常高-卡片长)/2，逐次累积（实测会一直漂到屏幕底）
            if (_dock == DockEdge.Right) { _form.Left = area.Right - _form.Width; _form.Top = _preHideTop; }
            else if (_dock == DockEdge.Left) { _form.Left = area.Left; _form.Top = _preHideTop; }
            else if (_dock == DockEdge.Top) { _form.Top = area.Top; _form.Left = _preHideLeft; }
            _dock = DockEdge.None;
            RestoreCardMaterial();
            _form.Invalidate();
        }

        /// <summary>进入收起态：按设置应用卡片材质（0=纯色 1=半透明 2=磨砂亚克力），并换成小圆角档</summary>
        private void ApplyCardMaterial()
        {
            int mat = _cfg.EdgeBarMaterial;
            if (mat == 1)
            {
                _form.Opacity = Math.Clamp(_cfg.EdgeBarTintOpacity, 0.5, 1.0);
            }
            else if (mat >= 2)
            {
                Color gap = EdgeBarRenderer.GetGapColor(mat);
                if (_form.TransparencyKey != gap)
                    _form.TransparencyKey = gap;
                _form.Opacity = 1.0;      // 磨砂自带透明，避免与整窗不透明度叠出灰
                _winHelper.SetBackdrop(mat == 3 ? 2 : 3);   // 3=云母(Mica) / 2=亚克力（DWMSBT：2=MainWindow 3=Transient）
            }
            _winHelper.SetCornerPreference(MainFormWinHelper.CornerRoundSmall);
        }

        /// <summary>离开收起态：恢复常规不透明度与圆角（背板材质留着不碍事：面板整面不透明绘制，照不到它）</summary>
        private void RestoreCardMaterial()
        {
            _form.Opacity = Math.Clamp(_cfg.Opacity, 0.1, 1.0);
            _winHelper.SetCornerPreference(MainFormWinHelper.CornerRound);
        }

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

            // 靠边 -> 收起
            if (!_isHidden && shouldHide && !_form.Bounds.Contains(cursor))
            {
                if (!_cfg.EdgeBarEnabled)
                {
                    // 传统模式：窗口滑出屏外，只留 4px
                    if (nearRight) { _form.Left = area.Right - HideWidthPlain; _dock = DockEdge.Right; }
                    else if (nearLeft) { _form.Left = area.Left - (_form.Width - HideWidthPlain); _dock = DockEdge.Left; }
                    else if (nearTop) { _form.Top = area.Top - (_form.Height - HideWidthPlain); _dock = DockEdge.Top; }
                    _isHidden = true;
                    return;
                }

                // 原生信息条模式：窗口真实缩小为贴边小卡片（Win11 DWM 提供原生圆角+阴影）
                _normalClientSize = _form.ClientSize;
                _preHideTop = _form.Top;
                _preHideLeft = _form.Left;
                int visW = EdgeVisibleWidth;
                int len = EdgeBarRenderer.EdgeBarLength;
                int over = EdgeBarRenderer.EdgeBarOverhang;
                int ccx = _form.Left + _form.Width / 2;
                int ccy = _form.Top + _form.Height / 2;

                if (nearRight)
                {
                    _dock = DockEdge.Right;
                    _form.ClientSize = new Size(visW + over, len);
                    _form.Left = area.Right - visW;      // 右侧伸出屏幕外（藏掉外侧圆角）
                    _form.Top = Math.Clamp(ccy - len / 2, area.Top, area.Bottom - len);
                }
                else if (nearLeft)
                {
                    _dock = DockEdge.Left;
                    _form.ClientSize = new Size(visW + over, len);
                    _form.Left = area.Left - over;
                    _form.Top = Math.Clamp(ccy - len / 2, area.Top, area.Bottom - len);
                }
                else
                {
                    _dock = DockEdge.Top;
                    _form.ClientSize = new Size(len, visW + over);
                    _form.Top = area.Top - over;
                    _form.Left = Math.Clamp(ccx - len / 2, area.Left, area.Right - len);
                }
                ApplyCardMaterial();
                _isHidden = true;
                _form.Invalidate();
                return;
            }

            // 鼠标靠近 -> 展开
            if (_isHidden)
            {
                const int hoverBand = 30;
                int visW = _cfg.EdgeBarEnabled ? EdgeVisibleWidth : HideWidthPlain;
                bool isMouseOnHiddenPanel = false;
                if (_dock == DockEdge.Right) isMouseOnHiddenPanel = cursor.X >= area.Right - visW && cursor.Y >= _form.Top && cursor.Y <= _form.Bottom;
                else if (_dock == DockEdge.Left) isMouseOnHiddenPanel = cursor.X <= area.Left + visW && cursor.Y >= _form.Top && cursor.Y <= _form.Bottom;
                else if (_dock == DockEdge.Top) isMouseOnHiddenPanel = cursor.Y <= area.Top + visW && cursor.X >= _form.Left && cursor.X <= _form.Right;

                if (isMouseOnHiddenPanel)
                {
                    bool expand = (_dock == DockEdge.Right && cursor.X >= area.Right - hoverBand)
                               || (_dock == DockEdge.Left && cursor.X <= area.Left + hoverBand)
                               || (_dock == DockEdge.Top && cursor.Y <= area.Top + hoverBand);
                    if (expand)
                    {
                        _isHidden = false;
                        ExpandToNormal();
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
                _normalClientSize = Size.Empty;
                RestoreCardMaterial();
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
