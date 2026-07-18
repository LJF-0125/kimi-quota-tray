// KimiQuotaTray —— Kimi Code 额度显示托盘工具（非官方第三方工具）
// 设计规格见同目录《计划书.md》。
// .NET Framework 4.8 / C# 5（系统自带 csc.exe），单文件，无第三方依赖。
// User-Agent 固定为 kimi-quota-tray/1.0，严禁伪装成 Kimi Code CLI。
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
    internal sealed class UsagesResponse : IExtensibleDataObject
    {
        // 保留未建模字段（user、parallel 及未来新增字段），
        // 使 settings.lastGoodData 的缓存尽可能接近「响应原文」
        public ExtensionDataObject ExtensionData { get; set; }

        [DataMember(Name = "usage")] public QuotaDetail Usage;              // 周额度
        [DataMember(Name = "limits")] public List<LimitItem> Limits;        // 各滚动窗口
        [DataMember(Name = "totalQuota")] public QuotaDetail TotalQuota;    // 月总额
        [DataMember(Name = "boosterWallet")] public BoosterWallet BoosterWallet; // Extra Usage，可缺失
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
        [DataMember(Name = "autoStart")] public bool AutoStart;
        [DataMember(Name = "colorThresholds")] public ColorThresholds Thresholds;
        [DataMember(Name = "lastGoodData")] public UsagesResponse LastGoodData; // 上次成功响应缓存

        public Settings()
        {
            RefreshIntervalMinutes = 1;
            IconSource = "window5h";
            LowQuotaAlertThreshold = 20;
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
        private const string UserAgent = "kimi-quota-tray/1.0";
        // CLI 公开二进制中的 OAuth 公共 client_id（公共客户端无法保密，社区工具通行做法）
        private const string OAuthClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";
        private const string ConsoleUrl = "https://www.kimi.com/code/console";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "KimiQuotaTray";
        private const int MaxTooltipLength = 127;

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

        private readonly List<ToolStripMenuItem> _iconSourceItems = new List<ToolStripMenuItem>();
        private readonly List<ToolStripMenuItem> _intervalItems = new List<ToolStripMenuItem>();
        private readonly List<ToolStripMenuItem> _alertItems = new List<ToolStripMenuItem>();
        private ToolStripMenuItem _autoStartItem;

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
            _tray.Text = "Kimi 额度: 加载中...";
            _tray.ContextMenuStrip = BuildMenu();
            _tray.MouseDoubleClick += OnTrayDoubleClick;
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

        private void OnTrayDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                RefreshAsync(); // 双击 = 立即刷新
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

            string text;
            Color color;
            ComputeIcon(u, out text, out color);
            SetIcon(text, color);
            _tray.Text = BuildTooltip(u);
            CheckAlert(u);
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
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (var brush = new SolidBrush(color))
                    g.FillEllipse(brush, 0, 0, 32, 32);

                float size = text.Length >= 3 ? 11f : (text.Length == 2 ? 14f : 18f);
                using (var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat())
                using (var white = new SolidBrush(Color.White))
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(text, font, white, new RectangleF(0, 1, 32, 32), sf);
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

        private string BuildTooltip(UsagesResponse u)
        {
            var lines = new List<string>();

            var w5 = FindWindow5hDetail(u);
            if (w5 != null)
            {
                int? pct = Percent(w5);
                lines.Add("5小时: 剩" + (pct.HasValue ? pct.Value + "%" : "?") +
                    " (" + FmtReset(w5.ResetTime) + ")");
            }
            else
            {
                lines.Add("5小时: 无数据");
            }

            if (u.Usage != null)
            {
                lines.Add("周额度: " + Str(u.Usage.Remaining) + "/" + Str(u.Usage.Limit) +
                    " (" + FmtReset(u.Usage.ResetTime) + ")");
            }

            string monthLine = null;
            if (u.TotalQuota != null)
                monthLine = "月总额: " + Str(u.TotalQuota.Remaining) + "/" + Str(u.TotalQuota.Limit);

            lines.Add(ExtraLine(u.BoosterWallet));

            var withMonth = new List<string>(lines);
            if (monthLine != null) withMonth.Insert(withMonth.Count - 1, monthLine);
            string text = string.Join("\n", withMonth.ToArray());
            if (text.Length > MaxTooltipLength && monthLine != null)
                text = string.Join("\n", lines.ToArray()); // 超长砍「月总额」行
            return TruncateTooltip(text);
        }

        private static string ExtraLine(BoosterWallet w)
        {
            if (w == null) return "Extra: 未开通";
            long? left = ExtraBalanceRaw(w);
            if (!left.HasValue) return "Extra: 无数据";

            // 余额 ÷ 10^8 = 元，四舍五入到分（全程整数运算）
            long cents = (left.Value + 500000) / 1000000;
            string line = "Extra: " + FmtYuanFromCents(cents);

            if (w.MonthlyChargeLimitEnabled && w.MonthlyChargeLimit != null && w.MonthlyUsed != null)
            {
                long limitCents, usedCents;
                if (TryParseLong(w.MonthlyChargeLimit.PriceInCents, out limitCents) &&
                    TryParseLong(w.MonthlyUsed.PriceInCents, out usedCents))
                {
                    line += " (本月" + FmtYuanFromCents(usedCents) + "/" + FmtYuanFromCents(limitCents) + ")";
                }
            }
            return line;
        }

        private static string FmtYuanFromCents(long cents)
        {
            long yuan = cents / 100;
            long frac = cents % 100;
            return "¥" + yuan + (frac > 0 ? "." + frac.ToString("00") : "");
        }

        private static string FmtReset(string resetTime)
        {
            DateTimeOffset dto;
            if (string.IsNullOrEmpty(resetTime) || !DateTimeOffset.TryParse(resetTime, out dto))
                return "重置未知";
            var local = dto.LocalDateTime;
            return local.Date == DateTime.Today
                ? local.ToString("HH:mm") + "重置"
                : local.ToString("MM-dd") + "重置";
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
