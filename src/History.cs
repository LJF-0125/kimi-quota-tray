// history.jsonl 存储 + BurnEstimator 烧速估算（TrayApp 的 partial 部分）

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KimiQuotaTray
{
    // 烧速估算算法：Theil-Sen 中位数斜率（稳健回归），详见《计划书-v1.3.md》第二部分，勿换模型
    internal static class BurnEstimator
    {
        public const double Epsilon = 0.01; // 请求/分钟

        internal struct SamplePoint
        {
            public long T;   // 秒级 Unix 时间戳
            public double U; // used
        }

        // 按索引均匀抽稀到 ≤ maxPoints 个（首尾保留）
        public static List<SamplePoint> Downsample(List<SamplePoint> pts, int maxPoints)
        {
            if (pts.Count <= maxPoints) return pts;
            var r = new List<SamplePoint>(maxPoints);
            for (int i = 0; i < maxPoints; i++)
                r.Add(pts[(int)((long)i * (pts.Count - 1) / (maxPoints - 1))]);
            return r;
        }

        // Theil-Sen：所有点对 (i<j) 斜率的中位数（请求/分钟）；pts 须按 T 升序
        // 样本不足（点数 < 5 或最早最晚跨度 < 10 分钟）返回 false
        public static bool MedianSlope(List<SamplePoint> pts, out double slopePerMinute)
        {
            slopePerMinute = 0;
            if (pts.Count < 5) return false;
            if (pts[pts.Count - 1].T - pts[0].T < 600) return false;
            var slopes = new List<double>();
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double dt = (pts[j].T - pts[i].T) / 60.0;
                    if (dt <= 0) continue;
                    slopes.Add((pts[j].U - pts[i].U) / dt);
                }
            if (slopes.Count == 0) return false;
            slopes.Sort();
            int n = slopes.Count;
            slopePerMinute = (n % 2 == 1) ? slopes[n / 2] : (slopes[n / 2 - 1] + slopes[n / 2]) / 2.0;
            return true;
        }
    }

    internal sealed partial class TrayApp
    {
        // ===================== 用量历史存储（计划书 v1.3 第一部分） =====================

        // 逐行读取历史文件，坏行跳过（append 非原子，容忍最后半行）；结果缓存于内存
        private void EnsureHistoryLoaded()
        {
            if (_historyLoaded) return;
            _historyLoaded = true;
            _history = new List<HistorySample>();
            try
            {
                if (!File.Exists(_historyPath)) return;
                foreach (var line in File.ReadAllLines(_historyPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var s = Deserialize<HistorySample>(line);
                    if (s != null && s.T > 0) _history.Add(s);
                }
            }
            catch
            {
                // 历史文件不可读不影响主流程
            }
        }

        internal List<HistorySample> GetHistory()
        {
            EnsureHistoryLoaded();
            return _history;
        }

        // 每次成功刷新后追加一条采样；historyEnabled=false 不写历史
        private void AppendHistory(UsagesResponse u)
        {
            if (!_settings.HistoryEnabled.GetValueOrDefault(true)) return;
            if (u == null) return;
            EnsureHistoryLoaded();
            var s = new HistorySample();
            s.T = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var w5 = FindWindow5hDetail(u);
            if (w5 != null)
            {
                long v;
                if (TryParseLong(w5.Used, out v)) s.W5u = v;
                if (TryParseLong(w5.Limit, out v)) s.W5l = v;
                s.W5r = string.IsNullOrEmpty(w5.ResetTime) ? null : w5.ResetTime;
            }
            if (u.Usage != null)
            {
                long v;
                if (TryParseLong(u.Usage.Used, out v)) s.Wku = v;
                if (TryParseLong(u.Usage.Limit, out v)) s.Wkl = v;
            }
            long? ex = ExtraBalanceRaw(u.BoosterWallet);
            if (ex.HasValue) s.Ex = ex.Value;
            try
            {
                File.AppendAllText(_historyPath, SerializeCompact(s) + "\n", new UTF8Encoding(false));
                _history.Add(s); // 写盘成功后才同步内存缓存，避免缓存与文件不一致
            }
            catch
            {
                // 历史写盘失败不影响主流程
            }
        }

        // 启动时 + 之后每 24 小时：丢弃 retention 天之前的记录，读全部 → 过滤 → 原子重写
        private void CleanupHistoryIfDue()
        {
            if (!_settings.HistoryEnabled.GetValueOrDefault(true)) return; // 历史禁用则不碰文件
            var now = DateTime.UtcNow;
            if ((now - _lastHistoryCleanupUtc).TotalHours < 24) return;
            _lastHistoryCleanupUtc = now;
            EnsureHistoryLoaded();
            if (_history.Count == 0) return;
            int days = _settings.HistoryRetentionDays;
            if (days < 1) days = 7;
            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)days * 86400;
            var kept = new List<HistorySample>();
            foreach (var s in _history)
                if (s.T >= cutoff) kept.Add(s);
            if (kept.Count == _history.Count) return;
            _history = kept;
            try
            {
                var sb = new StringBuilder();
                foreach (var s in kept) sb.Append(SerializeCompact(s)).Append('\n');
                AtomicWrite(_historyPath, sb.ToString()); // 临时文件 + rename，与设置/凭证写盘一致
            }
            catch
            {
                // 清理失败不影响主流程，24 小时后重试
            }
        }

        // ===================== 烧速估算（Theil-Sen，计划书 v1.3 第二部分） =====================

        // 收集 fromUnix 以来的样本点（isWindow5h: 取 w5u，否则取 wku），按时间升序
        internal List<BurnEstimator.SamplePoint> CollectPoints(long fromUnix, bool isWindow5h)
        {
            var pts = new List<BurnEstimator.SamplePoint>();
            foreach (var s in GetHistory())
            {
                long? v = isWindow5h ? s.W5u : s.Wku;
                if (s.T < fromUnix || !v.HasValue) continue;
                var p = new BurnEstimator.SamplePoint();
                p.T = s.T;
                p.U = v.Value;
                pts.Add(p);
            }
            pts.Sort(delegate(BurnEstimator.SamplePoint a, BurnEstimator.SamplePoint b)
            {
                return a.T.CompareTo(b.T);
            });
            return pts;
        }

        // 5小时窗口：最近 EstimateWindowMinutes 分钟样本做 Theil-Sen，四档判定输出
        internal EstimateResult EstimateWindow5h(QuotaDetail w5)
        {
            var r = new EstimateResult();
            r.Text = "数据积累中";
            long limit, used;
            if (w5 == null || !TryParseLong(w5.Limit, out limit) || !TryParseLong(w5.Used, out used))
                return r;
            int windowMin = _settings.EstimateWindowMinutes;
            if (windowMin < 5) windowMin = 60;
            string speed = "按近" + windowMin + "分钟速度"; // 估算窗口可配，文案跟随 settings
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var pts = CollectPoints(now - (long)windowMin * 60, true);
            double slope;
            if (!BurnEstimator.MedianSlope(BurnEstimator.Downsample(pts, 400), out slope))
                return r;
            r.Ready = true;
            r.Slope = slope;
            if (slope <= BurnEstimator.Epsilon)
            {
                r.Text = speed + "，暂无耗尽风险";
                return r;
            }
            double etaMin = (limit - used) / slope;
            if (etaMin < 0) etaMin = 0;
            if (etaMin <= 24 * 60)
            {
                r.HasEta = true;
                r.EtaUnix = now + (long)(etaMin * 60);
                int total = Math.Max(1, (int)Math.Round(etaMin)); // 与 CountdownText 一致：不足 1 分钟显示 1分
                string remain = total >= 60
                    ? (total / 60) + "小时" + (total % 60) + "分"
                    : total + "分";
                r.Text = speed + "，预计 " +
                    DateTimeOffset.FromUnixTimeSeconds(r.EtaUnix).LocalDateTime.ToString("HH:mm") +
                    " 耗尽（还剩 " + remain + "）";
            }
            else
            {
                r.Text = speed + "，24小时内不会耗尽";
            }
            return r;
        }

        // 周额度：最近 24 小时样本同样回归，与重置时间比对；usage/reset 缺失返回 null（不显示估算行）
        internal EstimateResult EstimateWeekly(QuotaDetail usage)
        {
            long limit, used;
            if (usage == null || !TryParseLong(usage.Limit, out limit) || !TryParseLong(usage.Used, out used))
                return null;
            DateTimeOffset reset;
            if (string.IsNullOrEmpty(usage.ResetTime) || !DateTimeOffset.TryParse(usage.ResetTime, out reset))
                return null;
            var r = new EstimateResult();
            r.Text = "数据积累中";
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var pts = CollectPoints(now - 24 * 3600, false);
            // v1.3.1 前置门槛：样本最早最晚跨度 < 12 小时不做外推（短期速度外推 5 天无意义）
            if (pts.Count < 2 || pts[pts.Count - 1].T - pts[0].T < 12 * 3600)
            {
                r.Text = "数据积累中（满 12 小时后出周预测）";
                return r;
            }
            double slope;
            if (!BurnEstimator.MedianSlope(BurnEstimator.Downsample(pts, 400), out slope))
                return r;
            r.Ready = true;
            r.Slope = slope;
            double minutesToReset = (reset.ToUnixTimeSeconds() - now) / 60.0;
            if (minutesToReset < 0) minutesToReset = 0;
            double projected = used + slope * minutesToReset;
            if (projected <= limit)
            {
                r.Text = "按当前速度，重置前够用（预计用 " +
                    Math.Max(0, (long)Math.Round(projected)) + "/" + limit + "）";
            }
            else
            {
                // projected > limit ≥ used 蕴含 slope > 0，这里除法安全
                double etaMin = (limit - used) / slope;
                r.HasEta = true;
                r.EtaUnix = now + (long)(Math.Max(0, etaMin) * 60);
                r.Text = "按当前速度，预计 " +
                    DateTimeOffset.FromUnixTimeSeconds(r.EtaUnix).LocalDateTime.ToString("MM-dd HH:mm") + " 耗尽";
            }
            return r;
        }
    }
}
