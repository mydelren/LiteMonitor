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

        /// <summary>磨砂模式的"留空色"（与 Form.TransparencyKey 一致）：留空像素被键出透出背板，
        /// 但抗锯齿的过渡色会混向它——键色必须尽量贴近可见的卡面色，否则边缘出现黑晕/白晕（两侧都实测翻过车）。
        /// 亚克力底色漂移 → 用中性浅灰；云母底色稳定 ≈ #F0F3F9 → 直接取云母卡面色，AA 过渡与卡面完全一致</summary>
        public static readonly Color AcrylicKeyColor = Color.FromArgb(0xD5, 0xD8, 0xDC);
        public static readonly Color MicaKeyColor = Color.FromArgb(0xF0, 0xF3, 0xF9);

        public static Color GetGapColor(int material) => material >= 3 ? MicaKeyColor : AcrylicKeyColor;

        /// <summary>磨砂卡专用：轨道加深（浅色轨道会在灰底上"隐身"，加深后胶囊轮廓保持清晰）</summary>
        private static readonly Color FrostTrack = Color.FromArgb(0xC4, 0xCA, 0xD6);

        /// <summary>云母卡专用：柔和填充色（淡一档——高饱和填充在浅卡上会"抢眼"，观感像撑出胶囊）</summary>
        private static readonly Color SoftUp = Color.FromArgb(0xB4, 0xBE, 0xFE);
        private static readonly Color SoftDown = Color.FromArgb(0x94, 0xE2, 0xD5);
        private static readonly Color SoftGreen = Color.FromArgb(0xA6, 0xE3, 0xA1);
        private static readonly Color SoftAmber = Color.FromArgb(0xF9, 0xE2, 0xAF);
        private static readonly Color SoftRed = Color.FromArgb(0xF3, 0x8B, 0xA8);

        private static double NetPct(float? bps)
        {
            if (!bps.HasValue || bps.Value <= 0) return 0;
            double kb = bps.Value / 1024.0;
            double p = Math.Log10(1 + kb) / Math.Log10(1 + LogMaxKb);
            return Math.Max(0, Math.Min(1, p));
        }

        /// <summary>仅"外端"带圆角的矩形路径（进度填充用：外端跟随胶囊圆弧、进度端保持平直）</summary>
        private static GraphicsPath MakeEndRoundRect(Rectangle r, float radius, bool vertical)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 1f) { path.AddRectangle(r); return path; }
            if (vertical)
            {
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);          // 左下
                path.AddLine(r.X, r.Y, r.Right, r.Y);                  // 顶边（进度端，平直）
                path.AddLine(r.Right, r.Y, r.Right, r.Bottom - d);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);   // 右下
                path.CloseFigure();
            }
            else
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);                  // 左上
                path.AddLine(r.Right, r.Y, r.Right, r.Bottom);
                path.AddLine(r.Right, r.Bottom, r.X + d / 2, r.Bottom);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);          // 左下
                path.CloseFigure();
            }
            return path;
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

            bool frost = material >= 2;      // 2=磨砂(亚克力) 3=磨砂(云母)
            bool mica = material >= 3;       // 云母：留空色=卡面色 → AA 无晕可选，端头全圆
            var oldSmoothing = g.SmoothingMode;
            var oldOffset = g.PixelOffsetMode;
            // GDI+ AA 默认带半像素偏移（轨道发虚、填充锐利，两者错位）→ Half 校正：整数边利落、圆弧平滑，填充与轨道对齐
            g.PixelOffsetMode = PixelOffsetMode.Half;
            // 亚克力（底色漂移，无固定色可配 AA）必须关 AA 防晕；云母留空色与卡面一致，AA 全开
            g.SmoothingMode = (frost && !mica) ? SmoothingMode.None : SmoothingMode.AntiAlias;
            float endR = (frost && !mica) ? 2f : BarW / 2f;   // 亚克力端头 2px（抗阶梯），其余全圆
            int bi = frost ? 0 : BarInset;                    // 磨砂下填充全宽（6px 上 1px 内缩太"贴片"）

            // 整窗铺卡片底色（含伸出屏幕外的部分；圆角与阴影由 Win11 DWM 提供）
            // 磨砂：铺"留空"色，由 TransparencyKey + 系统背板接管底色；其余材质铺主题卡面色
            Color cardBg = frost ? GetGapColor(material) : ThemeManager.ParseColor(t.Color.GroupBackground);
            using (var bgBrush = new SolidBrush(cardBg))
                g.FillRectangle(bgBrush, 0, 0, formW, formH);

            double mem = Math.Clamp(memPct ?? 0, 0, 100);
            Color trackColor = frost ? FrostTrack : ThemeManager.ParseColor(t.Color.BarBackground);   // 磨砂底上用加深轨道，否则胶囊轮廓会被灰底吞掉
            Color memColor = mica
                ? (mem >= memCrit ? SoftRed : mem >= memWarn ? SoftAmber : SoftGreen)
                : ThemeManager.ParseColor(
                    mem >= memCrit ? t.Color.BarHigh :
                    mem >= memWarn ? t.Color.BarMid : t.Color.BarLow);

            bool lightTheme = ThemeManager.ParseColor(t.Color.Background).GetBrightness() > 0.6;
            Color downColor = mica ? SoftDown : (lightTheme ? Color.FromArgb(23, 146, 153) : Color.FromArgb(148, 226, 213));
            Color upColor = mica ? SoftUp : (lightTheme ? Color.FromArgb(114, 135, 253) : Color.FromArgb(180, 190, 254));
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
                if (w < 20) { g.SmoothingMode = oldSmoothing; g.PixelOffsetMode = oldOffset; return; }

                DrawBidirectionalH(g, upBrush, downBrush, divPen, trackBrush, new Rectangle(left, netY, w, BarW), upP, downP, endR, bi);
                DrawBarH(g, trackBrush, memBrush, new Rectangle(left, memY, w, BarW), mem / 100.0, endR, bi);
            }
            else
            {
                // ===== 右/左缘：竖条（上传向上、下载向下；内存自下而上）=====
                int barTop = EndPad;
                int barH = formH - EndPad * 2;
                if (barH < 20) { g.SmoothingMode = oldSmoothing; g.PixelOffsetMode = oldOffset; return; }

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

                DrawBidirectionalV(g, upBrush, downBrush, divPen, trackBrush, new Rectangle(netX, barTop, BarW, barH), upP, downP, endR, bi);
                DrawBarV(g, trackBrush, memBrush, new Rectangle(memX, barTop, BarW, barH), mem / 100.0, endR, bi);
            }

            g.SmoothingMode = oldSmoothing;
            g.PixelOffsetMode = oldOffset;
        }

        private static void DrawBidirectionalV(Graphics g, Brush up, Brush down, Pen div, Brush track, Rectangle r, double upP, double downP, float endR, int bi)
        {
            using var path = MakeRoundRect(r, endR);
            g.FillPath(track, path);
            int center = r.Y + r.Height / 2;
            float half = r.Height / 2f - bi;
            var st = g.Save();
            g.SetClip(path);
            if (upP >= 0.02) { int uh = (int)(half * upP); g.FillRectangle(up, r.X + bi, center - uh, r.Width - bi * 2, uh); }
            if (downP >= 0.02) { int dh = (int)(half * downP); g.FillRectangle(down, r.X + bi, center, r.Width - bi * 2, dh); }
            g.Restore(st);
            g.DrawLine(div, r.X + 2, center, r.Right - 2, center);
        }

        private static void DrawBarV(Graphics g, Brush track, Brush fill, Rectangle r, double pct, float endR, int bi)
        {
            using var path = MakeRoundRect(r, endR);
            g.FillPath(track, path);
            if (pct >= 0.02)
            {
                int fh = (int)((r.Height - bi * 2) * pct);
                using var fp = MakeEndRoundRect(new Rectangle(r.X + bi, r.Bottom - bi - fh, r.Width - bi * 2, fh), endR, true);
                g.FillPath(fill, fp);   // 外端圆角跟随胶囊、进度端平直（不再用方角+硬裁剪，圆弧端头不再露角）
            }
        }

        private static void DrawBidirectionalH(Graphics g, Brush up, Brush down, Pen div, Brush track, Rectangle r, double upP, double downP, float endR, int bi)
        {
            using var path = MakeRoundRect(r, endR);
            g.FillPath(track, path);
            int center = r.X + r.Width / 2;
            float half = r.Width / 2f - bi;
            var st = g.Save();
            g.SetClip(path);
            if (upP >= 0.02) { int uw = (int)(half * upP); g.FillRectangle(up, center - uw, r.Y + bi, uw, r.Height - bi * 2); }
            if (downP >= 0.02) { int dw = (int)(half * downP); g.FillRectangle(down, center, r.Y + bi, dw, r.Height - bi * 2); }
            g.Restore(st);
            g.DrawLine(div, center, r.Y + 2, center, r.Bottom - 2);
        }

        private static void DrawBarH(Graphics g, Brush track, Brush fill, Rectangle r, double pct, float endR, int bi)
        {
            using var path = MakeRoundRect(r, endR);
            g.FillPath(track, path);
            if (pct >= 0.02)
            {
                int fw = (int)((r.Width - bi * 2) * pct);
                using var fp = MakeEndRoundRect(new Rectangle(r.X + bi, r.Y + bi, fw, r.Height - bi * 2), endR, false);
                g.FillPath(fill, fp);   // 外端（左）圆角跟随胶囊、进度端平直
            }
        }
    }
}
