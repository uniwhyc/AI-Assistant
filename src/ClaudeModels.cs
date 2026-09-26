using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AI_Assistant
{
    public static class ClaudeModels
    {
        public static Uri Endpoint(string address)
        {
            Uri uri;
            if (!Uri.TryCreate((address ?? "").Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != "https" && uri.Scheme != "http") || String.IsNullOrEmpty(uri.Host)
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidOperationException("请填写 HTTP/HTTPS 基础地址或模型接口地址，不要包含密钥、查询参数或片段。");
            var builder = new UriBuilder(uri);
            string path = uri.AbsolutePath.TrimEnd('/');
            if (uri.Host == "generativelanguage.googleapis.com" && path == "") path = "/v1beta";
            if (!path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                path += path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) ? "/models" : "/v1/models";
            builder.Path = path;
            return builder.Uri;
        }

        public static async Task<List<string>> FetchAsync(string address, string apiKey, CancellationToken cancellation)
        {
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { MaxResponseContentBufferSize = 2 * 1024 * 1024 })
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                return await FetchAsync(client, address, apiKey, timeout.Token).ConfigureAwait(false);
            }
        }

        public static async Task<List<string>> FetchAsync(HttpClient client, string address, string apiKey, CancellationToken cancellation)
        {
            Uri endpoint = Endpoint(address);
            bool gemini = endpoint.Host == "generativelanguage.googleapis.com";
            if (String.IsNullOrWhiteSpace(apiKey) || apiKey.Any(Char.IsWhiteSpace))
                throw new InvalidOperationException("请填写 API Key，不能包含空白字符。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            Uri next = endpoint;
            for (int page = 0; page < 100; page++)
            {
                cancellation.ThrowIfCancellationRequested();
                using (var request = new HttpRequestMessage(HttpMethod.Get, next))
                {
                    if (gemini) request.Headers.Add("x-goog-api-key", apiKey);
                    else if (endpoint.Host == "api.anthropic.com")
                    {
                        request.Headers.Add("x-api-key", apiKey);
                        request.Headers.Add("anthropic-version", "2023-06-01");
                    }
                    else
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                        // 未知兼容服务保留现有的双认证头行为。
                        if (!ClaudeModelPreset.All.Any(p => !p.IsReference && p.Address.Length > 0 && new Uri(p.Address).Host == endpoint.Host))
                        {
                            request.Headers.Add("x-api-key", apiKey);
                            request.Headers.Add("anthropic-version", "2023-06-01");
                        }
                    }
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using (var response = await client.SendAsync(request, cancellation).ConfigureAwait(false))
                    {
                        int code = (int)response.StatusCode;
                        if (code == 401 || code == 403) throw new InvalidOperationException("认证失败，请检查 API Key 及模型列表访问权限。");
                        if (code == 404) throw new InvalidOperationException("未找到模型接口，请检查 URL；该服务也可能不提供模型列表。");
                        if (code == 429) throw new InvalidOperationException("模型查询过于频繁，请稍后重试。");
                        if (code >= 300 && code < 400) throw new InvalidOperationException("接口返回重定向，请填写最终模型接口地址后重试。");
                        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("模型查询失败，HTTP 状态码：" + code + "。");
                        var root = Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        object data;
                        if (!root.TryGetValue(gemini ? "models" : "data", out data) || !(data is object[]))
                            throw new InvalidOperationException("返回内容缺少模型数组，请确认填写的是模型列表接口。");
                        foreach (object item in (object[])data)
                        {
                            var model = item as Dictionary<string, object>;
                            object id;
                            if (model == null || !model.TryGetValue(gemini ? "name" : "id", out id) || !(id is string) || String.IsNullOrWhiteSpace((string)id))
                                throw new InvalidOperationException("模型列表包含无效的模型 ID。");
                            string modelId = (string)id;
                            ids.Add(gemini && modelId.StartsWith("models/", StringComparison.Ordinal) ? modelId.Substring(7) : modelId);
                        }
                        if (gemini)
                        {
                            object token;
                            if (!root.TryGetValue("nextPageToken", out token) || Object.Equals(token, ""))
                                return ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
                            if (!(token is string) || String.IsNullOrWhiteSpace((string)token) || !cursors.Add((string)token))
                                throw new InvalidOperationException("模型列表的分页信息无效，未显示不完整的结果。");
                            next = new UriBuilder(endpoint) { Query = "pageToken=" + Uri.EscapeDataString((string)token) }.Uri;
                            continue;
                        }
                        object more;
                        if (!root.TryGetValue("has_more", out more) || Object.Equals(more, false))
                            return ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
                        object cursor;
                        if (!Object.Equals(more, true) || !root.TryGetValue("last_id", out cursor)
                            || !(cursor is string) || String.IsNullOrWhiteSpace((string)cursor) || !cursors.Add((string)cursor))
                            throw new InvalidOperationException("模型列表的分页信息无效，未显示不完整的结果。");
                        next = new UriBuilder(endpoint) { Query = "after_id=" + Uri.EscapeDataString((string)cursor) }.Uri;
                    }
                }
            }
            throw new InvalidOperationException("模型列表分页过多，请检查接口地址。");
        }

        static Dictionary<string, object> Parse(string text)
        {
            try
            {
                var result = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                if (result != null) return result;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            throw new InvalidOperationException("模型接口未返回有效的 JSON 对象。");
        }
    }
}
