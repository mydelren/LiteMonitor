﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using LiteMonitor.src.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace LiteMonitor
{
    /// <summary>
    /// 单个组的布局信息（名称 + 块区域 + 子项）
    /// 必须包含这个类定义，UIRenderer 才能引用它
    /// </summary>
    public class GroupLayoutInfo
    {
        public string GroupName { get; set; }
        // ★★★ 新增：缓存组标签 (防止渲染循环重复申请字符串) ★★★
        public string Label { get; set; } = ""; 
        public Rectangle Bounds { get; set; }
        public List<MetricItem> Items { get; set; }

        public GroupLayoutInfo(string name, List<MetricItem> items)
        {
            GroupName = name;
            Items = items;
            Bounds = Rectangle.Empty;
        }
    }

    public class UILayout
    {
        private readonly Theme _t;

        public UILayout(Theme t) { _t = t; }

        /// <summary>
        /// 计算所有布局：将数学计算完全封装在此，Renderer 只有绘制逻辑
        /// </summary>
        public int Build(List<GroupLayoutInfo> groups)
        {
            // ★★★ [优化] 获取缩放系数，修正硬编码像素在 高DPI 下过小的问题 ★★★
            float s = _t.Layout.LayoutScale; 
            if (s <= 0) s = 1.0f;

            // 将写死的像素值进行缩放
            int innerPad = (int)(10 * s);      // 原本是 10
            int innerPadTotal = innerPad * 2;  // 原本是 20
            int barMinH = (int)(4 * s);        // 原本是 6
            int barBotGap = (int)(3 * s);      // 原本是 3

            // ★★★ [视觉补偿] x 减去 1px ★★★
            // 修复“左边距比右边宽”的视觉问题（平衡 GDI+ 文本左侧留白）
            int x = _t.Layout.Padding - 1; 

            int y = _t.Layout.Padding;
            int w = _t.Layout.Width - _t.Layout.Padding * 2;
            int rowH = _t.Layout.RowHeight;

            // 1. 主标题占位（主题可关：showTitle=false）
            string title = LanguageManager.T("Title");
            if (_t.Layout.ShowTitle && !string.IsNullOrEmpty(title) && title != "Title")
                y += rowH + _t.Layout.Padding;

            // 2. [移除] 旧版 Dashboard 布局逻辑 (已整合进分组循环)
            // ==========================================================================================
            // if (Dashboard == null) Dashboard = new DashboardInfo(); ...
            
            // 3. 遍历分组
            for (int idx = 0; idx < groups.Count; idx++)
            {
                var g = groups[idx];
                
                // --- 策略判断：是 Dashboard、双列模式还是普通模式？ ---
                bool isDashboard = g.GroupName.Equals("DASH", StringComparison.OrdinalIgnoreCase);
                
                bool isTwoColumnGroup = 
                    g.GroupName.Equals("NET", StringComparison.OrdinalIgnoreCase) || 
                    g.GroupName.Equals("DISK", StringComparison.OrdinalIgnoreCase) || 
                    g.GroupName.Equals("DATA", StringComparison.OrdinalIgnoreCase);

                int contentHeight;
                
                // ★★★ 动态调整分组内边距 ★★★
                int groupPadding = _t.Layout.GroupPadding;
                if (isDashboard)
                {
                    groupPadding = Math.Max(2, groupPadding / 2);
                }

                // 计算起始 Y
                int itemY = y + groupPadding;

                // [Fix] 1.2.4 兼容：双列模式下，起始位置下移半个 Gap，以获得更多呼吸感并保持上下对称
                if (isTwoColumnGroup) 
                {
                    itemY += _t.Layout.ItemGap / 2;
                }

                int startItemY = itemY; // 记录起始位置用于计算总高度

                // 2. 混合布局算法 (支持 TextOnly / TwoColumn / StandardBar 混排)
                // -------------------------------------------------------------
                List<MetricItem> buffer2Col = new List<MetricItem>();
                int listRowH = (int)(rowH * 0.8); // 纯文本行高

                // 辅助函数：清空双列缓冲区
                void FlushBuffer()
                {
                    if (buffer2Col.Count == 0) return;

                    // 双列行高按字体自适应：标签行 = 标签字高，数值行 = 数值字高（rowH 仅作下限）
                    // —— 旧实现按 rowH/2 平分会把大字号数值的下缘硬切掉（实测 12.5pt 需 22px、只给了 15px）
                    int labelH = _t.FontItem.Height + 1;
                    int valueH = _t.FontValue.Height + 2;
                    int twoLineH = Math.Max(rowH, labelH + valueH);
                    int colWidth = w / 2;

                    // 如果只有1个，也占一半还是全宽？为了对齐，占一半比较合理，或者左对齐
                    for (int i = 0; i < buffer2Col.Count; i++)
                    {
                        var it = buffer2Col[i];
                        it.Style = MetricRenderStyle.TwoColumn;

                        // i=0 -> 左列, i=1 -> 右列
                        int itemX = (i == 0) ? x : x + colWidth;

                        // 区域
                        it.Bounds = new Rectangle(itemX, itemY, colWidth, twoLineH);

                        // 标签/数值各按自己的字高
                        it.LabelRect = new Rectangle(itemX, itemY, colWidth, labelH);
                        it.ValueRect = new Rectangle(itemX, itemY + labelH, colWidth, twoLineH - labelH);
                    }

                    // 移动 Y 轴 (双列共用一行高度)
                    itemY += twoLineH + _t.Layout.ItemGap;
                    buffer2Col.Clear();
                }

                foreach (var it in g.Items)
                {
                    // 策略 A: 纯文本 (Dashboard 组) -> 独占一行
                    if (isDashboard)
                    {
                        FlushBuffer(); // 先把之前的双列排完

                        it.Style = MetricRenderStyle.TextOnly;
                        it.Bounds = new Rectangle(x, itemY, w, listRowH);
                        var inner = new Rectangle(x + innerPad, itemY, w - innerPadTotal, listRowH);
                        it.LabelRect = inner;
                        it.ValueRect = inner;
                        
                        itemY += listRowH; // 纯文本紧凑排列，不需要额外 Gap (因为自带留白)
                    }
                    // 策略 B: 双列模式 (NET/DISK/DATA 且不是纯文本) -> 加入缓冲区
                    else if (isTwoColumnGroup)
                    {
                        buffer2Col.Add(it);
                        if (buffer2Col.Count >= 2) FlushBuffer();
                    }
                    // 策略 C: 标准进度条 -> 独占一行
                    else
                    {
                        FlushBuffer();

                        it.Style = MetricRenderStyle.StandardBar;

                        // 行高自适应：文字区必须容得下更高的那个字体（标签与数值同行排布）
                        int textNeed = Math.Max(_t.FontItem.Height, _t.FontValue.Height) + 2;
                        int barH = Math.Max(barMinH, (int)(rowH * 0.23));
                        int itemH = Math.Max(rowH, textNeed + barH + barBotGap + 2);

                        it.Bounds = new Rectangle(x, itemY, w, itemH);
                        var inner = new Rectangle(x + innerPad, itemY, w - innerPadTotal, itemH);

                        int topH = Math.Max((int)(inner.Height * 0.60), textNeed);
                        var topRect = new Rectangle(inner.X, inner.Y, inner.Width, topH);
                        it.LabelRect = topRect;
                        it.ValueRect = topRect;

                        int barY = inner.Bottom - barH - barBotGap;
                        it.BarRect = new Rectangle(inner.X, barY, inner.Width, barH);

                        itemY += itemH + _t.Layout.ItemGap;
                    }
                }
                FlushBuffer(); // 收尾

                // 计算内容总高度
                contentHeight = itemY - startItemY;

                // [Fix] 移除最后一个条目多加的 ItemGap (仅针对非 Dashboard 组)
                // 恢复 1.2.4 版本的紧凑布局： Padding - Items... - Padding
                // 注意：双列模式(isTwoColumnGroup) 1.2.4 原本就保留了 Gap，这里也不移除
                if (!isDashboard && !isTwoColumnGroup && contentHeight > 0)
                {
                    contentHeight -= _t.Layout.ItemGap;
                }

                // 3. 结算组高度
                int groupHeight = groupPadding * 2 + contentHeight;
                g.Bounds = new Rectangle(x, y, w, groupHeight);

                // 4. 移动 Y 轴到下一组
                if (idx < groups.Count - 1)
                    y += groupHeight + _t.Layout.GroupSpacing + _t.Layout.GroupBottom;
                else
                    y += groupHeight; 
            }

            return y + _t.Layout.Padding; // 返回总高度
        }
    }
}