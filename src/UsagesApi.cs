// /usages 请求与解析（TrayApp 的 partial 部分）

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KimiQuotaTray
{
    internal sealed partial class TrayApp
    {
        internal const string UserAgent = "kimi-quota-tray/1.3";

        internal static readonly string ApiBase = EnvOr("KIMI_CODE_BASE_URL", "https://api.kimi.com");

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
    }
}
