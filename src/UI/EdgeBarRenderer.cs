using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Helpers;

namespace LiteMonitor
{
    /// <summary>
    /// 贴边收起态信息条渲染器
    /// · 网速条（厚 12px）：中线双向 —— 上传向上、下载向下；对数共用刻度（1KB/s≈8%，1MB/s≈60%，10MB/s≈80%，100MB/s=100%）
    /// · 内存条（厚 10px）：线性 0-100%，阈值配色（BarLow/BarMid/BarHigh）
    /// · 支持 右缘 / 左缘（竖条）与 顶缘（横条）三种停靠；横条时上传向左、下载向右
    /// 绘制区域 = 主窗口当前可见的窄条（窗口其余部分已滑出屏外）
    /// </summary>
    public static class EdgeBarRenderer
    {
        private const int MarginInner = 3;   // 桌面内容一侧留白
        private const int Gap = 3;           // 两条之间的间距
        private const int NetWidth = 12;     // 网速条厚度
        private const int MemWidth = 10;     // 内存条厚度
        private const int BarPad = 5;        // 条两端留白（竖条为上下，横条为左右）
        private const int BarInset = 1;      // 填充相对轨道的内缩
        private const double LogMaxKb = 100.0 * 1024; // 对数刻度满格 = 100 MB/s

        /// <summary>网速 → 0..1 对数映射（上传/下载共用同一把尺）</summary>
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

        /// <param name="stripWidth">收起后可见宽度（px，已夹在 10-48）</param>
        /// <param name="formWidth">窗口宽</param>
        /// <param name="formHeight">窗口高</param>
        /// <param name="memPct">内存占用百分比 0-100</param>
        /// <param name="netUpBps">上传 字节/秒</param>
        /// <param name="netDownBps">下载 字节/秒</param>
        /// <param name="memWarn">内存告警阈值(%)</param>
        /// <param name="memCrit">内存临界阈值(%)</param>
        public static void Render(Graphics g, Theme t, MainFormBizHelper.DockEdge dock, int stripWidth,
                                  int formWidth, int formHeight,
                                  float? memPct, float? netUpBps, float? netDownBps,
                                  double memWarn, double memCrit)
        {
            if (dock == MainFormBizHelper.DockEdge.None || dock == MainFormBizHelper.DockEdge.Bottom) return;
            if (formWidth <= 0 || formHeight <= 0) return;
            stripWidth = Math.Clamp(stripWidth, 10, 48);

            var oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            double mem = Math.Clamp(memPct ?? 0, 0, 100);
            Color trackColor = ThemeManager.ParseColor(t.Color.BarBackground);
            Color memColor = ThemeManager.ParseColor(
                mem >= memCrit ? t.Color.BarHigh :
                mem >= memWarn ? t.Color.BarMid : t.Color.BarLow);

            // 网速双向条配色：Catppuccin（teal=下载 / lavender=上传），按主题背景明暗自动选择
            bool lightTheme = ThemeManager.ParseColor(t.Color.Background).GetBrightness() > 0.6;
            Color downColor = lightTheme ? Color.FromArgb(23, 146, 153) : Color.FromArgb(148, 226, 213);
            Color upColor = lightTheme ? Color.FromArgb(114, 135, 253) : Color.FromArgb(180, 190, 254);
            Color divColor = Color.FromArgb(70, ThemeManager.ParseColor(t.Color.TextGroup));

            using var trackBrush = new SolidBrush(trackColor);
            using var memBrush = new SolidBrush(memColor);
            using var upBrush = new SolidBrush(upColor);
            using var downBrush = new SolidBrush(downColor);
            using var divPen = new Pen(divColor, 1f);

            double upP = NetPct(netUpBps);
            double downP = NetPct(netDownBps);

            if (dock == MainFormBizHelper.DockEdge.Top)
            {
                // ===== 顶缘：横条（上传向左、下载向右；内存自左向右） =====
                int y0 = formHeight - stripWidth;
                int netY = y0 + MarginInner;
                int memY = netY + NetWidth + Gap;
                int left = BarPad;
                int w = formWidth - BarPad * 2;
                if (w < 20) { g.SmoothingMode = oldSmoothing; return; }

                var netRect = new Rectangle(left, netY, w, NetWidth);
                using (var path = MakeRoundRect(netRect, NetWidth / 2f))
                {
                    g.FillPath(trackBrush, path);
                    int center = left + w / 2;
                    float half = w / 2f - BarInset;
                    var st = g.Save();
                    g.SetClip(path);
                    if (upP >= 0.02) { int uw = (int)(half * upP); g.FillRectangle(upBrush, center - uw, netY + BarInset, uw, NetWidth - BarInset * 2); }
                    if (downP >= 0.02) { int dw = (int)(half * downP); g.FillRectangle(downBrush, center, netY + BarInset, dw, NetWidth - BarInset * 2); }
                    g.Restore(st);
                    g.DrawLine(divPen, center, netY + 2, center, netY + NetWidth - 2);
                }

                var memRect = new Rectangle(left, memY, w, MemWidth);
                using (var path = MakeRoundRect(memRect, MemWidth / 2f))
                {
                    g.FillPath(trackBrush, path);
                    double mp = mem / 100.0;
                    if (mp >= 0.02)
                    {
                        int fw = (int)((w - BarInset * 2) * mp);
                        var st = g.Save();
                        g.SetClip(path);
                        g.FillRectangle(memBrush, left + BarInset, memY + BarInset, fw, MemWidth - BarInset * 2);
                        g.Restore(st);
                    }
                }
            }
            else
            {
                // ===== 右/左缘：竖条（上传向上、下载向下；内存自下而上） =====
                // 可见窄条从「桌面内容一侧」向「屏幕边一侧」排布：[3][网速12][3][内存10][4]
                int x0 = dock == MainFormBizHelper.DockEdge.Right ? 0 : formWidth - stripWidth;
                int netX = x0 + MarginInner;
                int memX = netX + NetWidth + Gap;
                int top = BarPad;
                int h = formHeight - BarPad * 2;
                if (h < 20) { g.SmoothingMode = oldSmoothing; return; }

                var netRect = new Rectangle(netX, top, NetWidth, h);
                using (var path = MakeRoundRect(netRect, NetWidth / 2f))
                {
                    g.FillPath(trackBrush, path);
                    int center = top + h / 2;
                    float half = h / 2f - BarInset;
                    var st = g.Save();
                    g.SetClip(path);
                    if (upP >= 0.02) { int uh = (int)(half * upP); g.FillRectangle(upBrush, netX + BarInset, center - uh, NetWidth - BarInset * 2, uh); }
                    if (downP >= 0.02) { int dh = (int)(half * downP); g.FillRectangle(downBrush, netX + BarInset, center, NetWidth - BarInset * 2, dh); }
                    g.Restore(st);
                    g.DrawLine(divPen, netX + 2, center, netX + NetWidth - 2, center);
                }

                var memRect = new Rectangle(memX, top, MemWidth, h);
                using (var path = MakeRoundRect(memRect, MemWidth / 2f))
                {
                    g.FillPath(trackBrush, path);
                    double mp = mem / 100.0;
                    if (mp >= 0.02)
                    {
                        int fh = (int)((h - BarInset * 2) * mp);
                        var st = g.Save();
                        g.SetClip(path);
                        g.FillRectangle(memBrush, memX + BarInset, top + h - BarInset - fh, MemWidth - BarInset * 2, fh);
                        g.Restore(st);
                    }
                }
            }

            g.SmoothingMode = oldSmoothing;
        }
    }
}
