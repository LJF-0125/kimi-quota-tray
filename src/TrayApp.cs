// KimiQuotaTray —— Kimi Code 额度显示托盘工具（非官方第三方工具）
// 设计规格见 docs 目录《计划书.md》系列文档。
// .NET Framework 4.8 / C# 5（系统自带 csc.exe），src/ 多文件，无第三方依赖。
// User-Agent 固定为 kimi-quota-tray/1.3，严禁伪装成 Kimi Code CLI。
#pragma warning disable 4014 // 有意 fire-and-forget 的 async 调用

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KimiQuotaTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // .NET 4.8 默认可能不启用 TLS 1.2，api.kimi.com 需要
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }

    // ===================== 主程序（无窗体 ApplicationContext） =====================

    internal sealed partial class TrayApp : ApplicationContext
    {
        private const string ConsoleUrl = "https://www.kimi.com/code/console";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "KimiQuotaTray";
        // .NET Framework 的 NotifyIcon.Text 硬限制是 63 字符（≥64 抛 ArgumentOutOfRangeException），
        // 不是计划书按新版 Shell 估算的 127 —— 文案必须按 63 排版
        private const int MaxTooltipLength = 63;

        internal static readonly Color ColorGreen = Color.FromArgb(0x22, 0xC5, 0x5E);
        internal static readonly Color ColorYellow = Color.FromArgb(0xEA, 0xB3, 0x08);
        internal static readonly Color ColorRed = Color.FromArgb(0xEF, 0x44, 0x44);
        internal static readonly Color ColorGray = Color.FromArgb(0x9C, 0xA3, 0xAF);
        internal static readonly Color ColorBlue = Color.FromArgb(0x3B, 0x82, 0xF6); // Extra 余额图标用中性蓝

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        // 真实 DPI 来源：.NET Framework 无 app.config 时 Control.DeviceDpi 恒为 96（OnDpiChanged 也不触发），
        // 而 Windows 按 manifest 的 PerMonitorV2 用真实 DPI 渲染文字，两者不一致会把文字底部裁掉
        [DllImport("user32.dll")]
        internal static extern int GetDpiForWindow(IntPtr hwnd);

        private readonly string _settingsPath;
        private readonly string _credPath;
        private readonly string _historyPath;
        private readonly HttpClient _http;
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _timer;
        internal Settings _settings;
        private Icon _icon;
        private UsagesResponse _lastData;
        private string _lastGoodJson; // 用于避免重复写盘
        private int _failCount;
        private bool _refreshing;
        private bool _lastRefreshInvalid; // TryRefreshToken 失败原因：true=凭证失效，false=可重试错误
        private bool _credWriteWarned;    // 凭证写回失败的气泡警告只提示一次，写成功复位
        private string _alertWindowKey; // 同一窗口周期只提醒一次（用 resetTime 作 key）
        private UsagesResponse _refillBaseline; // 回满提醒：上一轮成功数据（仅内存，不持久化）
        private List<HistorySample> _history;   // history.jsonl 的内存缓存，写历史时同步更新，避免每次刷盘
        private bool _historyLoaded;
        private DateTime _lastHistoryCleanupUtc = DateTime.MinValue;

        private readonly List<ToolStripMenuItem> _iconSourceItems = new List<ToolStripMenuItem>();
        private readonly List<ToolStripMenuItem> _intervalItems = new List<ToolStripMenuItem>();
        private readonly List<ToolStripMenuItem> _alertItems = new List<ToolStripMenuItem>();
        private ToolStripMenuItem _autoStartItem;
        private ToolStripMenuItem _refill5hItem;
        private ToolStripMenuItem _refillWeeklyItem;

        public TrayApp()
        {
            _settingsPath = Path.Combine(
                Path.GetDirectoryName(Application.ExecutablePath), "settings.json");
            _credPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kimi-code", "credentials", "kimi-code.json");

            _historyPath = Path.Combine(
                Path.GetDirectoryName(Application.ExecutablePath), "history.jsonl");

            _settings = LoadSettings(_settingsPath);
            CleanupHistoryIfDue(); // 启动时清理过期历史（内部按需加载内存缓存）
            _lastGoodJson = _settings.LastGoodData != null ? Serialize(_settings.LastGoodData) : null;

            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(10);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            _tray = new NotifyIcon();
            _tray.Text = ""; // 正常状态悬停不显示 tooltip；错误时由 SetError 设置原因
            _tray.ContextMenuStrip = BuildMenu(); // 右键 = 菜单（原始行为）
            _tray.MouseUp += OnTrayMouseUp;       // 左键 = 详情窗口
            // 不订阅 MouseDoubleClick：双击的第一击会触发左键 MouseUp 弹出详情窗口，
            // 与「双击 = 立即刷新」冲突；刷新入口已由右键菜单提供，双击手势移除
            SetIcon("--", ColorGray);
            _tray.Visible = true;

            // 冷启动：先用 lastGoodData 渲染一次，避免空窗，再立即刷新
            if (_settings.LastGoodData != null)
                RenderData(_settings.LastGoodData);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = CurrentIntervalMs();
            _timer.Tick += OnTimerTick;
            _timer.Start();

            RefreshAsync();
        }

        // ===================== 菜单（规格书 3.3） =====================

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += delegate { RefreshAsync(); };
            menu.Items.Add(refresh);

            var detail = new ToolStripMenuItem("额度详情");
            detail.Click += delegate { ShowDetailWindow(); };
            menu.Items.Add(detail);

            var copy = new ToolStripMenuItem("复制额度摘要");
            copy.Click += delegate { CopySummary(); };
            menu.Items.Add(copy);

            var iconMenu = new ToolStripMenuItem("图标显示");
            AddRadio(iconMenu, _iconSourceItems, "5小时窗口", "window5h",
                _settings.IconSource == "window5h", OnIconSourceClick);
            AddRadio(iconMenu, _iconSourceItems, "周额度", "weekly",
                _settings.IconSource == "weekly", OnIconSourceClick);
            AddRadio(iconMenu, _iconSourceItems, "Extra余额", "extraBalance",
                _settings.IconSource == "extraBalance", OnIconSourceClick);
            menu.Items.Add(iconMenu);

            var intervalMenu = new ToolStripMenuItem("刷新间隔");
            AddRadio(intervalMenu, _intervalItems, "1分钟", 1,
                _settings.RefreshIntervalMinutes == 1, OnIntervalClick);
            AddRadio(intervalMenu, _intervalItems, "3分钟", 3,
                _settings.RefreshIntervalMinutes == 3, OnIntervalClick);
            AddRadio(intervalMenu, _intervalItems, "5分钟", 5,
                _settings.RefreshIntervalMinutes == 5, OnIntervalClick);
            AddRadio(intervalMenu, _intervalItems, "10分钟", 10,
                _settings.RefreshIntervalMinutes == 10, OnIntervalClick);
            menu.Items.Add(intervalMenu);

            var alertMenu = new ToolStripMenuItem("低额度提醒");
            AddRadio(alertMenu, _alertItems, "关", 0,
                _settings.LowQuotaAlertThreshold == 0, OnAlertClick);
            AddRadio(alertMenu, _alertItems, "低于20%提醒", 20,
                _settings.LowQuotaAlertThreshold == 20, OnAlertClick);
            AddRadio(alertMenu, _alertItems, "低于10%提醒", 10,
                _settings.LowQuotaAlertThreshold == 10, OnAlertClick);
            menu.Items.Add(alertMenu);

            var refillMenu = new ToolStripMenuItem("回满提醒");
            _refill5hItem = new ToolStripMenuItem("5小时窗口回满");
            _refill5hItem.CheckOnClick = true;
            _refill5hItem.Checked = _settings.RefillAlert5h.GetValueOrDefault(true);
            _refill5hItem.Click += OnRefillClick;
            refillMenu.DropDownItems.Add(_refill5hItem);
            _refillWeeklyItem = new ToolStripMenuItem("周额度回满");
            _refillWeeklyItem.CheckOnClick = true;
            _refillWeeklyItem.Checked = _settings.RefillAlertWeekly.GetValueOrDefault(true);
            _refillWeeklyItem.Click += OnRefillClick;
            refillMenu.DropDownItems.Add(_refillWeeklyItem);
            menu.Items.Add(refillMenu);

            menu.Items.Add(new ToolStripSeparator());

            _autoStartItem = new ToolStripMenuItem("开机自启");
            _autoStartItem.CheckOnClick = true;
            _autoStartItem.Checked = ReadAutoStart(); // 启动时读注册表同步勾选
            _settings.AutoStart = _autoStartItem.Checked;
            _autoStartItem.Click += OnAutoStartClick;
            menu.Items.Add(_autoStartItem);

            var console = new ToolStripMenuItem("打开 Kimi Code 控制台");
            console.Click += delegate
            {
                try { System.Diagnostics.Process.Start(ConsoleUrl); } catch { }
            };
            menu.Items.Add(console);

            var about = new ToolStripMenuItem("额度说明：数据来自 Kimi Code 内部额度接口（非官方工具）");
            about.Enabled = false;
            menu.Items.Add(about);

            menu.Items.Add(new ToolStripSeparator());

            var exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(exit);

            return menu;
        }

        private static void AddRadio(ToolStripMenuItem parent, List<ToolStripMenuItem> group,
            string text, object tag, bool isChecked, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text);
            item.Tag = tag;
            item.Checked = isChecked;
            item.Click += onClick;
            parent.DropDownItems.Add(item);
            group.Add(item);
        }

        private static void CheckOnly(List<ToolStripMenuItem> group, ToolStripMenuItem selected)
        {
            foreach (var item in group)
                item.Checked = ReferenceEquals(item, selected);
        }

        private void OnIconSourceClick(object sender, EventArgs e)
        {
            var item = (ToolStripMenuItem)sender;
            _settings.IconSource = (string)item.Tag;
            CheckOnly(_iconSourceItems, item);
            SaveSettings();
            if (_lastData != null) RenderData(_lastData);
        }

        private void OnIntervalClick(object sender, EventArgs e)
        {
            var item = (ToolStripMenuItem)sender;
            _settings.RefreshIntervalMinutes = (int)item.Tag;
            CheckOnly(_intervalItems, item);
            _timer.Interval = CurrentIntervalMs();
            SaveSettings();
        }

        private void OnAlertClick(object sender, EventArgs e)
        {
            var item = (ToolStripMenuItem)sender;
            _settings.LowQuotaAlertThreshold = (int)item.Tag;
            _alertWindowKey = null;
            CheckOnly(_alertItems, item);
            SaveSettings();
            if (_lastData != null) CheckAlert(_lastData);
        }

        private void OnAutoStartClick(object sender, EventArgs e)
        {
            ApplyAutoStart(_autoStartItem.Checked);
            _settings.AutoStart = _autoStartItem.Checked;
            SaveSettings();
        }

        private void OnRefillClick(object sender, EventArgs e)
        {
            _settings.RefillAlert5h = _refill5hItem.Checked;
            _settings.RefillAlertWeekly = _refillWeeklyItem.Checked;
            SaveSettings();
        }

        // 复制额度摘要：剪贴板偶发被占用，短重试 3 次后气泡提示结果
        private void CopySummary()
        {
            string text = _lastData == null ? "暂无数据" : BuildDetailText(_lastData);
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    _tray.ShowBalloonTip(1000, "Kimi 额度", "额度摘要已复制", ToolTipIcon.Info);
                    return;
                }
                catch
                {
                    if (i < 2) System.Threading.Thread.Sleep(100);
                }
            }
            _tray.ShowBalloonTip(3000, "Kimi 额度", "复制失败，剪贴板被占用", ToolTipIcon.Warning);
        }

        private void OnTrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                ShowDetailWindow(); // 左键 = 直接弹详情窗口
        }

        // ===================== 定时刷新（规格书 4.2） =====================

        private int CurrentIntervalMs()
        {
            int minutes = _settings.RefreshIntervalMinutes;
            if (minutes < 1) minutes = 1;
            int ms = minutes * 60 * 1000;
            if (_failCount >= 3) ms *= 5; // 连续失败 ≥3 次，间隔 ×5
            return ms;
        }

        private async void OnTimerTick(object sender, EventArgs e)
        {
            _timer.Stop(); // 防重入：刷新期间停表，完成后再按当前间隔恢复
            try
            {
                await RefreshAsync();
            }
            finally
            {
                _timer.Interval = CurrentIntervalMs();
                _timer.Start();
            }
        }

        internal async Task RefreshAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                var cred = ReadCredentials();
                if (cred == null)
                {
                    SetError("未找到凭证文件，请先在 Kimi Code 中登录");
                    return;
                }

                if (TokenExpired(cred))
                {
                    // 重读一次：CLI 可能刚刷新过
                    var cred2 = ReadCredentials();
                    if (cred2 != null) cred = cred2;
                }
                if (TokenExpired(cred))
                {
                    cred = await TryRefreshToken(cred);
                    if (cred == null)
                    {
                        ReportRefreshFailure();
                        return;
                    }
                }

                UsagesResponse data = null;
                QuotaHttpException httpEx = null;
                try
                {
                    data = await GetUsages(cred.AccessToken);
                }
                catch (QuotaHttpException ex)
                {
                    httpEx = ex; // C# 5 不允许在 catch 子句中 await，捕获后到外面处理
                }
                if (httpEx != null)
                {
                    if (httpEx.StatusCode != 401) throw httpEx;
                    // 本地判断未过期但服务端 401（时钟偏差 / 提前吊销 / expires_at 不可信）：
                    // 强制刷新一次并重试；401 不计入 _failCount 退避计数
                    cred = await TryRefreshToken(cred);
                    if (cred == null)
                    {
                        ReportRefreshFailure();
                        return;
                    }
                    httpEx = null;
                    try
                    {
                        data = await GetUsages(cred.AccessToken);
                    }
                    catch (QuotaHttpException ex2)
                    {
                        httpEx = ex2;
                    }
                    if (httpEx != null)
                    {
                        if (httpEx.StatusCode != 401) throw httpEx;
                        SetError("凭证失效，请在 Kimi Code 中 /login"); // 刷新后仍 401，确认凭证失效
                        return;
                    }
                }

                _failCount = 0; // 成功：恢复原间隔
                CheckRefillAlerts(data);
                AppendHistory(data);   // 仅成功刷新后写历史；失败不写；先于渲染，图表/估算不滞后一个采样点
                RenderData(data);
                CacheLastGood(data);
                CleanupHistoryIfDue(); // 之后每 24 小时清理一次过期记录
            }
            catch (QuotaHttpException ex)
            {
                // 401 已在上方强制刷新重试过，能到这里的一定是非 401
                _failCount++;
                if (ex.StatusCode == 429)
                    SetError("接口限流 (429)，已自动退避");
                else
                    SetError("接口错误 (HTTP " + ex.StatusCode + ")");
            }
            catch (Exception)
            {
                // 网络错误 / 解析失败等：灰图标降级，不崩溃；消息不含任何敏感信息
                _failCount++;
                SetError("网络错误，稍后自动重试");
            }
            finally
            {
                _refreshing = false;
            }
        }

        // TryRefreshToken 失败后的统一上报：凭证失效与可重试错误区分消息
        private void ReportRefreshFailure()
        {
            if (_lastRefreshInvalid)
            {
                SetError("凭证失效，请在 Kimi Code 中 /login");
            }
            else
            {
                _failCount++; // 网络/服务端异常：计入退避计数
                SetError("Token 刷新失败（网络或服务端异常），稍后自动重试");
            }
        }

        // ===================== 渲染：图标 / tooltip / 提醒 =====================

        private void RenderData(UsagesResponse u)
        {
            _lastData = u;
            _lastSuccessAt = DateTime.Now;

            string text;
            Color color;
            ComputeIcon(u, out text, out color);
            SetIcon(text, color);
            _tray.Text = ""; // 正常状态悬停不显示 tooltip（详情走左键窗口）；错误时由 SetError 设置原因
            CheckAlert(u);

            // 详情窗口开着时同步最新数据
            if (_detailForm != null && !_detailForm.IsDisposed && _detailForm.Visible)
                _detailForm.SetData(u);
        }

        private void ComputeIcon(UsagesResponse u, out string text, out Color color)
        {
            text = "!";
            color = ColorGray;
            if (u == null) return;

            if (_settings.IconSource == "weekly")
            {
                int? pct = Percent(u.Usage);
                if (pct.HasValue)
                {
                    text = pct.Value.ToString();
                    color = ColorForPercent(pct.Value);
                }
            }
            else if (_settings.IconSource == "extraBalance")
            {
                long? left = ExtraBalanceRaw(u.BoosterWallet);
                if (left.HasValue)
                {
                    long yuan = left.Value / 100000000; // 10^8 = 1 元，图标显示整数部分
                    if (yuan < 0) yuan = 0;
                    text = yuan > 999 ? "999" : yuan.ToString();
                    color = ColorBlue; // 中性蓝：0 余额是常态，红/绿会误导为异常或充裕
                }
            }
            else // window5h 默认
            {
                var item = FindWindow5h(u);
                int? pct = item != null ? Percent(item.Detail) : null;
                if (pct.HasValue)
                {
                    text = pct.Value.ToString();
                    color = ColorForPercent(pct.Value);
                }
            }
        }

        internal Color ColorForPercent(int pct)
        {
            var t = _settings.Thresholds != null ? _settings.Thresholds : new ColorThresholds();
            if (pct > t.Green) return ColorGreen;
            if (pct >= t.Yellow) return ColorYellow;
            return ColorRed;
        }

        private void SetIcon(string text, Color color)
        {
            var newIcon = CreateIcon(text, color);
            var old = _icon;
            _icon = newIcon;
            _tray.Icon = newIcon;
            if (old != null)
            {
                DestroyIcon(old.Handle); // 必须释放旧句柄，否则 GDI 泄漏
                old.Dispose();
            }
        }

        private static Icon CreateIcon(string text, Color color)
        {
            // 高 DPI：位图尺寸取系统托盘图标实际尺寸，不再写死 32×32（PerMonitorV2 下随缩放变化）
            var iconSize = SystemInformation.SmallIconSize;
            int w = iconSize.Width;
            int h = iconSize.Height;
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (var brush = new SolidBrush(color))
                    g.FillEllipse(brush, 0, 0, w, h);

                // 字号基准 15/20/24px 是按 32px 位图定的，按实际宽度等比换算
                float fontPx = (text.Length >= 3 ? 15f : (text.Length == 2 ? 20f : 24f)) * w / 32f;
                using (var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat())
                using (var white = new SolidBrush(Color.White))
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(text, font, white, new RectangleF(0, h / 32f, w, h), sf);
                }
            }
            IntPtr hIcon = bmp.GetHicon();
            bmp.Dispose();
            return Icon.FromHandle(hIcon);
        }

        private void SetError(string reason)
        {
            SetIcon("!", ColorGray);
            _tray.Text = TruncateTooltip("Kimi 额度: " + reason);
        }

        private static string TruncateTooltip(string text)
        {
            if (text.Length > MaxTooltipLength) text = text.Substring(0, MaxTooltipLength);
            // 末尾不得残留半个代理对（高位代理项），否则产生非法字符（文案引入 emoji 时兜底）
            if (text.Length > 0 && char.IsHighSurrogate(text[text.Length - 1]))
                text = text.Substring(0, text.Length - 1);
            return text;
        }

        internal static QuotaDetail FindWindow5hDetail(UsagesResponse u)
        {
            var item = FindWindow5h(u);
            return item != null ? item.Detail : null;
        }

        internal static LimitItem FindWindow5h(UsagesResponse u)
        {
            // 按 window.duration=300 + timeUnit=TIME_UNIT_MINUTE 找，不硬编码下标
            if (u == null || u.Limits == null) return null;
            foreach (var item in u.Limits)
            {
                long duration;
                if (item != null && item.Window != null &&
                    long.TryParse(item.Window.Duration, out duration) &&
                    duration == 300 &&
                    item.Window.TimeUnit == "TIME_UNIT_MINUTE")
                    return item;
            }
            return null;
        }

        internal static int? Percent(QuotaDetail d)
        {
            if (d == null) return null;
            long limit, remaining;
            if (!TryParseLong(d.Limit, out limit) || !TryParseLong(d.Remaining, out remaining))
                return null;
            if (limit <= 0) return null;
            int pct = (int)Math.Round(remaining * 100.0 / limit);
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            return pct;
        }

        internal static long? ExtraBalanceRaw(BoosterWallet w)
        {
            if (w == null || w.Balance == null) return null;
            long v;
            if (!TryParseLong(w.Balance.AmountLeft, out v)) return null;
            return v;
        }

        internal static bool TryParseLong(string s, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            return long.TryParse(s, out value);
        }

        internal static string FmtYuanFromCents(long cents)
        {
            long yuan = cents / 100;
            long frac = cents % 100;
            return "¥" + yuan + (frac > 0 ? "." + frac.ToString("00") : "");
        }

        private static string FmtResetFull(string resetTime)
        {
            DateTimeOffset dto;
            if (string.IsNullOrEmpty(resetTime) || !DateTimeOffset.TryParse(resetTime, out dto))
                return "未知";
            return dto.LocalDateTime.ToString("MM-dd HH:mm") + "（" + CountdownText(dto) + "）";
        }

        // 倒计时按本地时间在每次刷新/打开窗口时重算，不做秒级走动
        private static string CountdownText(DateTimeOffset dto)
        {
            var remain = dto.LocalDateTime - DateTime.Now;
            if (remain <= TimeSpan.Zero) return "已重置";
            if (remain.TotalDays >= 1)
                return "还剩 " + (int)remain.TotalDays + "天" + remain.Hours + "小时";
            if (remain.TotalHours >= 1)
                return "还剩 " + (int)remain.TotalHours + "小时" + remain.Minutes + "分";
            return "还剩 " + Math.Max(1, (int)remain.TotalMinutes) + "分";
        }

        // 详情窗口 UI 用：「07-18 15:15 重置 · 还剩 2小时17分」
        internal static string FmtResetUi(string resetTime)
        {
            DateTimeOffset dto;
            if (string.IsNullOrEmpty(resetTime) || !DateTimeOffset.TryParse(resetTime, out dto))
                return "重置时间未知";
            return dto.LocalDateTime.ToString("MM-dd HH:mm") + " 重置 · " + CountdownText(dto);
        }

        // 左键「额度详情」：无边框圆角卡片面板（右下角弹出、置顶、可常驻，每次刷新后同步更新）
        private DetailForm _detailForm;
        internal DateTime _lastSuccessAt;

        private void ShowDetailWindow()
        {
            if (_detailForm == null || _detailForm.IsDisposed)
            {
                _detailForm = new DetailForm(this);
                _detailForm.FormClosed += delegate { _detailForm = null; };
            }
            _detailForm.SetData(_lastData);
            if (!_detailForm.Visible) _detailForm.Show();
            _detailForm.Activate();
        }

        private string BuildDetailText(UsagesResponse u)
        {
            if (u == null) return "暂无数据，请等待刷新或检查网络";
            var sb = new StringBuilder();
            var w5 = FindWindow5hDetail(u);
            if (w5 != null)
            {
                int? pct = Percent(w5);
                sb.Append("5小时窗口: 剩 ").Append(Str(w5.Remaining)).Append('/').Append(Str(w5.Limit));
                if (pct.HasValue) sb.Append(" (").Append(pct.Value).Append("%)");
                sb.Append("\n  重置: ").Append(FmtResetFull(w5.ResetTime));
            }
            if (u.Usage != null)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("周额度: ").Append(Str(u.Usage.Remaining)).Append('/').Append(Str(u.Usage.Limit));
                sb.Append("\n  重置: ").Append(FmtResetFull(u.Usage.ResetTime));
            }
            if (u.TotalQuota != null)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("月总额度: ").Append(Str(u.TotalQuota.Remaining)).Append('/').Append(Str(u.TotalQuota.Limit));
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("Extra余额: ").Append(ExtraDetail(u.BoosterWallet));
            if (u.Parallel != null)
            {
                long limit;
                sb.Append("\n并行会话: ")
                    .Append(u.Parallel.Details != null ? u.Parallel.Details.Count : 0)
                    .Append('/')
                    .Append(TryParseLong(u.Parallel.Limit, out limit) ? limit.ToString() : "?")
                    .Append(" 进行中");
            }
            if (_lastSuccessAt != DateTime.MinValue)
                sb.Append("\n\n更新于: ").Append(_lastSuccessAt.ToString("HH:mm:ss"));
            return sb.ToString();
        }

        // 圆角矩形路径（窗体 Region、卡片、进度条共用）
        internal static GraphicsPath RoundedRect(Rectangle b, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d > b.Width) d = b.Width;
            if (d > b.Height) d = b.Height;
            if (d < 1) { p.AddRectangle(b); return p; }
            p.AddArc(b.X, b.Y, d, d, 180, 90);
            p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
            p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static string ExtraDetail(BoosterWallet w)
        {
            if (w == null) return "未开通";
            long? left = ExtraBalanceRaw(w);
            if (!left.HasValue) return "无数据";
            long cents = (left.Value + 500000) / 1000000;
            string s = FmtYuanFromCents(cents);
            if (w.MonthlyChargeLimitEnabled && w.MonthlyChargeLimit != null && w.MonthlyUsed != null)
            {
                long limitCents, usedCents;
                if (TryParseLong(w.MonthlyChargeLimit.PriceInCents, out limitCents) &&
                    TryParseLong(w.MonthlyUsed.PriceInCents, out usedCents))
                    s += "\n  本月已用: " + FmtYuanFromCents(usedCents) + " / 上限 " + FmtYuanFromCents(limitCents);
            }
            return s;
        }

        internal static string Str(string s)
        {
            return string.IsNullOrEmpty(s) ? "?" : s;
        }

        // ===================== 低额度提醒（ShowBalloonTip） =====================

        private void CheckAlert(UsagesResponse u)
        {
            int th = _settings.LowQuotaAlertThreshold.GetValueOrDefault();
            if (th <= 0) return;
            var d = FindWindow5hDetail(u);
            int? pct = Percent(d);
            if (!pct.HasValue) return;

            if (pct.Value < th)
            {
                // 同一窗口周期（用 resetTime 标识）只提醒一次；
                // 标志不在余量回升时清空，仅在 resetTime 变化（窗口重置）后因 key 不同而自然解除
                string key = d != null ? (d.ResetTime ?? "") : "";
                if (_alertWindowKey != key)
                {
                    _alertWindowKey = key;
                    _tray.ShowBalloonTip(5000, "Kimi 额度提醒",
                        "5小时窗口剩余 " + pct.Value + "%，已低于 " + th + "%",
                        ToolTipIcon.Warning);
                }
            }
        }

        // ===================== 回满提醒（额度恢复时通知） =====================

        // 每次成功刷新后与内存中上一轮成功数据对比；进程启动后首次成功刷新只建立基线不提醒
        private void CheckRefillAlerts(UsagesResponse u)
        {
            var prev = _refillBaseline;
            _refillBaseline = u;
            if (prev == null) return; // 首次成功刷新不触发（避免与 lastGoodData 旧缓存误对比）
            if (_settings.RefillAlert5h.GetValueOrDefault(true))
                CheckRefill(FindWindow5hDetail(prev), FindWindow5hDetail(u), "5小时窗口额度已回满", true);
            if (_settings.RefillAlertWeekly.GetValueOrDefault(true))
                CheckRefill(prev.Usage, u.Usage, "周额度已回满", false);
        }

        private void CheckRefill(QuotaDetail prev, QuotaDetail cur, string message, bool withNumbers)
        {
            if (prev == null || cur == null) return; // 上一轮/本轮字段缺失：跳过，不报错
            if (string.IsNullOrEmpty(prev.ResetTime) || string.IsNullOrEmpty(cur.ResetTime))
                return; // resetTime 缺失视为字段缺失，跳过比对
            if (prev.ResetTime == cur.ResetTime) return; // 窗口未重置
            long limit, remaining;
            if (!TryParseLong(cur.Limit, out limit) || !TryParseLong(cur.Remaining, out remaining))
                return;
            if (remaining != limit) return; // resetTime 变化但未回满（如跨窗口继承余量）
            if (withNumbers)
                message += "（" + remaining + "/" + limit + "）";
            _tray.ShowBalloonTip(5000, "Kimi 额度提醒", message, ToolTipIcon.Info);
        }

        // ===================== 设置持久化（规格书 3.4） =====================
        private void CacheLastGood(UsagesResponse data)
        {
            _settings.LastGoodData = data;
            var json = Serialize(data);
            if (json != _lastGoodJson) // 内容没变就不写盘
            {
                _lastGoodJson = json;
                SaveSettings();
            }
        }

        private static Settings LoadSettings(string path)
        {
            Settings s = null;
            try
            {
                if (File.Exists(path))
                    s = Deserialize<Settings>(File.ReadAllText(path));
            }
            catch
            {
                s = null;
            }
            if (s == null) s = new Settings();
            // DataContractJsonSerializer 反序列化不走构造函数，字段可能缺失，逐项兜底
            if (s.RefreshIntervalMinutes < 1) s.RefreshIntervalMinutes = 1;
            if (string.IsNullOrEmpty(s.IconSource)) s.IconSource = "window5h";
            if (s.Thresholds == null) s.Thresholds = new ColorThresholds();
            if (!s.LowQuotaAlertThreshold.HasValue) s.LowQuotaAlertThreshold = 20; // 缺失 ≠ 显式为 0（关）
            if (!s.RefillAlert5h.HasValue) s.RefillAlert5h = true;       // 缺失 ≠ 显式关闭
            if (!s.RefillAlertWeekly.HasValue) s.RefillAlertWeekly = true;
            if (!s.HistoryEnabled.HasValue) s.HistoryEnabled = true; // 缺失 ≠ 显式关闭
            if (s.HistoryRetentionDays < 1) s.HistoryRetentionDays = 7;
            if (s.EstimateWindowMinutes < 5) s.EstimateWindowMinutes = 60;
            return s;
        }

        internal void SaveSettings()
        {
            try
            {
                AtomicWrite(_settingsPath, Serialize(_settings));
            }
            catch
            {
                // 设置写盘失败不影响主流程
            }
        }

        // 临时文件 + rename 原子写入（与 CLI 写凭证的方式一致）
        internal static void AtomicWrite(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch (PlatformNotSupportedException)
            {
                // File.Replace 仅支持 NTFS；exFAT/FAT32（如 U 盘）上退化为覆盖写，牺牲原子性保底可用
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
        }

        // ===================== JSON（DataContractJsonSerializer，框架自带） =====================

        internal static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var ser = new DataContractJsonSerializer(typeof(T));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return ser.ReadObject(ms) as T;
            }
            catch
            {
                return null; // 字段漂移 / 格式变化：优雅降级
            }
        }

        internal static string Serialize<T>(T obj)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true))
                {
                    ser.WriteObject(w, obj);
                    w.Flush();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // history.jsonl 用：无缩进紧凑输出（EmitDefaultValue=false 的字段被省略）
        internal static string SerializeCompact<T>(T obj)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, false))
                {
                    ser.WriteObject(w, obj);
                    w.Flush();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // ===================== 开机自启（规格书 4.3） =====================

        private static bool ReadAutoStart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    return key != null && key.GetValue(RunValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyAutoStart(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    if (enable)
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else
                        key.DeleteValue(RunValueName, false);
                }
            }
            catch
            {
                // 注册表不可写时静默降级
            }
        }

        // ===================== 退出 =====================

        private void ExitApp()
        {
            SaveSettings();
            _timer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            Application.Exit();
        }
    }
}
