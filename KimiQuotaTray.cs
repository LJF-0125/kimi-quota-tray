// KimiQuotaTray —— Kimi Code 额度显示托盘工具（非官方第三方工具）
// 设计规格见同目录《计划书.md》。
// .NET Framework 4.8 / C# 5（系统自带 csc.exe），单文件，无第三方依赖。
// User-Agent 固定为 kimi-quota-tray/1.2，严禁伪装成 Kimi Code CLI。
#pragma warning disable 4014 // 有意 fire-and-forget 的 async 调用

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
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

    // ===================== 数据模型（DataContractJsonSerializer） =====================
    // 注意：/usages 返回中所有数字都是字符串（64 位整数），这里全部用 string 接收，
    // 解析时用 Int64，不用浮点。任何字段缺失都要能优雅降级。

    [DataContract]
    internal sealed class QuotaDetail : IExtensibleDataObject
    {
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "limit")] public string Limit;
        [DataMember(Name = "used")] public string Used;
        [DataMember(Name = "remaining")] public string Remaining;
        [DataMember(Name = "resetTime")] public string ResetTime;
    }

    [DataContract]
    internal sealed class LimitWindow : IExtensibleDataObject
    {
        public ExtensionDataObject ExtensionData { get; set; }

        // 实测样例是裸数字 300，但服务端惯例是「数字皆字符串」：统一按字符串接收，
        // 反序列化前把裸数字归一化成字符串（见 NormalizeDurationNumbers），抗字段漂移
        [DataMember(Name = "duration")] public string Duration;
        [DataMember(Name = "timeUnit")] public string TimeUnit;
    }

    [DataContract]
    internal sealed class LimitItem : IExtensibleDataObject
    {
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "window")] public LimitWindow Window;
        [DataMember(Name = "detail")] public QuotaDetail Detail;
    }

    [DataContract]
    internal sealed class MoneyAmount : IExtensibleDataObject
    {
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "amount")] public string Amount;
        [DataMember(Name = "amountLeft")] public string AmountLeft;
        [DataMember(Name = "unit")] public string Unit;
    }

    [DataContract]
    internal sealed class Money : IExtensibleDataObject
    {
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "currency")] public string Currency;
        [DataMember(Name = "priceInCents")] public string PriceInCents;
    }

    [DataContract]
    internal sealed class BoosterWallet : IExtensibleDataObject
    {
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "balance")] public MoneyAmount Balance;
        [DataMember(Name = "status")] public string Status;
        [DataMember(Name = "monthlyChargeLimitEnabled")] public bool MonthlyChargeLimitEnabled;
        [DataMember(Name = "monthlyChargeLimit")] public Money MonthlyChargeLimit;
        [DataMember(Name = "monthlyUsed")] public Money MonthlyUsed;
    }

    [DataContract]
    internal sealed class ParallelInfo : IExtensibleDataObject
    {
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "limit")] public string Limit;
        [DataMember(Name = "details")] public List<string> Details; // 此刻执行中的请求，长度 = 占用数
    }

    [DataContract]
    internal sealed class UsagesResponse : IExtensibleDataObject
    {
        // 保留未建模字段（user 及未来新增字段），
        // 使 settings.lastGoodData 的缓存尽可能接近「响应原文」
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "usage")] public QuotaDetail Usage;              // 周额度
        [DataMember(Name = "limits")] public List<LimitItem> Limits;        // 各滚动窗口
        [DataMember(Name = "totalQuota")] public QuotaDetail TotalQuota;    // 月总额
        [DataMember(Name = "boosterWallet")] public BoosterWallet BoosterWallet; // Extra Usage，可缺失
        [DataMember(Name = "parallel")] public ParallelInfo Parallel;       // 并行会话，可缺失
    }

    [DataContract]
    internal sealed class Credentials : IExtensibleDataObject
    {
        // 往返保留未建模字段：CLI 未来新增字段不会在刷新写回时丢失
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "access_token")] public string AccessToken;
        [DataMember(Name = "refresh_token")] public string RefreshToken;
        [DataMember(Name = "expires_at")] public long ExpiresAt;   // 秒级 Unix 时间戳
        [DataMember(Name = "expires_in")] public long ExpiresIn;
        [DataMember(Name = "token_type")] public string TokenType;
        [DataMember(Name = "scope")] public string Scope;
    }

    [DataContract]
    internal sealed class TokenResponse
    {
        [DataMember(Name = "access_token")] public string AccessToken;
        [DataMember(Name = "refresh_token")] public string RefreshToken; // 会轮换，必须写回
        [DataMember(Name = "expires_in")] public long ExpiresIn;
        [DataMember(Name = "token_type")] public string TokenType;
        [DataMember(Name = "scope")] public string Scope;
        [DataMember(Name = "error")] public string Error; // invalid_grant 等
    }

    [DataContract]
    internal sealed class ColorThresholds
    {
        [DataMember(Name = "green")] public int Green;
        [DataMember(Name = "yellow")] public int Yellow;

        public ColorThresholds()
        {
            Green = 50;
            Yellow = 20;
        }
    }

    [DataContract]
    internal sealed class Settings
    {
        [DataMember(Name = "refreshIntervalMinutes")] public int RefreshIntervalMinutes;
        [DataMember(Name = "iconSource")] public string IconSource;            // window5h | weekly | extraBalance
        [DataMember(Name = "lowQuotaAlertThreshold")] public int? LowQuotaAlertThreshold; // 0 = 关；可空以区分「字段缺失」与「显式为 0」
        [DataMember(Name = "refillAlert5h")] public bool? RefillAlert5h;         // 可空以区分「字段缺失」与「显式关闭」
        [DataMember(Name = "refillAlertWeekly")] public bool? RefillAlertWeekly;
        [DataMember(Name = "autoStart")] public bool AutoStart;
        [DataMember(Name = "detailWidth")] public int? DetailWidth; // 详情窗口宽（逻辑像素），可空 = 默认
        [DataMember(Name = "detailHeight")] public int? DetailHeight; // 详情窗口高（逻辑像素），可空 = 按内容自适应
        [DataMember(Name = "colorThresholds")] public ColorThresholds Thresholds;
        [DataMember(Name = "lastGoodData")] public UsagesResponse LastGoodData; // 上次成功响应缓存

        public Settings()
        {
            RefreshIntervalMinutes = 1;
            IconSource = "window5h";
            LowQuotaAlertThreshold = 20;
            RefillAlert5h = true;
            RefillAlertWeekly = true;
            AutoStart = false;
            Thresholds = new ColorThresholds();
            LastGoodData = null;
        }
    }

    internal sealed class QuotaHttpException : Exception
    {
        public readonly int StatusCode;
        public QuotaHttpException(int statusCode)
            : base("HTTP " + statusCode)
        {
            StatusCode = statusCode;
        }
    }

    // ===================== 主程序（无窗体 ApplicationContext） =====================

    internal sealed class TrayApp : ApplicationContext
    {
        private const string UserAgent = "kimi-quota-tray/1.2";
        // CLI 公开二进制中的 OAuth 公共 client_id（公共客户端无法保密，社区工具通行做法）
        private const string OAuthClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";
        private const string ConsoleUrl = "https://www.kimi.com/code/console";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "KimiQuotaTray";
        // .NET Framework 的 NotifyIcon.Text 硬限制是 63 字符（≥64 抛 ArgumentOutOfRangeException），
        // 不是计划书按新版 Shell 估算的 127 —— 文案必须按 63 排版
        private const int MaxTooltipLength = 63;

        private static readonly string ApiBase = EnvOr("KIMI_CODE_BASE_URL", "https://api.kimi.com");
        private static readonly string OAuthHost =
            EnvOr("KIMI_CODE_OAUTH_HOST", EnvOr("KIMI_OAUTH_HOST", "https://auth.kimi.com"));

        private static readonly Color ColorGreen = Color.FromArgb(0x22, 0xC5, 0x5E);
        private static readonly Color ColorYellow = Color.FromArgb(0xEA, 0xB3, 0x08);
        private static readonly Color ColorRed = Color.FromArgb(0xEF, 0x44, 0x44);
        private static readonly Color ColorGray = Color.FromArgb(0x9C, 0xA3, 0xAF);
        private static readonly Color ColorBlue = Color.FromArgb(0x3B, 0x82, 0xF6); // Extra 余额图标用中性蓝

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        // 真实 DPI 来源：.NET Framework 无 app.config 时 Control.DeviceDpi 恒为 96（OnDpiChanged 也不触发），
        // 而 Windows 按 manifest 的 PerMonitorV2 用真实 DPI 渲染文字，两者不一致会把文字底部裁掉
        [DllImport("user32.dll")]
        private static extern int GetDpiForWindow(IntPtr hwnd);

        private readonly string _settingsPath;
        private readonly string _credPath;
        private readonly HttpClient _http;
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _timer;
        private Settings _settings;
        private Icon _icon;
        private UsagesResponse _lastData;
        private string _lastGoodJson; // 用于避免重复写盘
        private int _failCount;
        private bool _refreshing;
        private bool _lastRefreshInvalid; // TryRefreshToken 失败原因：true=凭证失效，false=可重试错误
        private bool _credWriteWarned;    // 凭证写回失败的气泡警告只提示一次，写成功复位
        private string _alertWindowKey; // 同一窗口周期只提醒一次（用 resetTime 作 key）
        private UsagesResponse _refillBaseline; // 回满提醒：上一轮成功数据（仅内存，不持久化）

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

            _settings = LoadSettings(_settingsPath);
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

        private static string EnvOr(string name, string fallback)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            return v.Trim().TrimEnd('/');
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

        private async Task RefreshAsync()
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
                RenderData(data);
                CacheLastGood(data);
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

        // ===================== 凭证与 token 刷新（规格书 2.2 / 2.3） =====================

        private Credentials ReadCredentials()
        {
            try
            {
                if (!File.Exists(_credPath)) return null;
                var cred = Deserialize<Credentials>(File.ReadAllText(_credPath));
                if (cred == null || string.IsNullOrEmpty(cred.AccessToken)) return null;
                return cred;
            }
            catch
            {
                return null;
            }
        }

        private static bool TokenExpired(Credentials cred)
        {
            // 提前 60 秒视为过期，避免请求路上过期
            return cred.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
        }

        private async Task<Credentials> TryRefreshToken(Credentials cred)
        {
            _lastRefreshInvalid = false;
            if (string.IsNullOrEmpty(cred.RefreshToken))
            {
                _lastRefreshInvalid = true;
                return null;
            }

            // main 分支源码是 /api/oauth/token，本机 CLI v0.27.0 是 /v1/oauth/token；前者优先
            var paths = new[] { "/api/oauth/token", "/v1/oauth/token" };
            var backoffMs = new[] { 1000, 2000, 4000 };

            for (int attempt = 0; attempt < 3; attempt++)
            {
                bool transient = false;
                foreach (var path in paths)
                {
                    HttpResponseMessage resp = null;
                    try
                    {
                        using (var req = new HttpRequestMessage(HttpMethod.Post, OAuthHost + path))
                        {
                            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                            {
                                { "client_id", OAuthClientId },
                                { "grant_type", "refresh_token" },
                                { "refresh_token", cred.RefreshToken }
                            });
                            req.Headers.Accept.ParseAdd("application/json");
                            resp = await _http.SendAsync(req);
                        } // req.Dispose 会连带释放 Content
                    }
                    catch (Exception)
                    {
                        transient = true; // 网络错误：退避后重试
                        break;
                    }

                    using (resp)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        int code = (int)resp.StatusCode;
                        if (resp.IsSuccessStatusCode)
                        {
                            var tr = Deserialize<TokenResponse>(body);
                            if (tr == null || string.IsNullOrEmpty(tr.AccessToken))
                            {
                                // 成功状态但响应体解析失败：按可重试错误处理，不误判为凭证失效
                                transient = true;
                                break;
                            }
                            // 先更新内存凭证：即使下面写盘失败，本轮请求也能用新令牌
                            cred.AccessToken = tr.AccessToken;
                            if (!string.IsNullOrEmpty(tr.RefreshToken))
                                cred.RefreshToken = tr.RefreshToken; // refresh_token 轮换，必须写回
                            if (tr.ExpiresIn > 0)
                            {
                                cred.ExpiresIn = tr.ExpiresIn;
                                cred.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + tr.ExpiresIn;
                            }
                            if (!string.IsNullOrEmpty(tr.TokenType)) cred.TokenType = tr.TokenType;
                            if (!string.IsNullOrEmpty(tr.Scope)) cred.Scope = tr.Scope;
                            if (!await TryWriteCredentials(Serialize(cred)) && !_credWriteWarned)
                            {
                                // 写盘失败：内存凭证仍有效并继续本轮请求，但轮换的 refresh_token 未落盘，
                                // 必须明确告知用户，区别于普通「网络错误」
                                _credWriteWarned = true;
                                _tray.ShowBalloonTip(8000, "Kimi 额度",
                                    "凭证文件写入失败（可能被占用）。新令牌仅保留在内存中，重启后可能需要重新登录。",
                                    ToolTipIcon.Warning);
                            }
                            return cred;
                        }
                        if (code == 404) continue; // 此路径不存在，试下一个
                        if (code == 401 || code == 403)
                        {
                            _lastRefreshInvalid = true; // refresh_token 已失效
                            return null;
                        }
                        var err = Deserialize<TokenResponse>(body);
                        if (err != null && err.Error == "invalid_grant")
                        {
                            _lastRefreshInvalid = true;
                            return null;
                        }
                        transient = true; // 429 / 5xx：退避后重试
                        break;
                    }
                }
                if (!transient) return null; // 两个路径都 404，服务端结构变了（按可重试错误上报）
                if (attempt < 2) await Task.Delay(backoffMs[attempt]);
            }
            return null;
        }

        // 凭证原子写回，失败时短退避重试 2 次（与 CLI 并发写 / 杀软占用兜底）
        private async Task<bool> TryWriteCredentials(string json)
        {
            for (int i = 0; i < 3; i++)
            {
                bool failed = false;
                try
                {
                    AtomicWrite(_credPath, json); // 临时文件 + rename，与 CLI 一致
                    _credWriteWarned = false;
                    return true;
                }
                catch
                {
                    failed = true; // C# 5 不允许在 catch 子句中 await，延迟到 catch 外
                }
                if (failed && i < 2) await Task.Delay(300 * (i + 1));
            }
            return false;
        }

        private async Task<UsagesResponse> GetUsages(string accessToken)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/coding/v1/usages"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using (var resp = await _http.SendAsync(req))
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new QuotaHttpException((int)resp.StatusCode);
                    var body = await resp.Content.ReadAsStringAsync();
                    var data = Deserialize<UsagesResponse>(NormalizeDurationNumbers(body));
                    if (data == null) throw new Exception("响应解析失败");
                    return data;
                }
            }
        }

        // duration 实测为裸数字 300，但服务端惯例是「数字皆字符串」：
        // 统一把裸数字归一化为字符串再交给强类型模型（Duration 按 string 建模），两种形式都能解析
        private static string NormalizeDurationNumbers(string json)
        {
            return Regex.Replace(json, "(\"duration\"\\s*:\\s*)(\\d+)", "$1\"$2\"");
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

        private Color ColorForPercent(int pct)
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

        private static QuotaDetail FindWindow5hDetail(UsagesResponse u)
        {
            var item = FindWindow5h(u);
            return item != null ? item.Detail : null;
        }

        private static LimitItem FindWindow5h(UsagesResponse u)
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

        private static int? Percent(QuotaDetail d)
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

        private static long? ExtraBalanceRaw(BoosterWallet w)
        {
            if (w == null || w.Balance == null) return null;
            long v;
            if (!TryParseLong(w.Balance.AmountLeft, out v)) return null;
            return v;
        }

        private static bool TryParseLong(string s, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            return long.TryParse(s, out value);
        }

        private static string FmtYuanFromCents(long cents)
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
        private static string FmtResetUi(string resetTime)
        {
            DateTimeOffset dto;
            if (string.IsNullOrEmpty(resetTime) || !DateTimeOffset.TryParse(resetTime, out dto))
                return "重置时间未知";
            return dto.LocalDateTime.ToString("MM-dd HH:mm") + " 重置 · " + CountdownText(dto);
        }

        // 左键「额度详情」：无边框圆角卡片面板（右下角弹出、置顶、可常驻，每次刷新后同步更新）
        private DetailForm _detailForm;
        private DateTime _lastSuccessAt;

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
        private static GraphicsPath RoundedRect(Rectangle b, int radius)
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

        private sealed class DetailForm : Form
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
                _dpi = GetDpiForWindow(Handle);
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
                using (var path = RoundedRect(new Rectangle(0, 0, Width, Height), Scale(12)))
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
                    var w5 = FindWindow5hDetail(_data);
                    if (w5 != null)
                        y += AddQuotaCard(pad, y, cardW, "5小时窗口", w5, true) + gap;
                    if (_data.Usage != null)
                        y += AddQuotaCard(pad, y, cardW, "周额度", _data.Usage, true) + gap;
                    if (_data.TotalQuota != null)
                        y += AddQuotaCard(pad, y, cardW, "月总额度", _data.TotalQuota, false) + gap;
                    y += AddExtraCard(pad, y, cardW) + gap;

                    if (_data.Parallel != null)
                    {
                        long limit;
                        string text = "并行会话 " +
                            (_data.Parallel.Details != null ? _data.Parallel.Details.Count : 0) +
                            "/" + (TryParseLong(_data.Parallel.Limit, out limit) ? limit.ToString() : "?") +
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
            private int AddQuotaCard(int x, int y, int w, string title, QuotaDetail d, bool showReset)
            {
                int pad = Scale(14);
                int innerW = w - pad * 2;
                int cy = pad;

                int? pct = Percent(d);

                var card = new CardPanel(Scale(8)) { Bounds = new Rectangle(x, y, w, 10) };
                card.Controls.Add(MakeLabel(title, new Rectangle(pad, cy, innerW, Scale(16)),
                    _fontTitle, TitleColor, ContentAlignment.MiddleLeft, Color.Transparent));
                cy += Scale(16) + Scale(4);

                // 大数字（左） + 剩 n/limit（右下对齐）
                string bigText = pct.HasValue ? pct.Value + "%" : "?";
                string sideText = "剩 " + Str(d.Remaining) + "/" + Str(d.Limit);
                int bigH = Scale(34);
                card.Controls.Add(MakeLabel(bigText, new Rectangle(pad, cy, innerW / 2, bigH),
                    _fontBig, BigColor, ContentAlignment.MiddleLeft, Color.Transparent));
                card.Controls.Add(MakeLabel(sideText,
                    new Rectangle(pad + innerW / 2, cy, innerW / 2, bigH),
                    _fontAux, TitleColor, ContentAlignment.BottomRight, Color.Transparent));
                cy += bigH + Scale(10);

                // 进度条：填充色 = ColorForPercent，与托盘图标同一套阈值颜色
                var bar = new ProgressBarControl(pct.GetValueOrDefault(),
                    pct.HasValue ? _app.ColorForPercent(pct.Value) : ColorGray);
                bar.Bounds = new Rectangle(pad, cy, innerW, Scale(6));
                card.Controls.Add(bar);
                cy += Scale(6);

                if (showReset)
                {
                    cy += Scale(8);
                    card.Controls.Add(MakeLabel(FmtResetUi(d.ResetTime),
                        new Rectangle(pad, cy, innerW, Scale(16)),
                        _fontAux, TitleColor, ContentAlignment.MiddleLeft, Color.Transparent));
                    cy += Scale(16);
                }

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
                long? left = ExtraBalanceRaw(wallet);
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
                    card.Controls.Add(MakeLabel(FmtYuanFromCents(cents),
                        new Rectangle(pad, cy, innerW, Scale(34)),
                        _fontBig, BigColor, ContentAlignment.MiddleLeft, Color.Transparent));
                    cy += Scale(34) + Scale(6);

                    long limitCents = 0, usedCents = 0;
                    bool hasLimit = wallet.MonthlyChargeLimitEnabled &&
                        wallet.MonthlyChargeLimit != null && wallet.MonthlyUsed != null &&
                        TryParseLong(wallet.MonthlyChargeLimit.PriceInCents, out limitCents) &&
                        TryParseLong(wallet.MonthlyUsed.PriceInCents, out usedCents) &&
                        limitCents > 0;
                    if (hasLimit)
                    {
                        card.Controls.Add(MakeLabel(
                            "本月已用 " + FmtYuanFromCents(usedCents) + " / 上限 " + FmtYuanFromCents(limitCents),
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
            private sealed class CardPanel : Panel
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
                    using (var path = RoundedRect(rect, _radius))
                    {
                        using (var brush = new SolidBrush(Color.White))
                            g.FillPath(brush, path);
                        using (var pen = new Pen(BorderColor))
                            g.DrawPath(pen, path);
                    }
                }
            }

            // 进度条：轨道 #E5E7EB，填充色由调用方按阈值给定；每次刷新随卡片重建
            private sealed class ProgressBarControl : Control
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
                    using (var track = RoundedRect(new Rectangle(0, 0, r.Width - 1, r.Height - 1), r.Height / 2))
                    using (var trackBrush = new SolidBrush(BorderColor))
                        g.FillPath(trackBrush, track);
                    int w = (int)Math.Round((r.Width - 1) * _percent / 100.0);
                    if (w > 0)
                    {
                        if (w > r.Width - 1) w = r.Width - 1;
                        using (var fill = RoundedRect(new Rectangle(0, 0, w, r.Height - 1), r.Height / 2))
                        using (var fillBrush = new SolidBrush(_fillColor))
                            g.FillPath(fillBrush, fill);
                    }
                }
            }
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

        private static string Str(string s)
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
            return s;
        }

        private void SaveSettings()
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
        private static void AtomicWrite(string path, string content)
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

        private static T Deserialize<T>(string json) where T : class
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

        private static string Serialize<T>(T obj)
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
