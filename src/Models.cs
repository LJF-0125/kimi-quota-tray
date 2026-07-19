// 数据模型（DataContractJsonSerializer）
// 注意：/usages 返回中所有数字都是字符串（64 位整数），这里全部用 string 接收，
// 解析时用 Int64，不用浮点。任何字段缺失都要能优雅降级。

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace KimiQuotaTray
{
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
        [DataMember(Name = "historyEnabled")] public bool? HistoryEnabled;   // 可空以区分「字段缺失」与「显式关闭」
        [DataMember(Name = "historyRetentionDays")] public int HistoryRetentionDays;
        [DataMember(Name = "estimateWindowMinutes")] public int EstimateWindowMinutes;
        [DataMember(Name = "predictiveAlertEnabled")] public bool? PredictiveAlertEnabled; // 可空以区分「字段缺失」与「显式关闭」
        [DataMember(Name = "predictiveAlertMinutes")] public int PredictiveAlertMinutes;   // 手改可调，不进菜单

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
            HistoryEnabled = true;
            HistoryRetentionDays = 7;
            EstimateWindowMinutes = 60;
            PredictiveAlertEnabled = true;
            PredictiveAlertMinutes = 30;
        }
    }

    // history.jsonl 单行记录（v1.3）：一行一条紧凑 JSON，缺失字段省略（EmitDefaultValue=false）
    [DataContract]
    internal sealed class HistorySample
    {
        [DataMember(Name = "t", Order = 1)] public long T;              // 采样时间，秒级 Unix 时间戳，必填
        [DataMember(Name = "w5u", Order = 2, EmitDefaultValue = false)] public long? W5u; // 5小时窗口 used
        [DataMember(Name = "w5l", Order = 3, EmitDefaultValue = false)] public long? W5l; // 5小时窗口 limit
        [DataMember(Name = "w5r", Order = 4, EmitDefaultValue = false)] public string W5r; // 5小时窗口 resetTime 原文
        [DataMember(Name = "wku", Order = 5, EmitDefaultValue = false)] public long? Wku; // 周额度 used
        [DataMember(Name = "wkl", Order = 6, EmitDefaultValue = false)] public long? Wkl; // 周额度 limit
        [DataMember(Name = "ex", Order = 7, EmitDefaultValue = false)] public long? Ex;   // Extra 余额原值（1e-8 元整数）
    }

    // 烧速估算结果：Text 为卡片上的展示文案
    internal sealed class EstimateResult
    {
        public bool Ready;       // 样本足够、斜率可用
        public double Slope;     // 请求/分钟（可 ≤ 0，滚动窗口滑出是正常输出）
        public string Text;
        public bool HasEta;      // 有可绘制的 ETA（24h 内耗尽）
        public long EtaUnix;     // ETA 秒级 Unix 时间戳
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
}
