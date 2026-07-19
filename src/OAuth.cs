// 凭证读取、token 刷新、原子写回（TrayApp 的 partial 部分）

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KimiQuotaTray
{
    internal sealed partial class TrayApp
    {
        // CLI 公开二进制中的 OAuth 公共 client_id（公共客户端无法保密，社区工具通行做法）
        internal const string OAuthClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";

        internal static readonly string OAuthHost =
            EnvOr("KIMI_CODE_OAUTH_HOST", EnvOr("KIMI_OAUTH_HOST", "https://auth.kimi.com"));

        internal static string EnvOr(string name, string fallback)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            return v.Trim().TrimEnd('/');
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
    }
}
