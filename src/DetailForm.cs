// 详情窗口：无边框圆角卡片面板（右下角弹出、置顶、可常驻，每次刷新后同步更新）
// 原为 TrayApp 的嵌套类，分层重构提升为 internal 顶层类（含嵌套的 CardPanel/ProgressBarControl/TrendChartControl）
#pragma warning disable 4014 // 有意 fire-and-forget 的 async 调用

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimiQuotaTray
{
    internal sealed class DetailForm : Form
    {
        // Design Tokens（设计书——UI）
        private static readonly Color BgColor = Color.FromArgb(0xF3, 0xF4, 0xF6);
        private static readonly Color BorderColor = Color.FromArgb(0xE5, 0xE7, 0xEB);
        private static readonly Color TitleColor = Color.FromArgb(0x6B, 0x72, 0x80);
        private static readonly Color BigColor = Color.FromArgb(0x11, 0x18, 0x27);
        private static readonly Color FooterColor = Color.FromArgb(0x9C, 0xA3, 0xAF);
        private static readonly Color CloseHoverColor = Color.FromArgb(0xEF, 0x44, 0x44);

        private const int DesignWidth = 600;   // 逻辑像素（默认宽，用户拖过边缘后以 settings.detailWidth 为准）
        private const int DesignHeight = 640;  // 逻辑像素（默认高，仅在无 detailHeight 时用；内容超出会再长高）
        private const int MinLogicalWidth = 320;
        private const int MaxLogicalWidth = 1000;
        private const int MinLogicalHeight = 240;
        private const int MaxLogicalHeight = 1600;
        private const int TitleBarHeight = 36; // 逻辑像素
        private const int ResizeGrip = 6;      // 逻辑像素，边缘缩放热区
        private const int ClassStyleDropShadow = 0x00020000; // CS_DROPSHADOW
        private const int WmNcHitTest = 0x84;
        private const int WmGetMinMaxInfo = 0x24;
        private const int WmNcLButtonDblClk = 0xA3;
        private const int WmDpiChanged = 0x02E0;
        private const int HtCaption = 0x2;
        private const int HtClient = 0x1;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;

        private readonly TrayApp _app;
        private readonly Font _fontTitle;   // 9pt
        private readonly Font _fontBig;     // 18pt Bold
        private readonly Font _fontAux;     // 8.25pt
        private readonly Font _fontFooter;  // 8pt
        private readonly Label _closeLabel;
        private readonly List<Control> _content = new List<Control>();
        private UsagesResponse _data;
        private int _dpi = 96; // 当前显示器真实 DPI（GetDpiForWindow），勿用 DeviceDpi（恒为 96）

        public DetailForm(TrayApp app)
        {
            _app = app;
            Text = "Kimi 额度详情";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = BgColor;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None; // 布局全部手动走 Scale()，避免引擎二次缩放
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);

            _fontTitle = new Font("Segoe UI", 9F);
            _fontBig = new Font("Segoe UI", 18F, FontStyle.Bold);
            _fontAux = new Font("Segoe UI", 8.25F);
            _fontFooter = new Font("Segoe UI", 8F);

            var dummy = Handle; // 强制建句柄，GetDpiForWindow 才能拿到所在显示器的真实 DPI
            _dpi = TrayApp.GetDpiForWindow(Handle);
            if (_dpi < 96) _dpi = 96;
            Width = Scale(LogicalWidth());
            Height = Scale(LogicalHeight());
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Scale(16), wa.Bottom - Height - Scale(16));

            _closeLabel = new Label();
            _closeLabel.Text = "✕";
            _closeLabel.Font = _fontTitle;
            _closeLabel.ForeColor = FooterColor;
            _closeLabel.BackColor = Color.Transparent;
            _closeLabel.TextAlign = ContentAlignment.MiddleCenter;
            _closeLabel.Cursor = Cursors.Hand;
            _closeLabel.Click += delegate { Close(); };
            _closeLabel.MouseEnter += delegate { _closeLabel.ForeColor = CloseHoverColor; };
            _closeLabel.MouseLeave += delegate { _closeLabel.ForeColor = FooterColor; };
            Controls.Add(_closeLabel);

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
            };
            LayoutChrome();
            ApplyRoundRegion();
        }

        // 固定像素统一过 Scale：逻辑像素 → 当前显示器物理像素
        private int Scale(int px)
        {
            return (int)Math.Round(px * _dpi / 96.0);
        }

        // 用户拖过边缘后记住的逻辑宽（settings.detailWidth），缺失则用默认并夹取范围
        private int LogicalWidth()
        {
            int w = _app._settings.DetailWidth.GetValueOrDefault(DesignWidth);
            if (w < MinLogicalWidth) w = MinLogicalWidth;
            if (w > MaxLogicalWidth) w = MaxLogicalWidth;
            return w;
        }

        // 用户拖过边缘后记住的逻辑高（settings.detailHeight），缺失则用默认
        private int LogicalHeight()
        {
            int h = _app._settings.DetailHeight.GetValueOrDefault(DesignHeight);
            if (h < MinLogicalHeight) h = MinLogicalHeight;
            if (h > MaxLogicalHeight) h = MaxLogicalHeight;
            return h;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= ClassStyleDropShadow; // 无边框窗体可用的最简单阴影方案
                return cp;
            }
        }

        private void ApplyRoundRegion()
        {
            using (var path = TrayApp.RoundedRect(new Rectangle(0, 0, Width, Height), Scale(12)))
            {
                var old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        private void LayoutChrome()
        {
            _closeLabel.Bounds = new Rectangle(
                Width - Scale(32), 0, Scale(32), Scale(TitleBarHeight));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (_closeLabel != null)
            {
                LayoutChrome();
                ApplyRoundRegion();
            }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _dpi = e.DeviceDpiNew;
            ApplyDpiChange();
        }

        // DPI 变化：右缘锚定重算宽高并重排内容（DPI 变大宽度增长时向左生长，不推出屏幕右缘）
        private void ApplyDpiChange()
        {
            int right = Right;
            Width = Scale(LogicalWidth());
            Left = right - Width;
            Height = Scale(LogicalHeight());
            RebuildContent();
        }

        // 拖动结束后：内容按新宽度重排，记住宽高（高度不再回弹，用户拖多高就多高）
        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            int logicalW = (int)Math.Round(Width * 96.0 / _dpi);
            if (logicalW < MinLogicalWidth) logicalW = MinLogicalWidth;
            if (logicalW > MaxLogicalWidth) logicalW = MaxLogicalWidth;
            int logicalH = (int)Math.Round(Height * 96.0 / _dpi);
            if (logicalH < MinLogicalHeight) logicalH = MinLogicalHeight;
            if (logicalH > MaxLogicalHeight) logicalH = MaxLogicalHeight;
            _app._settings.DetailWidth = logicalW;
            _app._settings.DetailHeight = logicalH;
            _app.SaveSettings();
            RebuildContent();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public Point Reserved;
            public Point MaxSize;
            public Point MaxPosition;
            public Point MinTrackSize;
            public Point MaxTrackSize;
        }

        // 标题栏区域（✕ 除外）拖动整个窗口；左/右/下边缘 6px 为缩放热区
        protected override void WndProc(ref Message m)
        {
            // 双击标题栏：恢复默认宽高，解决拉大后难以复原的问题
            // 必须在 base.WndProc 之前拦截，否则默认处理会把双击当成“最大化”
            if (m.Msg == WmNcLButtonDblClk && m.WParam.ToInt32() == HtCaption)
            {
                _app._settings.DetailWidth = DesignWidth;
                _app._settings.DetailHeight = DesignHeight;
                _app.SaveSettings();
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                Width = Scale(DesignWidth);
                Height = Scale(DesignHeight);
                RebuildContent();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
            // OnDpiChanged 在本运行时下不触发（DeviceDpi 恒为 96 的同一原因），直接处理 WM_DPICHANGED
            if (m.Msg == WmDpiChanged)
            {
                int newDpi = (int)((long)m.WParam & 0xFFFF);
                if (newDpi >= 96 && newDpi != _dpi)
                {
                    _dpi = newDpi;
                    ApplyDpiChange();
                }
                return;
            }
            if (m.Msg == WmGetMinMaxInfo)
            {
                var mmi = (MinMaxInfo)Marshal.PtrToStructure(m.LParam, typeof(MinMaxInfo));
                mmi.MinTrackSize = new Point(Scale(MinLogicalWidth), Scale(240));
                mmi.MaxTrackSize = new Point(Scale(MaxLogicalWidth), Scale(MaxLogicalHeight));
                Marshal.StructureToPtr(mmi, m.LParam, true);
                return;
            }
            if (m.Msg == WmNcHitTest && (int)m.Result == HtClient)
            {
                int lp = m.LParam.ToInt32();
                var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)(lp >> 16)));
                int grip = Scale(ResizeGrip);
                bool onLeft = pt.X < grip;
                bool onRight = pt.X >= Width - grip;
                bool onBottom = pt.Y >= Height - grip;
                if (onLeft && onBottom) { m.Result = (IntPtr)HtBottomLeft; return; }
                if (onRight && onBottom) { m.Result = (IntPtr)HtBottomRight; return; }
                if (onLeft) { m.Result = (IntPtr)HtLeft; return; }
                if (onRight) { m.Result = (IntPtr)HtRight; return; }
                if (onBottom) { m.Result = (IntPtr)HtBottom; return; }
                if (pt.Y < Scale(TitleBarHeight) && !_closeLabel.Bounds.Contains(pt))
                    m.Result = (IntPtr)HtCaption;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            // 自定义标题栏：标题文字 + 底部 1px 分隔线
            using (var brush = new SolidBrush(TitleColor))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                g.DrawString(Text, _fontTitle, brush,
                    new RectangleF(Scale(12), 0, Width - Scale(12 + 32), Scale(TitleBarHeight)), sf);
            using (var pen = new Pen(BorderColor))
                g.DrawLine(pen, 0, Scale(TitleBarHeight) - 1, Width, Scale(TitleBarHeight) - 1);
        }

        public void SetData(UsagesResponse u)
        {
            _data = u;
            RebuildContent();
        }

        // 按最新数据重排卡片；防闪烁：SuspendLayout + 双缓冲
        private void RebuildContent()
        {
            SuspendLayout();
            foreach (var c in _content)
            {
                Controls.Remove(c);
                c.Dispose();
            }
            _content.Clear();

            int pad = Scale(14);
            int gap = Scale(10);
            int cardW = Width - pad * 2;
            int y = Scale(TitleBarHeight) + gap;

            if (_data == null)
            {
                // 无数据降级：整块内容区居中灰字
                int h = Scale(160);
                var empty = MakeLabel("暂无数据，请等待刷新或检查网络",
                    new Rectangle(0, y, Width, h), _fontAux, TitleColor,
                    ContentAlignment.MiddleCenter, BgColor);
                AddContent(empty);
                y += h;
            }
            else
            {
                var w5 = TrayApp.FindWindow5hDetail(_data);
                if (w5 != null)
                {
                    y += AddQuotaCard(pad, y, cardW, "5小时窗口", w5, true, null) + gap;
                    y += AddTrendCard(pad, y, cardW, w5) + gap; // v1.3：5小时窗口趋势卡
                }
                if (_data.Usage != null)
                {
                    // v1.3：周额度估算行（历史禁用时不显示）
                    var weeklyEst = _app._settings.HistoryEnabled.GetValueOrDefault(true)
                        ? _app.EstimateWeekly(_data.Usage) : null;
                    y += AddQuotaCard(pad, y, cardW, "周额度", _data.Usage, true,
                        weeklyEst != null ? weeklyEst.Text : null) + gap;
                    y += AddWeeklyTrendCard(pad, y, cardW, _data.Usage) + gap; // v1.4：周额度趋势卡
                }
                if (_data.TotalQuota != null)
                    y += AddQuotaCard(pad, y, cardW, "月总额度", _data.TotalQuota, false, null) + gap;
                y += AddExtraCard(pad, y, cardW) + gap;

                if (_data.Parallel != null)
                {
                    long limit;
                    string text = "并行会话 " +
                        (_data.Parallel.Details != null ? _data.Parallel.Details.Count : 0) +
                        "/" + (TrayApp.TryParseLong(_data.Parallel.Limit, out limit) ? limit.ToString() : "?") +
                        " 进行中";
                    var line = MakeLabel(text, new Rectangle(pad, y, cardW, Scale(18)),
                        _fontFooter, TitleColor, ContentAlignment.MiddleLeft, BgColor);
                    AddContent(line);
                    y += Scale(18) + gap;
                }

                // 页脚：更新于 + 刷新按钮
                var footer = MakeLabel(
                    _app._lastSuccessAt != DateTime.MinValue
                        ? "更新于 " + _app._lastSuccessAt.ToString("HH:mm:ss")
                        : "尚未刷新",
                    new Rectangle(pad, y, cardW - Scale(24), Scale(22)),
                    _fontFooter, FooterColor, ContentAlignment.MiddleLeft, BgColor);
                AddContent(footer);
                var refresh = MakeLabel("↻",
                    new Rectangle(Width - pad - Scale(24), y, Scale(24), Scale(22)),
                    _fontTitle, FooterColor, ContentAlignment.MiddleCenter, BgColor);
                refresh.Cursor = Cursors.Hand;
                refresh.Click += delegate { _app.RefreshAsync(); };
                refresh.MouseEnter += delegate { refresh.ForeColor = TitleColor; };
                refresh.MouseLeave += delegate { refresh.ForeColor = FooterColor; };
                AddContent(refresh);
                y += Scale(22);
            }

            int contentH = y + pad;
            if (Height < contentH)
            {
                // 内容变多时装不下：保持底边锚定向上生长；否则尊重用户拖出的高度，不回弹
                int bottom = Bottom;
                Height = contentH;
                Top = bottom - Height;
            }
            ResumeLayout(true);
        }

        private void AddContent(Control c)
        {
            Controls.Add(c);
            _content.Add(c);
        }

        private static Label MakeLabel(string text, Rectangle bounds, Font font,
            Color foreColor, ContentAlignment align, Color backColor)
        {
            var l = new Label();
            l.Text = text;
            l.Bounds = bounds;
            l.Font = font;
            l.ForeColor = foreColor;
            l.BackColor = backColor;
            l.TextAlign = align;
            l.AutoEllipsis = true;
            return l;
        }

        // 额度卡片（5小时窗口 / 周额度 / 月总额度），返回卡片高度
        // estimateLine：v1.3 周额度估算行，显示在重置行下一行；null 不显示
        private int AddQuotaCard(int x, int y, int w, string title, QuotaDetail d, bool showReset,
            string estimateLine)
        {
            int pad = Scale(14);
            int innerW = w - pad * 2;
            int cy = pad;

            int? pct = TrayApp.Percent(d);

            var card = new CardPanel(Scale(8)) { Bounds = new Rectangle(x, y, w, 10) };
            card.Controls.Add(MakeLabel(title, new Rectangle(pad, cy, innerW, Scale(16)),
                _fontTitle, TitleColor, ContentAlignment.MiddleLeft, Color.Transparent));
            cy += Scale(16) + Scale(4);

            // 大数字（左） + 剩 n/limit（右下对齐）
            string bigText = pct.HasValue ? pct.Value + "%" : "?";
            string sideText = "剩 " + TrayApp.Str(d.Remaining) + "/" + TrayApp.Str(d.Limit);
            int bigH = Scale(34);
            card.Controls.Add(MakeLabel(bigText, new Rectangle(pad, cy, innerW / 2, bigH),
                _fontBig, BigColor, ContentAlignment.MiddleLeft, Color.Transparent));
            card.Controls.Add(MakeLabel(sideText,
                new Rectangle(pad + innerW / 2, cy, innerW / 2, bigH),
                _fontAux, TitleColor, ContentAlignment.BottomRight, Color.Transparent));
            cy += bigH + Scale(10);

            // 进度条：填充色 = ColorForPercent，与托盘图标同一套阈值颜色
            var bar = new ProgressBarControl(pct.GetValueOrDefault(),
                pct.HasValue ? _app.ColorForPercent(pct.Value) : TrayApp.ColorGray);
            bar.Bounds = new Rectangle(pad, cy, innerW, Scale(6));
            card.Controls.Add(bar);
            cy += Scale(6);

            if (showReset)
            {
                cy += Scale(8);
                card.Controls.Add(MakeLabel(TrayApp.FmtResetUi(d.ResetTime),
                    new Rectangle(pad, cy, innerW, Scale(16)),
                    _fontAux, TitleColor, ContentAlignment.MiddleLeft, Color.Transparent));
                cy += Scale(16);
            }

            if (estimateLine != null)
            {
                cy += Scale(4);
                card.Controls.Add(MakeLabel(estimateLine,
                    new Rectangle(pad, cy, innerW, Scale(16)),
                    _fontAux, FooterColor, ContentAlignment.MiddleLeft, Color.Transparent));
                cy += Scale(16);
            }

            int cardH = cy + pad;
            card.Height = cardH;
            AddContent(card);
            return cardH;
        }

        // 「5小时窗口趋势」卡片（v1.3）：折线图 + 底部估算文字，读内存历史缓存不刷盘
        private int AddTrendCard(int x, int y, int w, QuotaDetail w5)
        {
            int pad = Scale(14);
            int innerW = w - pad * 2;
            int cy = pad;

            var card = new CardPanel(Scale(8)) { Bounds = new Rectangle(x, y, w, 10) };
            card.Controls.Add(MakeLabel("5小时窗口趋势", new Rectangle(pad, cy, innerW, Scale(16)),
                _fontTitle, TitleColor, ContentAlignment.MiddleLeft, Color.Transparent));
            cy += Scale(16) + Scale(4);

            bool enabled = _app._settings.HistoryEnabled.GetValueOrDefault(true);
            var est = enabled ? _app.EstimateWindow5h(w5) : null;

            var chart = new TrendChartControl(_fontFooter);
            chart.ScalePx = Scale; // 固定尺寸统一过 Scale，与卡片其余部分一致
            chart.Bounds = new Rectangle(pad, cy, innerW, Scale(80));
            if (!enabled)
            {
                chart.Message = "历史记录已禁用";
            }
            else
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var pts = BurnEstimator.Downsample(_app.CollectPoints(now - 6 * 3600, true), 1000);
                long limit;
                if (!TrayApp.TryParseLong(w5.Limit, out limit) || limit <= 0)
                {
                    limit = 0; // 当前 limit 缺失时回退到历史记录里的最大值
                    foreach (var s in _app.GetHistory())
                        if (s.W5l.HasValue && s.W5l.Value > limit) limit = s.W5l.Value;
                }
                if (limit <= 0 || pts.Count < 2)
                {
                    chart.Message = "数据积累中，约 10 分钟后出趋势";
                }
                else
                {
                    chart.Points = pts;
                    chart.LimitValue = limit;
                    chart.StartUnix = now - 6 * 3600;
                    chart.EndUnix = now;
                    int? pct = TrayApp.Percent(w5); // 折线颜色与卡片/图标同一套阈值
                    chart.LineColor = pct.HasValue ? _app.ColorForPercent(pct.Value) : TrayApp.ColorGray;
                    // v1.4：高亮当前滚动窗口覆盖的时间区域 = resetTime − window.duration 到 now
                    // （时长取接口返回的 window.duration 分钟数，不写死 300；resetTime 缺失/解析失败则不画）
                    var w5Item = TrayApp.FindWindow5h(_data);
                    DateTimeOffset resetDto;
                    long durationMin;
                    if (w5Item != null && w5Item.Window != null &&
                        TrayApp.TryParseLong(w5Item.Window.Duration, out durationMin) &&
                        !string.IsNullOrEmpty(w5.ResetTime) &&
                        DateTimeOffset.TryParse(w5.ResetTime, out resetDto))
                        chart.HighlightFromUnix = resetDto.ToUnixTimeSeconds() - durationMin * 60;
                    // X 轴最近 6 小时；仅当 ETA 落在 6 小时内才画预测虚线，
                    // 且右端点延伸到 ETA（否则撞线点在未来，会画出绘图区右缘）
                    if (est != null && est.HasEta && est.EtaUnix > now && est.EtaUnix <= now + 6 * 3600)
                    {
                        chart.HasEta = true;
                        chart.EtaUnix = est.EtaUnix;
                        chart.SlopePerMinute = est.Slope;
                        chart.EndUnix = est.EtaUnix;
                    }
                }
            }
            card.Controls.Add(chart);
            cy += Scale(80) + Scale(6);

            card.Controls.Add(MakeLabel(enabled ? est.Text : "历史记录已禁用",
                new Rectangle(pad, cy, innerW, Scale(16)),
                _fontAux, FooterColor, ContentAlignment.MiddleLeft, Color.Transparent));
            cy += Scale(16);

            int cardH = cy + pad;
            card.Height = cardH;
            AddContent(card);
            return cardH;
        }

        // 「周额度趋势（近 7 天）」卡片（v1.4）：wku 序列，X 轴日期标签，不画 ETA 虚线/高亮区
        private int AddWeeklyTrendCard(int x, int y, int w, QuotaDetail usage)
        {
            int pad = Scale(14);
            int innerW = w - pad * 2;
            int cy = pad;

            var card = new CardPanel(Scale(8)) { Bounds = new Rectangle(x, y, w, 10) };
            card.Controls.Add(MakeLabel("周额度趋势（近 7 天）", new Rectangle(pad, cy, innerW, Scale(16)),
                _fontTitle, TitleColor, ContentAlignment.MiddleLeft, Color.Transparent));
            cy += Scale(16) + Scale(4);

            bool enabled = _app._settings.HistoryEnabled.GetValueOrDefault(true);

            var chart = new TrendChartControl(_fontFooter);
            chart.ScalePx = Scale;
            chart.AxisTimeFormat = "MM-dd"; // 7 天跨度用日期标签
            chart.Bounds = new Rectangle(pad, cy, innerW, Scale(80));
            if (!enabled)
            {
                chart.Message = "历史记录已禁用";
            }
            else
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var pts = BurnEstimator.Downsample(_app.CollectPoints(now - 7 * 86400, false), 1000);
                long limit;
                if (!TrayApp.TryParseLong(usage.Limit, out limit) || limit <= 0)
                {
                    limit = 0; // 当前 limit 缺失时回退到历史记录里的最大值（同 5h 图）
                    foreach (var s in _app.GetHistory())
                        if (s.Wkl.HasValue && s.Wkl.Value > limit) limit = s.Wkl.Value;
                }
                if (limit <= 0 || pts.Count < 2)
                {
                    chart.Message = "数据积累中，约 10 分钟后出趋势";
                }
                else
                {
                    chart.Points = pts;
                    chart.LimitValue = limit;
                    chart.StartUnix = now - 7 * 86400;
                    chart.EndUnix = now;
                    int? pct = TrayApp.Percent(usage); // 折线颜色与卡片/图标同一套阈值
                    chart.LineColor = pct.HasValue ? _app.ColorForPercent(pct.Value) : TrayApp.ColorGray;
                }
            }
            card.Controls.Add(chart);
            cy += Scale(80);

            int cardH = cy + pad;
            card.Height = cardH;
            AddContent(card);
            return cardH;
        }

        // Extra 余额卡片：大数字 = 余额；进度条按 1-已用/上限 着色；未开通/无数据只显示文字
        private int AddExtraCard(int x, int y, int w)
        {
            int pad = Scale(14);
            int innerW = w - pad * 2;
            int cy = pad;

            var card = new CardPanel(Scale(8)) { Bounds = new Rectangle(x, y, w, 10) };
            card.Controls.Add(MakeLabel("Extra 余额", new Rectangle(pad, cy, innerW, Scale(16)),
                _fontTitle, TitleColor, ContentAlignment.MiddleLeft, Color.Transparent));
            cy += Scale(16) + Scale(4);

            var wallet = _data.BoosterWallet;
            long? left = TrayApp.ExtraBalanceRaw(wallet);
            if (wallet == null || !left.HasValue)
            {
                card.Controls.Add(MakeLabel(wallet == null ? "未开通" : "无数据",
                    new Rectangle(pad, cy, innerW, Scale(34)),
                    _fontBig, BigColor, ContentAlignment.MiddleLeft, Color.Transparent));
                cy += Scale(34);
            }
            else
            {
                long cents = (left.Value + 500000) / 1000000;
                card.Controls.Add(MakeLabel(TrayApp.FmtYuanFromCents(cents),
                    new Rectangle(pad, cy, innerW, Scale(34)),
                    _fontBig, BigColor, ContentAlignment.MiddleLeft, Color.Transparent));
                cy += Scale(34) + Scale(6);

                long limitCents = 0, usedCents = 0;
                bool hasLimit = wallet.MonthlyChargeLimitEnabled &&
                    wallet.MonthlyChargeLimit != null && wallet.MonthlyUsed != null &&
                    TrayApp.TryParseLong(wallet.MonthlyChargeLimit.PriceInCents, out limitCents) &&
                    TrayApp.TryParseLong(wallet.MonthlyUsed.PriceInCents, out usedCents) &&
                    limitCents > 0;
                if (hasLimit)
                {
                    card.Controls.Add(MakeLabel(
                        "本月已用 " + TrayApp.FmtYuanFromCents(usedCents) + " / 上限 " + TrayApp.FmtYuanFromCents(limitCents),
                        new Rectangle(pad, cy, innerW, Scale(16)),
                        _fontAux, TitleColor, ContentAlignment.MiddleLeft, Color.Transparent));
                    cy += Scale(16) + Scale(8);

                    int pctLeft = (int)Math.Round((limitCents - usedCents) * 100.0 / limitCents);
                    if (pctLeft < 0) pctLeft = 0;
                    if (pctLeft > 100) pctLeft = 100;
                    var bar = new ProgressBarControl(pctLeft, _app.ColorForPercent(pctLeft));
                    bar.Bounds = new Rectangle(pad, cy, innerW, Scale(6));
                    card.Controls.Add(bar);
                    cy += Scale(6);
                }

                // v1.4：今日/本周 Extra 消费（历史禁用或无 ex 样本时不显示该行）
                if (_app._settings.HistoryEnabled.GetValueOrDefault(true))
                {
                    long todayCents, weekCents;
                    if (_app.TryGetExtraSpend(out todayCents, out weekCents))
                    {
                        cy += Scale(8);
                        card.Controls.Add(MakeLabel(
                            "今日消费 " + TrayApp.FmtYuanFromCents(todayCents) +
                            " · 本周消费 " + TrayApp.FmtYuanFromCents(weekCents),
                            new Rectangle(pad, cy, innerW, Scale(16)),
                            _fontAux, TitleColor, ContentAlignment.MiddleLeft, Color.Transparent));
                        cy += Scale(16);
                    }
                }
            }

            int cardH = cy + pad;
            card.Height = cardH;
            AddContent(card);
            return cardH;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // _content 中的控件都还在 Controls 里，由 base.Dispose 级联释放
                _content.Clear();
                _fontTitle.Dispose();
                _fontBig.Dispose();
                _fontAux.Dispose();
                _fontFooter.Dispose();
            }
            base.Dispose(disposing);
        }

        // 白底圆角卡片：自绘背景 + 1px 描边
        internal sealed class CardPanel : Panel
        {
            private readonly int _radius;

            public CardPanel(int radius)
            {
                _radius = radius;
                SetStyle(ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw, true);
                BackColor = Color.White;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = TrayApp.RoundedRect(rect, _radius))
                {
                    using (var brush = new SolidBrush(Color.White))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(BorderColor))
                        g.DrawPath(pen, path);
                }
            }
        }

        // 进度条：轨道 #E5E7EB，填充色由调用方按阈值给定；每次刷新随卡片重建
        internal sealed class ProgressBarControl : Control
        {
            private readonly int _percent;
            private readonly Color _fillColor;

            public ProgressBarControl(int percent, Color fillColor)
            {
                _percent = percent;
                _fillColor = fillColor;
                SetStyle(ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = ClientRectangle;
                if (r.Width < 1 || r.Height < 1) return;
                using (var track = TrayApp.RoundedRect(new Rectangle(0, 0, r.Width - 1, r.Height - 1), r.Height / 2))
                using (var trackBrush = new SolidBrush(BorderColor))
                    g.FillPath(trackBrush, track);
                int w = (int)Math.Round((r.Width - 1) * _percent / 100.0);
                if (w > 0)
                {
                    if (w > r.Width - 1) w = r.Width - 1;
                    using (var fill = TrayApp.RoundedRect(new Rectangle(0, 0, w, r.Height - 1), r.Height / 2))
                    using (var fillBrush = new SolidBrush(_fillColor))
                        g.FillPath(fillBrush, fill);
                }
            }
        }
        // 趋势折线图（v1.3 起 5 小时窗口用，v1.4 起周额度复用）：Y 轴 0~limit
        internal sealed class TrendChartControl : Control
        {
            public List<BurnEstimator.SamplePoint> Points;
            public double LimitValue;
            public Color LineColor = TrayApp.ColorGray;
            public long StartUnix;
            public long EndUnix;
            public bool HasEta;
            public long EtaUnix;
            public double SlopePerMinute;
            public string Message; // 非空 → 居中灰字，不画图
            public Func<int, int> ScalePx; // 逻辑像素 → 物理像素（DetailForm.Scale），空则恒等
            public string AxisTimeFormat = "HH:mm"; // X 轴两端时间标签格式（5h 图 HH:mm，7 天图 MM-dd）
            public long? HighlightFromUnix; // v1.4：当前窗口高亮区起点（终点 = now），null 不画（周趋势卡不设置）
            private readonly Font _font;

            public TrendChartControl(Font font)
            {
                _font = font;
                SetStyle(ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                var r = ClientRectangle;
                if (r.Width < 10 || r.Height < 10) return;

                if (Message != null)
                {
                    using (var brush = new SolidBrush(FooterColor))
                    using (var sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(Message, _font, brush, r, sf);
                    }
                    return;
                }
                if (Points == null || Points.Count < 2 || LimitValue <= 0 || EndUnix <= StartUnix)
                    return;

                int textH = (int)Math.Ceiling(_font.GetHeight(g)) + 2;
                int padX = ScalePx != null ? ScalePx(8) : 8;
                // 绘图区：顶部留给 limit 标签，底部留给 0 与时间标签
                var plot = new Rectangle(padX, textH, r.Width - padX * 2, r.Height - textH * 2 - 4);
                if (plot.Width < 10 || plot.Height < 10) return;

                Func<long, float> mapX = delegate(long t)
                {
                    return plot.Left + (float)((t - StartUnix) * (double)plot.Width / (EndUnix - StartUnix));
                };
                Func<double, float> mapY = delegate(double u)
                {
                    return plot.Bottom - (float)(u / LimitValue * plot.Height);
                };

                // v1.4：当前窗口高亮区（resetTime − duration 到 now），背景层级，先画再画虚线/折线
                if (HighlightFromUnix.HasValue)
                {
                    long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    float hx0 = mapX(HighlightFromUnix.Value);
                    float hx1 = mapX(nowSec);
                    if (hx0 < plot.Left) hx0 = plot.Left; // 起点早于图表左缘时裁剪
                    if (hx1 > plot.Right) hx1 = plot.Right;
                    if (hx1 > hx0)
                    {
                        using (var brush = new SolidBrush(Color.FromArgb(40, 0x22, 0xC5, 0x5E)))
                            g.FillRectangle(brush, hx0, plot.Top, hx1 - hx0, plot.Height);
                    }
                }

                // 上限虚线：顶部红色 #EF4444
                using (var pen = new Pen(Color.FromArgb(0xEF, 0x44, 0x44)))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawLine(pen, plot.Left, plot.Top, plot.Right, plot.Top);
                }

                // 用量折线：2px，颜色 = ColorForPercent(最新百分比)
                using (var pen = new Pen(LineColor, 2f))
                {
                    var pts = new PointF[Points.Count];
                    for (int i = 0; i < Points.Count; i++)
                        pts[i] = new PointF(mapX(Points[i].T), mapY(Points[i].U));
                    g.DrawLines(pen, pts);
                }

                // 预测虚线：ETA 落在图内时，从最新点按斜率延伸到与上限线相交，交点画小圆点
                if (HasEta && SlopePerMinute > 0)
                {
                    var last = Points[Points.Count - 1];
                    double tHit = last.T + (LimitValue - last.U) / SlopePerMinute * 60.0;
                    if (tHit > last.T && tHit <= EndUnix)
                    {
                        float x1 = mapX((long)tHit);
                        float y1 = mapY(LimitValue);
                        using (var pen = new Pen(LineColor, 1.5f))
                        {
                            pen.DashStyle = DashStyle.Dash;
                            g.DrawLine(pen, mapX(last.T), mapY(last.U), x1, y1);
                        }
                        int dot = ScalePx != null ? ScalePx(6) : 6;
                        using (var brush = new SolidBrush(LineColor))
                            g.FillEllipse(brush, x1 - dot / 2f, y1 - dot / 2f, dot, dot);
                    }
                }

                // 轴标签：左上 limit 值、左下 0、底部左右端点时间（格式由 AxisTimeFormat 给定）
                using (var brush = new SolidBrush(FooterColor))
                {
                    g.DrawString(((long)LimitValue).ToString(), _font, brush, plot.Left, 0);
                    g.DrawString("0", _font, brush, plot.Left, plot.Bottom - textH);
                    string t0 = DateTimeOffset.FromUnixTimeSeconds(StartUnix)
                        .LocalDateTime.ToString(AxisTimeFormat);
                    string t1 = DateTimeOffset.FromUnixTimeSeconds(EndUnix)
                        .LocalDateTime.ToString(AxisTimeFormat);
                    g.DrawString(t0, _font, brush, plot.Left, plot.Bottom + 2);
                    var sz = g.MeasureString(t1, _font);
                    g.DrawString(t1, _font, brush, plot.Right - sz.Width, plot.Bottom + 2);
                }
            }
        }
    }
}
