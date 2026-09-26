using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AI_Assistant;

public static class ClaudeModelsTests
{
    static int passed;
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception("模型检查失败：" + message);
        passed++; Console.WriteLine("通过：" + message);
    }

    sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { return Reply(request, cancellation); }
    }

    static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    { return new HttpResponseMessage(status) { Content = new StringContent(body) }; }

    public static async Task Run()
    {
        string secret = Guid.NewGuid().ToString("N");
        Check(ClaudeModelPreset.All.Select(p => p.Name).Distinct().Count() == ClaudeModelPreset.All.Length, "服务商与套餐预设名称不重复");
        foreach (var preset in ClaudeModelPreset.All.Where(p => p.Address.Length > 0))
        {
            Check(new Uri(preset.Address).Scheme == "https", "预设使用 HTTPS 地址：" + preset.Name);
            if (!preset.IsReference) Check(ClaudeModels.Endpoint(preset.Address).AbsoluteUri == preset.Address, "实时预设直接使用明确的模型接口：" + preset.Name);
            else Check(preset.ReferenceModels.All(id => !String.IsNullOrWhiteSpace(id)) && preset.Source.Length > 0, "套餐参考模型有明确来源：" + preset.Name);
        }
        Check(ClaudeModels.Endpoint("https://generativelanguage.googleapis.com").AbsoluteUri == "https://generativelanguage.googleapis.com/v1beta/models", "Gemini 基础地址使用原生模型接口");
        Check(ClaudeModels.Endpoint("https://example.invalid").AbsoluteUri == "https://example.invalid/v1/models", "基础 URL 补全模型接口");
        Check(ClaudeModels.Endpoint("https://example.invalid/v1/").AbsoluteUri == "https://example.invalid/v1/models", "已有版本路径不会重复追加");
        Check(ClaudeModels.Endpoint("https://example.invalid/api/models").AbsoluteUri == "https://example.invalid/api/models", "完整模型接口保持原路径");
        Check(ClaudeModels.Endpoint("https://example.invalid/anthropic").AbsoluteUri == "https://example.invalid/anthropic/v1/models", "保留自定义服务路径前缀");
        foreach (string address in new[] { "", "file:///路径", "https://user:password@example.invalid", "https://example.invalid?key=参数" })
        {
            bool rejected = false;
            try { ClaudeModels.Endpoint(address); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "拒绝无效或夹带认证信息的 URL");
        }
        int requests = 0;
        using (var handler = new Handler())
        using (var client = new HttpClient(handler))
        {
            handler.Reply = (request, cancellation) => {
                requests++;
                Check(request.Method == HttpMethod.Get && request.Content == null, "查询只发送 GET，不上传配置或日志");
                Check(request.Headers.GetValues("x-api-key").Single() == secret
                    && request.Headers.Authorization.Scheme == "Bearer" && request.Headers.Authorization.Parameter == secret,
                    "携带官方和兼容服务认证头");
                Check(request.Headers.GetValues("anthropic-version").Single() == "2023-06-01", "发送 Claude 官方 API 版本头");
                Check(!request.RequestUri.AbsoluteUri.Contains(secret), "密钥不进入请求 URL");
                return Task.FromResult(Response(requests == 1
                    ? "{\"data\":[{\"id\":\"模型乙\"}],\"has_more\":true,\"last_id\":\"游标 &/\"}"
                    : "{\"data\":[{\"id\":\"模型甲\"},{\"id\":\"模型乙\"}],\"has_more\":false}"));
            };
            var ids = await ClaudeModels.FetchAsync(client, "https://example.invalid", secret, CancellationToken.None);
            Check(requests == 2 && ids.SequenceEqual(new[] { "模型乙", "模型甲" }.OrderBy(x => x, StringComparer.Ordinal)), "获取分页、去重并稳定排序模型 ID");
            handler.Reply = (request, cancellation) => Task.FromResult(Response("{\"object\":\"list\",\"data\":[{\"id\":\"兼容模型\"}]}"));
            Check((await ClaudeModels.FetchAsync(client, "https://example.invalid", secret, CancellationToken.None)).Single() == "兼容模型", "兼容无分页信息的模型列表");
            handler.Reply = (request, cancellation) => Task.FromResult(Response("{\"data\":[]}"));
            Check((await ClaudeModels.FetchAsync(client, "https://example.invalid", secret, CancellationToken.None)).Count == 0, "空模型列表可正常返回");
            int geminiPages = 0;
            handler.Reply = (request, cancellation) => {
                geminiPages++;
                Check(request.Headers.GetValues("x-goog-api-key").Single() == secret && request.Headers.Authorization == null
                    && !request.Headers.Contains("x-api-key"), "Gemini 使用独立认证头，不发送其他平台认证头");
                if (geminiPages == 2) Check(request.RequestUri.Query == "?pageToken=next%20%26%2F", "Gemini 分页游标正确转义");
                return Task.FromResult(Response(geminiPages == 1
                    ? "{\"models\":[{\"name\":\"models/gemini-test-a\"}],\"nextPageToken\":\"next &/\"}"
                    : "{\"models\":[{\"name\":\"models/gemini-test-b\"}]}"));
            };
            var geminiIds = await ClaudeModels.FetchAsync(client, "https://generativelanguage.googleapis.com/v1beta/models", secret, CancellationToken.None);
            Check(geminiPages == 2 && geminiIds.SequenceEqual(new[] { "gemini-test-a", "gemini-test-b" }), "Gemini 模型名称与原生分页正确解析");
            handler.Reply = (request, cancellation) => {
                Check(request.Headers.GetValues("x-api-key").Single() == secret && request.Headers.Authorization == null, "Claude 官方预设使用官方认证方式");
                return Task.FromResult(Response("{\"data\":[]}"));
            };
            await ClaudeModels.FetchAsync(client, "https://api.anthropic.com/v1/models", secret, CancellationToken.None);
            handler.Reply = (request, cancellation) => {
                Check(request.Headers.Authorization.Parameter == secret && !request.Headers.Contains("x-api-key"), "OpenAI 预设使用 Bearer 认证方式");
                return Task.FromResult(Response("{\"data\":[]}"));
            };
            await ClaudeModels.FetchAsync(client, "https://api.openai.com/v1/models", secret, CancellationToken.None);
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
                (HttpStatusCode)429, HttpStatusCode.Redirect, HttpStatusCode.InternalServerError })
            {
                handler.Reply = (request, cancellation) => Task.FromResult(Response(secret, status));
                bool rejected = false;
                try { await ClaudeModels.FetchAsync(client, "https://example.invalid", secret, CancellationToken.None); }
                catch (InvalidOperationException ex) { rejected = !ex.Message.Contains(secret); }
                Check(rejected, "HTTP 错误显示可读提示，不回显响应中的敏感信息");
            }
            foreach (string body in new[] { "<html>错误</html>", "{}", "{\"data\":{}}", "{\"data\":[{}]}",
                "{\"data\":[],\"has_more\":true}", "{\"data\":[],\"has_more\":true,\"last_id\":\"重复\"}" })
            {
                handler.Reply = (request, cancellation) => Task.FromResult(Response(body));
                bool rejected = false;
                try { await ClaudeModels.FetchAsync(client, "https://example.invalid", secret, CancellationToken.None); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "响应结构或分页无效时不展示不完整列表");
            }
            handler.Reply = (request, cancellation) => { throw new Exception("不应发出请求"); };
            bool emptyRejected = false;
            try { await ClaudeModels.FetchAsync(client, "https://example.invalid", "", CancellationToken.None); }
            catch (InvalidOperationException) { emptyRejected = true; }
            Check(emptyRejected, "空 API Key 在发送请求前被拒绝");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool observed = false;
                try { await ClaudeModels.FetchAsync(client, "https://example.invalid", secret, cancelled.Token); }
                catch (OperationCanceledException) { observed = true; }
                Check(observed, "已取消的查询不发送请求");
            }
        }
        Console.WriteLine("模型查询检查：" + passed + " 项通过。");
    }
}
