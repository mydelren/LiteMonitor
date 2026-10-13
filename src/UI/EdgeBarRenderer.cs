using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Helpers;

namespace LiteMonitor
{
    /// <summary>
    /// 贴边收起态信息条渲染器（v6「三列」）
    /// · 收起 = 窗口真实缩小为小卡片（右/左缘=竖条，顶缘=横条）；Win11 原生圆角与阴影由 DWM 提供
    /// · 窗口向外多伸出 Overhang 像素：藏掉外侧圆角，可见侧形成"左弧右平、直抵屏幕边"
    /// · 三列（CPU·内存·网络）：**颜色即身份**——CPU 蓝（≥crit 转红）/ 内存绿→黄→红 / 网络上紫下青；
    ///   云母档每列淡色轨道（V2 定稿）：空条时也能认出哪列（小界面通行做法：固定位置+可记忆颜色，不用图标）
    /// </summary>
    public static class EdgeBarRenderer
    {
        public const int EdgeBarLength = 38;    // 卡片可见长度（竖条=高 / 横条=宽）· C 档几何
        public const int EdgeBarOverhang = 6;   // 伸出屏幕外的部分（藏外侧圆角）

        private const int Lanes = 3;            // 三列：CPU / 内存 / 网络
        private const int Gap = 2;              // 条间距
        private const int BarW = 5;             // 条宽（三根一致）· B 档折中
        private const int EndPad = 4;           // 长轴两端留白
        private const int BarInset = 1;         // 填充内缩（非磨砂）
        private const double LogMaxKb = 100.0 * 1024; // 对数满格 = 100 MB/s

        /// <summary>磨砂模式的"留空色"（与 Form.TransparencyKey 一致）：留空像素被键出透出背板，
        /// 但抗锯齿的过渡色会混向它——键色必须尽量贴近可见的卡面色，否则边缘出现黑晕/白晕（两侧都实测翻过车）。
        /// 亚克力底色漂移 → 用中性浅灰；云母底色稳定 ≈ #F0F3F9 → 直接取云母卡面色，AA 过渡与卡面完全一致</summary>
        public static readonly Color AcrylicKeyColor = Color.FromArgb(0xD5, 0xD8, 0xDC);
        public static readonly Color MicaKeyColor = Color.FromArgb(0xF0, 0xF3, 0xF9);

        public static Color GetGapColor(int material) => material >= 3 ? MicaKeyColor : AcrylicKeyColor;

        /// <summary>亚克力档：轨道加深（浅色轨道会在灰底上"隐身"；云母档改用每列淡色轨道）</summary>
        private static readonly Color FrostTrack = Color.FromArgb(0xC4, 0xCA, 0xD6);

        /// <summary>V2 定稿：云母档每列淡色轨道——颜色即身份，空条/低负载时也能认出是哪列</summary>
        private static readonly Color TrkCpu = Color.FromArgb(0xC9, 0xD6, 0xEE);
        private static readonly Color TrkMem = Color.FromArgb(0xCF, 0xE4, 0xCD);
        private static readonly Color TrkNet = Color.FromArgb(0xC9, 0xE1, 0xE1);

        /// <summary>CPU 身份蓝；≥crit 转红（与内存共用报警色，蓝→红两态）</summary>
        private static readonly Color CpuBlue = Color.FromArgb(0x5B, 0x8D, 0xEF);

        /// <summary>云母卡·内存条专用：柔和中档色（比淡彩深一档、比原饱和色收敛——兼顾"看得清"与"不刺眼"）</summary>
        private static readonly Color SoftGreen = Color.FromArgb(0x62, 0xBE, 0x5E);
        private static readonly Color SoftAmber = Color.FromArgb(0xE9, 0xA6, 0x3C);
        private static readonly Color SoftRed = Color.FromArgb(0xE2, 0x57, 0x6E);

        /// <summary>云母卡·网速条专用：柔和中档色——网速条又细又短，全淡色会糊进轨道看不见（实测用户反馈），
        /// 取"够看清但不刺眼"的中间饱和度；上传=紫（从蓝紫 #7C8CF5 微调，拉开与 CPU 蓝的距离）</summary>
        private static readonly Color MicaUp = Color.FromArgb(0x8B, 0x7C, 0xF0);
        private static readonly Color MicaDown = Color.FromArgb(0x3B, 0xA7, 0xA9);

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
                                  float? cpuPct, float? memPct, float? netUpBps, float? netDownBps,
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
            int bi = frost ? 0 : BarInset;                    // 磨砂下填充全宽（5px 上 1px 内缩太"贴片"）

            // 整窗铺卡片底色（含伸出屏幕外的部分；圆角与阴影由 Win11 DWM 提供）
            // 磨砂：铺"留空"色，由 TransparencyKey + 系统背板接管底色；其余材质铺主题卡面色
            Color cardBg = frost ? GetGapColor(material) : ThemeManager.ParseColor(t.Color.GroupBackground);
            using (var bgBrush = new SolidBrush(cardBg))
                g.FillRectangle(bgBrush, 0, 0, formW, formH);

            double cpu = Math.Clamp(cpuPct ?? 0, 0, 100);
            double mem = Math.Clamp(memPct ?? 0, 0, 100);

            // 轨道：云母=每列淡色身份轨道（V2 定稿）；亚克力=加深轨道；主题模式=主题轨道色
            Color themeTrack = ThemeManager.ParseColor(t.Color.BarBackground);
            Color trkCpu = mica ? TrkCpu : frost ? FrostTrack : themeTrack;
            Color trkMem = mica ? TrkMem : frost ? FrostTrack : themeTrack;
            Color trkNet = mica ? TrkNet : frost ? FrostTrack : themeTrack;

            // 填充：磨砂=身份色（CPU 蓝→红 / 内存绿黄红）；主题模式=主题阈值色链
            Color cpuColor = frost
                ? (cpu >= memCrit ? SoftRed : CpuBlue)
                : ThemeManager.ParseColor(cpu >= memCrit ? t.Color.BarHigh : cpu >= memWarn ? t.Color.BarMid : t.Color.BarLow);
            Color memColor = frost
                ? (mem >= memCrit ? SoftRed : mem >= memWarn ? SoftAmber : SoftGreen)
                : ThemeManager.ParseColor(mem >= memCrit ? t.Color.BarHigh : mem >= memWarn ? t.Color.BarMid : t.Color.BarLow);

            bool lightTheme = ThemeManager.ParseColor(t.Color.Background).GetBrightness() > 0.6;
            Color downColor = frost ? MicaDown : (lightTheme ? Color.FromArgb(23, 146, 153) : Color.FromArgb(148, 226, 213));
            Color upColor = frost ? MicaUp : (lightTheme ? Color.FromArgb(114, 135, 253) : Color.FromArgb(180, 190, 254));
            Color divColor = Color.FromArgb(60, ThemeManager.ParseColor(t.Color.TextGroup));

            using var trkCpuBrush = new SolidBrush(trkCpu);
            using var trkMemBrush = new SolidBrush(trkMem);
            using var trkNetBrush = new SolidBrush(trkNet);
            using var cpuBrush = new SolidBrush(cpuColor);
            using var memBrush = new SolidBrush(memColor);
            using var upBrush = new SolidBrush(upColor);
            using var downBrush = new SolidBrush(downColor);
            using var divPen = new Pen(divColor, 1f);

            double upP = NetPct(netUpBps);
            double downP = NetPct(netDownBps);

            int margin = Math.Max(2, (visibleW - (BarW * Lanes + Gap * (Lanes - 1))) / 2);  // 条组在宽度轴内居中

            if (dock == MainFormBizHelper.DockEdge.Top)
            {
                // ===== 顶缘：横条三行（自上而下 CPU / 内存 / 网络；网络双向：左上传、右下载）=====
                int cpuY = EdgeBarOverhang + margin;
                int memY = cpuY + BarW + Gap;
                int netY = memY + BarW + Gap;
                int left = EndPad;
                int w = formW - EndPad * 2;
                if (w < 20) { g.SmoothingMode = oldSmoothing; g.PixelOffsetMode = oldOffset; return; }

                DrawBarH(g, trkCpuBrush, cpuBrush, new Rectangle(left, cpuY, w, BarW), cpu / 100.0, endR, bi);
                DrawBarH(g, trkMemBrush, memBrush, new Rectangle(left, memY, w, BarW), mem / 100.0, endR, bi);
                DrawBidirectionalH(g, upBrush, downBrush, divPen, trkNetBrush, new Rectangle(left, netY, w, BarW), upP, downP, endR, bi);
            }
            else
            {
                // ===== 右/左缘：竖条三列（右缘 左→右 = CPU/内存/网络，网络贴屏幕缘；左缘镜像，CPU 仍在桌面侧）=====
                int barTop = EndPad;
                int barH = formH - EndPad * 2;
                if (barH < 20) { g.SmoothingMode = oldSmoothing; g.PixelOffsetMode = oldOffset; return; }

                int cpuX, memX, netX;
                if (dock == MainFormBizHelper.DockEdge.Right)
                {
                    cpuX = margin;                      // 桌面侧在左：CPU 在前
                    memX = cpuX + BarW + Gap;
                    netX = memX + BarW + Gap;           // 网络贴屏幕缘
                }
                else
                {
                    cpuX = formW - margin - BarW;       // 左缘镜像：桌面侧在右，CPU 仍在桌面侧
                    memX = cpuX - Gap - BarW;
                    netX = memX - Gap - BarW;           // 网络贴屏幕缘（左）
                }

                DrawBarV(g, trkCpuBrush, cpuBrush, new Rectangle(cpuX, barTop, BarW, barH), cpu / 100.0, endR, bi);
                DrawBarV(g, trkMemBrush, memBrush, new Rectangle(memX, barTop, BarW, barH), mem / 100.0, endR, bi);
                DrawBidirectionalV(g, upBrush, downBrush, divPen, trkNetBrush, new Rectangle(netX, barTop, BarW, barH), upP, downP, endR, bi);
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
