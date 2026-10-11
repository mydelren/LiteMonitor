using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Helpers;

namespace LiteMonitor
{
    /// <summary>
    /// 贴边收起态信息条渲染器（v5「原生」）
    /// · 收起 = 窗口真实缩小为小卡片（右/左缘=竖条，顶缘=横条）；Win11 原生圆角与阴影由 DWM 提供
    /// · 窗口向外多伸出 Overhang 像素：藏掉外侧圆角，可见侧形成"左弧右平、直抵屏幕边"
    /// · 两根胶囊条同宽；网速=中线双向（上传向上/下载向下，对数共用刻度），内存=线性阈值色
    /// </summary>
    public static class EdgeBarRenderer
    {
        public const int EdgeBarLength = 38;    // 卡片可见长度（竖条=高 / 横条=宽）· C 档几何
        public const int EdgeBarOverhang = 6;   // 伸出屏幕外的部分（藏外侧圆角）

        private const int Gap = 2;              // 两条之间
        private const int BarW = 6;             // 条宽（两根一致）· C 档几何
        private const int EndPad = 4;           // 长轴两端留白（收紧，只为收住）
        private const int BarInset = 1;         // 填充内缩
        private const double LogMaxKb = 100.0 * 1024; // 对数满格 = 100 MB/s

        /// <summary>磨砂（亚克力）模式的"透明键"底色：该色像素会被 Form.TransparencyKey 透出系统模糊背板</summary>
        public static readonly Color AcrylicKeyColor = Color.FromArgb(1, 1, 1);

        private static double NetPct(float? bps)
        {
            if (!bps.HasValue || bps.Value <= 0) return 0;
            double kb = bps.Value / 1024.0;
            double p = Math.Log10(1 + kb) / Math.Log10(1 + LogMaxKb);
            return Math.Max(0, Math.Min(1, p));
        }

        private static GraphicsPath MakeRoundRect(Rectangle r, float radius)
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

        public static void Render(Graphics g, Theme t, MainFormBizHelper.DockEdge dock, int visibleW,
                                  int formW, int formH,
                                  float? memPct, float? netUpBps, float? netDownBps,
                                  double memWarn, double memCrit, int material = 0)
        {
            if (dock == MainFormBizHelper.DockEdge.None || dock == MainFormBizHelper.DockEdge.Bottom) return;
            if (formW <= 0 || formH <= 0) return;

            var oldSmoothing = g.SmoothingMode;
            // 磨砂模式下必须关抗锯齿：AA 边缘会与近黑的"透明键"底色混色，在上/左侧留下 1px 黑描边（实测）
            g.SmoothingMode = material == 2 ? SmoothingMode.None : SmoothingMode.AntiAlias;

            // 整窗铺卡片底色（含伸出屏幕外的部分；圆角与阴影由 Win11 DWM 提供）
            // 材质 2 = 磨砂：铺"透明键"色，由 TransparencyKey + 亚克力背板接管底色；其余材质铺主题卡面色
            Color cardBg = material == 2 ? AcrylicKeyColor : ThemeManager.ParseColor(t.Color.GroupBackground);
            using (var bgBrush = new SolidBrush(cardBg))
                g.FillRectangle(bgBrush, 0, 0, formW, formH);

            double mem = Math.Clamp(memPct ?? 0, 0, 100);
            Color trackColor = ThemeManager.ParseColor(t.Color.BarBackground);
            Color memColor = ThemeManager.ParseColor(
                mem >= memCrit ? t.Color.BarHigh :
                mem >= memWarn ? t.Color.BarMid : t.Color.BarLow);

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

            int margin = Math.Max(2, (visibleW - (BarW * 2 + Gap)) / 2);  // 条组在宽度轴内居中（随可见宽度自适应）

            if (dock == MainFormBizHelper.DockEdge.Top)
            {
                // ===== 顶缘：横条（上传向左、下载向右；内存自左向右）=====
                int netY = EdgeBarOverhang + margin;
                int memY = netY + BarW + Gap;
                int left = EndPad;
                int w = formW - EndPad * 2;
                if (w < 20) { g.SmoothingMode = oldSmoothing; return; }

                DrawBidirectionalH(g, upBrush, downBrush, divPen, trackBrush, new Rectangle(left, netY, w, BarW), upP, downP);
                DrawBarH(g, trackBrush, memBrush, new Rectangle(left, memY, w, BarW), mem / 100.0);
            }
            else
            {
                // ===== 右/左缘：竖条（上传向上、下载向下；内存自下而上）=====
                int barTop = EndPad;
                int barH = formH - EndPad * 2;
                if (barH < 20) { g.SmoothingMode = oldSmoothing; return; }

                int netX, memX;
                if (dock == MainFormBizHelper.DockEdge.Right)
                {
                    // 桌面侧在左：网速条在前
                    netX = margin;
                    memX = netX + BarW + Gap;
                }
                else
                {
                    // 左缘：镜像（桌面侧在右，网速条仍在前）
                    netX = formW - margin - BarW;
                    memX = netX - Gap - BarW;
                }

                DrawBidirectionalV(g, upBrush, downBrush, divPen, trackBrush, new Rectangle(netX, barTop, BarW, barH), upP, downP);
                DrawBarV(g, trackBrush, memBrush, new Rectangle(memX, barTop, BarW, barH), mem / 100.0);
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
