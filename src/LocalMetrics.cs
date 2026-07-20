// 本机 token 消耗统计（v1.5）：TrayApp 的 partial 部分
// 数据源：%USERPROFILE%\.kimi-code\server\events\*.jsonl（kimi CLI 本地事件日志，纯只读）
// 零新增网络请求、零第三方依赖；进程内状态不持久化（事件文件本身就是持久层，重启重扫不重不漏）
// 口径公式见 docs/计划书-v1.5.md「口径定义」一节，实现照抄：
//   单条事件输入 = usage.inputOther + usage.inputCacheRead + usage.inputCacheCreation
//   缓存命中率   = ΣinputCacheRead / 今日输入 × 100%（四舍五入取整；今日输入 = 0 显示 —）
//   当前速率     = 最近 60 秒 Σoutput / 60（tok/s，整数；60 秒无事件归 0）

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace KimiQuotaTray
{
    internal sealed partial class TrayApp
    {
        private readonly object _lmLock = new object();     // 聚合状态 / 偏移 / 去重集合的统一锁
        private readonly object _lmTailLock = new object(); // 串行化文件 tail（watcher 回调可并发触发）
        private FileSystemWatcher _lmWatcher;
        private bool _lmRunning;
        private bool _lmHasFiles;      // events 目录存在且有 session 文件（卡片降级判断依据）
        private readonly Dictionary<string, long> _lmOffsets = new Dictionary<string, long>();     // 每文件读取偏移
        private readonly Dictionary<string, byte[]> _lmPending = new Dictionary<string, byte[]>(); // 每文件半行缓冲（字节，见 LmTailFile）
        private readonly HashSet<string> _lmSeen = new HashSet<string>(); // sessionId|stepId 全局去重（保险）
        private DateTime _lmDay = DateTime.Today; // 当前统计日（本地），跨天由 LmResetIfNewDayLocked 清零
        private long _lmTodayInput;    // Σ(inputOther + inputCacheRead + inputCacheCreation)
        private long _lmTodayOutput;   // Σ output
        private long _lmTodayCacheRead;
        private int _lmTodayEvents;
        private long? _lmLastTtftMs;   // 最新一条事件的 llmFirstTokenLatencyMs
        private DateTimeOffset _lmLastTtftAt = DateTimeOffset.MinValue; // 该 TTFT 的事件时间（扫描按文件序处理，按时间戳取最新）
        // 最近 120 秒的 (unix秒, output) 环形缓冲；速率每次取最近 60 秒均值
        private readonly List<long[]> _lmRing = new List<long[]>();
        private DateTime _lmLastTooltipAt = DateTime.MinValue; // tooltip 更新节流（≥5 秒）

        private static string LmEventsDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kimi-code", "server", "events");
        }

        // 启动采集：先建 watcher 再后台全量扫描，避免扫描与 watcher 建立之间漏事件；
        // 重叠部分由 sessionId|stepId 去重兜底。目录不存在 → 整个功能降级（卡片不显示，不报错）
        private void StartLocalMetrics()
        {
            lock (_lmLock)
            {
                if (_lmRunning) return;
                try
                {
                    _lmWatcher = new FileSystemWatcher();
                    _lmWatcher.Path = LmEventsDir(); // 目录不存在这里直接抛，进入 catch 降级
                    _lmWatcher.Filter = "*.jsonl";
                    _lmWatcher.NotifyFilter = NotifyFilters.FileName |
                        NotifyFilters.LastWrite | NotifyFilters.Size;
                    _lmWatcher.Created += OnLmFileEvent;
                    _lmWatcher.Changed += OnLmFileEvent;
                    _lmWatcher.Deleted += OnLmFileRemoved;
                    _lmWatcher.Renamed += OnLmFileRenamed;
                    _lmWatcher.EnableRaisingEvents = true;
                    _lmRunning = true;
                }
                catch
                {
                    if (_lmWatcher != null)
                    {
                        try { _lmWatcher.Dispose(); } catch { }
                        _lmWatcher = null;
                    }
                    _lmHasFiles = false;
                    return;
                }
            }
            ThreadPool.QueueUserWorkItem(LmScanAll);
        }

        // 关闭总开关：停采集、释放 watcher 句柄、清空全部聚合状态（卡片/tooltip 由调用方复原）
        private void StopLocalMetrics()
        {
            lock (_lmLock)
            {
                _lmRunning = false;
                if (_lmWatcher != null)
                {
                    try { _lmWatcher.EnableRaisingEvents = false; _lmWatcher.Dispose(); } catch { }
                    _lmWatcher = null;
                }
                _lmOffsets.Clear();
                _lmPending.Clear();
                _lmSeen.Clear();
                _lmRing.Clear();
                _lmTodayInput = 0;
                _lmTodayOutput = 0;
                _lmTodayCacheRead = 0;
                _lmTodayEvents = 0;
                _lmLastTtftMs = null;
                _lmLastTtftAt = DateTimeOffset.MinValue;
                _lmDay = DateTime.Today;
                _lmHasFiles = false;
            }
        }

        // 采集线程异常 → 功能降级态（停采集），绝不让托盘崩溃（与 RefreshAsync 的 catch 哲学一致）
        private void LmDegrade()
        {
            lock (_lmLock)
            {
                _lmRunning = false;
                if (_lmWatcher != null)
                {
                    try { _lmWatcher.EnableRaisingEvents = false; _lmWatcher.Dispose(); } catch { }
                    _lmWatcher = null;
                }
            }
            LmPushUi(); // 快照 HasFiles=false：已显示的卡片立即重排消失，不等下次主刷新
        }

        // 启动全量扫描：逐文件从头读，只统计今日事件；结束后即由 watcher 进入增量 tail
        private void LmScanAll(object state)
        {
            try
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(LmEventsDir(), "*.jsonl");
                }
                catch
                {
                    return; // 目录被删/无权访问：保持降级态
                }
                if (files.Length == 0) return; // 无任何 session 文件：卡片不显示
                lock (_lmLock)
                {
                    if (!_lmRunning) return;
                    _lmHasFiles = true;
                }
                foreach (var f in files)
                    LmTailFile(f);
                LmPushUi();
            }
            catch
            {
                LmDegrade();
            }
        }

        private void OnLmFileEvent(object sender, FileSystemEventArgs e)
        {
            try
            {
                lock (_lmLock)
                {
                    if (!_lmRunning) return;
                    _lmHasFiles = true;
                }
                LmTailFile(e.FullPath);
                LmPushUi();
            }
            catch
            {
                LmDegrade();
            }
        }

        private void OnLmFileRemoved(object sender, FileSystemEventArgs e)
        {
            lock (_lmLock)
            {
                _lmOffsets.Remove(e.FullPath); // 文件被删除/轮转 → 移除偏移
                _lmPending.Remove(e.FullPath);
            }
        }

        private void OnLmFileRenamed(object sender, RenamedEventArgs e)
        {
            lock (_lmLock)
            {
                _lmOffsets.Remove(e.OldFullPath);
                _lmPending.Remove(e.OldFullPath);
            }
            // 新名字按新文件处理：Changed/Created 事件到来时偏移从 0 开始，只统计今日事件
        }

        // 按偏移读文件新增字节；kimi 进程持有写句柄，必须 FileShare.ReadWrite | FileShare.Delete
        private void LmTailFile(string path)
        {
            lock (_lmTailLock)
            {
                byte[] buf;
                byte[] pending;
                lock (_lmLock)
                {
                    if (!_lmRunning) return;
                    if (!_lmOffsets.TryGetValue(path, out _lmTailOffset)) _lmTailOffset = 0;
                    pending = _lmPending.ContainsKey(path) ? _lmPending[path] : null;
                }
                long off = _lmTailOffset;
                long newOff;
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length < off) off = 0; // 文件被截断/轮转：从头读，去重兜底
                        fs.Seek(off, SeekOrigin.Begin);
                        using (var ms = new MemoryStream())
                        {
                            fs.CopyTo(ms, 65536);
                            buf = ms.ToArray();
                        }
                        newOff = fs.Position;
                    }
                }
                catch (FileNotFoundException)
                {
                    lock (_lmLock) { _lmOffsets.Remove(path); _lmPending.Remove(path); }
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }
                catch (IOException)
                {
                    return; // 暂时被占用：偏移不动，下次事件再读
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
                if (buf.Length == 0) return;

                // 半行缓冲按字节存：kimi 若把一个多字节 UTF-8 字符拆进两次写，
                // 按字符串存会被解码成 U+FFFD，导致整行 JSON 解析失败丢事件
                byte[] all;
                if (pending != null && pending.Length > 0)
                {
                    all = new byte[pending.Length + buf.Length];
                    Buffer.BlockCopy(pending, 0, all, 0, pending.Length);
                    Buffer.BlockCopy(buf, 0, all, pending.Length, buf.Length);
                }
                else
                {
                    all = buf;
                }

                // 只解码到最后一个 \n（含）为止的完整部分：0x0A 不会出现在多字节序列内，边界安全；
                // 残段（最后一个 \n 之后的字节）留在 pending，下次拼接后再解析
                int lastNl = -1;
                for (int i = all.Length - 1; i >= 0; i--)
                {
                    if (all[i] == 0x0A) { lastNl = i; break; }
                }
                if (lastNl >= 0)
                {
                    string text = Encoding.UTF8.GetString(all, 0, lastNl + 1);
                    int start = 0;
                    for (int i = 0; i < text.Length; i++)
                    {
                        if (text[i] != '\n') continue;
                        var line = text.Substring(start, i - start).TrimEnd('\r');
                        start = i + 1;
                        if (line.Length > 0) LmProcessLine(line);
                    }
                }
                int restLen = all.Length - (lastNl + 1);
                lock (_lmLock)
                {
                    if (!_lmRunning) return;
                    _lmOffsets[path] = newOff;
                    if (restLen > 0)
                    {
                        var rest = new byte[restLen];
                        Buffer.BlockCopy(all, lastNl + 1, rest, 0, restLen);
                        _lmPending[path] = rest;
                    }
                    else
                    {
                        _lmPending.Remove(path);
                    }
                }
            }
        }
        private long _lmTailOffset; // LmTailFile 内的临时值，避免 C# 5 无 out var 的啰嗦

        // 逐行解析：只取 turn.step.completed 且 timestamp ≥ 今日 00:00 本地的事件；解析失败跳过该行
        private void LmProcessLine(string line)
        {
            var ev = Deserialize<LocalEventLine>(line);
            if (ev == null || ev.Envelope == null) return;
            if (ev.Envelope.Type != "turn.step.completed") return;
            DateTimeOffset ts;
            if (string.IsNullOrEmpty(ev.Envelope.Timestamp) ||
                !DateTimeOffset.TryParse(ev.Envelope.Timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out ts))
                return;
            if (ts.LocalDateTime < DateTime.Today) return; // 只统计今日
            var p = ev.Envelope.Payload;
            if (p == null || p.Usage == null) return;

            lock (_lmLock)
            {
                if (!_lmRunning) return;
                LmResetIfNewDayLocked(); // 跨天清零：常驻进程过 0 点后「今日」重新累计
                if (!string.IsNullOrEmpty(p.StepId))
                {
                    // sessionId|stepId 全局去重（实测无重复，这是保险）
                    string sessionId = p.SessionId ?? ev.Envelope.SessionId ?? "";
                    if (!_lmSeen.Add(sessionId + "|" + p.StepId)) return;
                }
                _lmTodayInput += p.Usage.InputOther + p.Usage.InputCacheRead + p.Usage.InputCacheCreation;
                _lmTodayOutput += p.Usage.Output;
                _lmTodayCacheRead += p.Usage.InputCacheRead;
                _lmTodayEvents++;
                // 启动全量扫描按文件序处理，不能靠到达顺序取「最新」，按事件时间戳比较
                if (p.LlmFirstTokenLatencyMs.HasValue && ts >= _lmLastTtftAt)
                {
                    _lmLastTtftAt = ts;
                    _lmLastTtftMs = p.LlmFirstTokenLatencyMs.Value;
                }
                _lmRing.Add(new long[] { ts.ToUnixTimeSeconds(), p.Usage.Output });
            }
        }

        // 跨天清零（调用方须持锁）：「今日」口径按本地日期，常驻进程过 0 点重新累计；
        // 不会丢数据——事件按自身时间戳过滤，重置只清内存计数，历史事件文件还在
        private void LmResetIfNewDayLocked()
        {
            if (DateTime.Today == _lmDay) return;
            _lmDay = DateTime.Today;
            _lmTodayInput = 0;
            _lmTodayOutput = 0;
            _lmTodayCacheRead = 0;
            _lmTodayEvents = 0;
            _lmLastTtftMs = null;
            _lmLastTtftAt = DateTimeOffset.MinValue;
            _lmSeen.Clear();
            _lmRing.Clear();
        }

        // 速率：环形缓冲保留最近 120 秒，每次取最近 60 秒 Σoutput / 60（调用方须持锁）
        private int LmRateLocked(long nowSec)
        {
            long cutoff120 = nowSec - 120;
            _lmRing.RemoveAll(delegate(long[] pt) { return pt[0] < cutoff120; });
            long cutoff60 = nowSec - 60;
            long sum = 0;
            foreach (var pt in _lmRing)
                if (pt[0] >= cutoff60) sum += pt[1];
            return (int)(sum / 60);
        }

        // UI 读取快照（详情卡片 / tooltip 共用），速率在取快照时按当前时间计算
        internal LocalMetricsSnapshot GetLocalMetricsSnapshot()
        {
            var s = new LocalMetricsSnapshot();
            s.Enabled = _settings.LocalMetricsEnabled.GetValueOrDefault(true);
            lock (_lmLock)
            {
                LmResetIfNewDayLocked(); // 0 点后无新事件时，卡片/tooltip 也及时归零
                s.HasFiles = _lmRunning && _lmHasFiles;
                s.HasTodayEvents = _lmTodayEvents > 0;
                s.TodayInput = _lmTodayInput;
                s.TodayOutput = _lmTodayOutput;
                s.TodayCacheRead = _lmTodayCacheRead;
                s.RateTokPerSec = s.HasTodayEvents
                    ? LmRateLocked(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : 0;
                s.TtftMs = _lmLastTtftMs;
            }
            // 命中率必须用加总后的字段计算，禁止对各 session 命中率取平均
            s.CacheHitPct = s.TodayInput > 0
                ? (int)Math.Round(s.TodayCacheRead * 100.0 / s.TodayInput) : -1;
            return s;
        }

        // 事件到达后的 UI 推送：详情窗口可见则 BeginInvoke 只更新本卡片（不整窗重建）；
        // tooltip 节流 ≥5 秒且 SetError 优先（错误态不覆盖）
        private void LmPushUi()
        {
            try
            {
                var snap = GetLocalMetricsSnapshot();
                var form = _detailForm;
                if (form != null && !form.IsDisposed && form.Visible)
                    form.BeginInvoke(new Action(delegate { form.UpdateLocalMetrics(snap); }));
                if (!_inError &&
                    _settings.LocalMetricsEnabled.GetValueOrDefault(true) &&
                    _settings.TrayMetricsTooltipEnabled.GetValueOrDefault(false) &&
                    DateTime.Now - _lmLastTooltipAt >= TimeSpan.FromSeconds(5))
                {
                    _lmLastTooltipAt = DateTime.Now;
                    ApplyNormalTooltip();
                }
            }
            catch
            {
                // UI 推送失败不影响采集
            }
        }

        // 正常态 tooltip：悬停摘要开启且有今日事件时显示单行摘要（≤63 字符，TruncateTooltip 兜底）；
        // 否则恢复为空（现状行为）。错误态由 SetError 覆盖，不走这里
        private void ApplyNormalTooltip()
        {
            if (_settings.LocalMetricsEnabled.GetValueOrDefault(true) &&
                _settings.TrayMetricsTooltipEnabled.GetValueOrDefault(false))
            {
                var snap = GetLocalMetricsSnapshot();
                if (snap.HasFiles && snap.HasTodayEvents)
                {
                    _tray.Text = TruncateTooltip(BuildMetricsTooltip(snap));
                    return;
                }
            }
            _tray.Text = "";
        }

        // ===================== 格式化（详情卡片 / tooltip 共用） =====================

        // token 数：≥1M 用 1.2M，≥1000 用 12.3k（一位小数）
        internal static string FmtTokens(long n)
        {
            if (n >= 1000000) return (n / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000) return (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            return n.ToString();
        }

        // TTFT：<1s 显示毫秒，否则一位小数秒
        internal static string FmtTtft(long ms)
        {
            if (ms < 1000) return ms + "ms";
            return (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        internal static string BuildLmLine1(LocalMetricsSnapshot s)
        {
            if (!s.HasTodayEvents) return "今日暂无对话";
            return "↑ 输入 " + FmtTokens(s.TodayInput) + "   ↓ 输出 " + FmtTokens(s.TodayOutput);
        }

        internal static string BuildLmLine2(LocalMetricsSnapshot s)
        {
            if (!s.HasTodayEvents) return "本机全部 session 聚合，token 口径";
            string hit = s.CacheHitPct >= 0 ? s.CacheHitPct + "%" : "—";
            string t = "缓存命中 " + hit + " · 速率 " + s.RateTokPerSec + " tok/s";
            if (s.TtftMs.HasValue) t += " · TTFT " + FmtTtft(s.TtftMs.Value);
            return t;
        }

        // 托盘悬停摘要：「今日 ↑38.2k ↓12.1k · 45 tok/s · 命中 68%」（≤63 字符）
        internal static string BuildMetricsTooltip(LocalMetricsSnapshot s)
        {
            string hit = s.CacheHitPct >= 0 ? s.CacheHitPct + "%" : "—";
            return "今日 ↑" + FmtTokens(s.TodayInput) + " ↓" + FmtTokens(s.TodayOutput) +
                " · " + s.RateTokPerSec + " tok/s · 命中 " + hit;
        }
    }
}
