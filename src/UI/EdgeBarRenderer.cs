using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Helpers;

namespace LiteMonitor
{
    /// <summary>
    /// 贴边收起态信息条渲染器（v2：紧凑胶囊卡片）
    /// · 收起后只保留一块 32×64 的小卡片（由 MainFormBizHelper 用 Region 裁形，其余部分不可见）
    /// · 网速条：中线双向 —— 上传向上（lavender）、下载向下（teal）；对数共用刻度（1KB/s≈8% → 100MB/s=100%）
    /// · 内存条：线性 0-100%，阈值配色（BarLow/BarMid/BarHigh）
    /// · 支持 右缘/左缘（竖条）与 顶缘（横条）；横条时上传向左、下载向右
    /// </summary>
    public static class EdgeBarRenderer
    {
        public const int PieceLength = 64;   // 收起卡片长度（竖条高 / 横条宽）
        public const int PieceRadius = 12;   // 卡片圆角（圆润一点更优雅）

        private const int MarginNear = 5;    // 靠桌面内容一侧留白
        private const int MarginFar = 4;     // 靠屏幕边一侧留白
        private const int Gap = 2;           // 两条之间
        private const int NetWidth = 12;     // 网速条厚度
        private const int MemWidth = 9;      // 内存条厚度
        private const int BarInset = 1;      // 填充内缩
        private const double LogMaxKb = 100.0 * 1024; // 对数满格 = 100 MB/s

        private static double NetPct(float? bps)
        {
            if (!bps.HasValue || bps.Value <= 0) return 0;
            double kb = bps.Value / 1024.0;
            double p = Math.Log10(1 + kb) / Math.Log10(1 + LogMaxKb);
            return Math.Max(0, Math.Min(1, p));
        }

        /// <summary>收起卡片在窗口坐标系中的矩形（窗口其余部分用 Region 裁掉）</summary>
        public static Rectangle GetPieceRect(MainFormBizHelper.DockEdge dock, int formW, int formH, int stripWidth)
        {
            stripWidth = Math.Clamp(stripWidth, 10, 48);
            int maxLen = (dock == MainFormBizHelper.DockEdge.Top ? formW : formH) - 20;
            int len = Math.Min(PieceLength, Math.Max(24, maxLen));
            return dock switch
            {
                MainFormBizHelper.DockEdge.Right => new Rectangle(0, (formH - len) / 2, stripWidth, len),
                MainFormBizHelper.DockEdge.Left => new Rectangle(formW - stripWidth, (formH - len) / 2, stripWidth, len),
                MainFormBizHelper.DockEdge.Top => new Rectangle((formW - len) / 2, formH - stripWidth, len, stripWidth),
                _ => Rectangle.Empty
            };
        }

        public static GraphicsPath MakeRoundRect(Rectangle r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 1f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Render(Graphics g, Theme t, MainFormBizHelper.DockEdge dock, int stripWidth,
                                  int formWidth, int formHeight,
                                  float? memPct, float? netUpBps, float? netDownBps,
                                  double memWarn, double memCrit)
        {
            if (dock == MainFormBizHelper.DockEdge.None || dock == MainFormBizHelper.DockEdge.Bottom) return;
            if (formWidth <= 0 || formHeight <= 0) return;

            var piece = GetPieceRect(dock, formWidth, formHeight, stripWidth);
            if (piece.IsEmpty || piece.Width < 12 || piece.Height < 24) return;

            var oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // ===== 卡片背景（纯色卡面、无描边——干净优雅风；同时修复未绘制区域露黑） =====
            using (var bgPath = MakeRoundRect(piece, PieceRadius))
            {
                using var bgBrush = new SolidBrush(ThemeManager.ParseColor(t.Color.GroupBackground));
                g.FillPath(bgBrush, bgPath);
            }

            double mem = Math.Clamp(memPct ?? 0, 0, 100);
            Color trackColor = ThemeManager.ParseColor(t.Color.BarBackground);
            Color memColor = ThemeManager.ParseColor(
                mem >= memCrit ? t.Color.BarHigh :
                mem >= memWarn ? t.Color.BarMid : t.Color.BarLow);

            // 网速双向条配色：Catppuccin（teal=下载 / lavender=上传），按主题背景明暗自动选择
            bool lightTheme = ThemeManager.ParseColor(t.Color.Background).GetBrightness() > 0.6;
            Color downColor = lightTheme ? Color.FromArgb(23, 146, 153) : Color.FromArgb(148, 226, 213);
            Color upColor = lightTheme ? Color.FromArgb(114, 135, 253) : Color.FromArgb(180, 190, 254);
            Color divColor = Color.FromArgb(60, ThemeManager.ParseColor(t.Color.TextGroup));

            using var trackBrush = new SolidBrush(trackColor);
            using var memBrush = new SolidBrush(memColor);
            using var upBrush = new SolidBrush(upColor);
            using var downBrush = new SolidBrush(downColor);
            using var divPen = new Pen(divColor, 1f);

            double upP = NetPct(netUpBps);
            double downP = NetPct(netDownBps);

            if (dock == MainFormBizHelper.DockEdge.Top)
            {
                // ===== 顶缘：横条（上传向左、下载向右；内存自左向右）=====
                int barLeft = piece.X + MarginNear;
                int barW = piece.Width - MarginNear - MarginFar;
                int netY = piece.Y + MarginNear;
                int memY = netY + NetWidth + Gap;
                if (barW < 20) { g.SmoothingMode = oldSmoothing; return; }

                DrawBidirectionalH(g, upBrush, downBrush, divPen, trackBrush, new Rectangle(barLeft, netY, barW, NetWidth), upP, downP);
                DrawBarH(g, trackBrush, memBrush, new Rectangle(barLeft, memY, barW, MemWidth), mem / 100.0);
            }
            else
            {
                // ===== 右/左缘：竖条（上传向上、下载向下；内存自下而上）=====
                int netX = piece.X + MarginNear;
                int memX = netX + NetWidth + Gap;
                int barTop = piece.Y + MarginNear;
                int barH = piece.Height - MarginNear - MarginFar;
                if (barH < 20) { g.SmoothingMode = oldSmoothing; return; }

                DrawBidirectionalV(g, upBrush, downBrush, divPen, trackBrush, new Rectangle(netX, barTop, NetWidth, barH), upP, downP);
                DrawBarV(g, trackBrush, memBrush, new Rectangle(memX, barTop, MemWidth, barH), mem / 100.0);
            }

            g.SmoothingMode = oldSmoothing;
        }

        private static void DrawBidirectionalV(Graphics g, Brush up, Brush down, Pen div, Brush track, Rectangle r, double upP, double downP)
        {
            using var path = MakeRoundRect(r, r.Width / 2f);
            g.FillPath(track, path);
            int center = r.Y + r.Height / 2;
            float half = r.Height / 2f - BarInset;
            var st = g.Save();
            g.SetClip(path);
            if (upP >= 0.02) { int uh = (int)(half * upP); g.FillRectangle(up, r.X + BarInset, center - uh, r.Width - BarInset * 2, uh); }
            if (downP >= 0.02) { int dh = (int)(half * downP); g.FillRectangle(down, r.X + BarInset, center, r.Width - BarInset * 2, dh); }
            g.Restore(st);
            g.DrawLine(div, r.X + 2, center, r.Right - 2, center);
        }

        private static void DrawBarV(Graphics g, Brush track, Brush fill, Rectangle r, double pct)
        {
            using var path = MakeRoundRect(r, r.Width / 2f);
            g.FillPath(track, path);
            if (pct < 0.02) return;
            int fh = (int)((r.Height - BarInset * 2) * pct);
            var st = g.Save();
            g.SetClip(path);
            g.FillRectangle(fill, r.X + BarInset, r.Bottom - BarInset - fh, r.Width - BarInset * 2, fh);
            g.Restore(st);
        }

        private static void DrawBidirectionalH(Graphics g, Brush up, Brush down, Pen div, Brush track, Rectangle r, double upP, double downP)
        {
            using var path = MakeRoundRect(r, r.Height / 2f);
            g.FillPath(track, path);
            int center = r.X + r.Width / 2;
            float half = r.Width / 2f - BarInset;
            var st = g.Save();
            g.SetClip(path);
            if (upP >= 0.02) { int uw = (int)(half * upP); g.FillRectangle(up, center - uw, r.Y + BarInset, uw, r.Height - BarInset * 2); }
            if (downP >= 0.02) { int dw = (int)(half * downP); g.FillRectangle(down, center, r.Y + BarInset, dw, r.Height - BarInset * 2); }
            g.Restore(st);
            g.DrawLine(div, center, r.Y + 2, center, r.Bottom - 2);
        }

        private static void DrawBarH(Graphics g, Brush track, Brush fill, Rectangle r, double pct)
        {
            using var path = MakeRoundRect(r, r.Height / 2f);
            g.FillPath(track, path);
            if (pct < 0.02) return;
            int fw = (int)((r.Width - BarInset * 2) * pct);
            var st = g.Save();
            g.SetClip(path);
            g.FillRectangle(fill, r.X + BarInset, r.Y + BarInset, fw, r.Height - BarInset * 2);
            g.Restore(st);
        }
    }
}
