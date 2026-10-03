using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using AI_Assistant;

public static class UsageTests
{
    static int passed, failed;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static string Line(object value) { return Json.Serialize(value); }
    static string Codex(long input, long cached, long output, string time, long written = 0)
    {
        return Line(new { timestamp = time, type = "event_msg", payload = new { type = "token_count", info = new {
            total_token_usage = new { input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = written,
                output_tokens = output, reasoning_output_tokens = output / 2, total_tokens = input + output },
            last_token_usage = new { input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = written, output_tokens = output }
        } } });
    }
    static string Claude(string id, long input, long read, long write, long output, string time, string request = "req-1")
    {
        return Line(new { type = "assistant", timestamp = time, sessionId = "claude-session", cwd = "E:/测试项目", requestId = request,
            message = new { id = id, model = "测试模型", usage = new { input_tokens = input,
                cache_read_input_tokens = read, cache_creation_input_tokens = write, output_tokens = output,
                cache_creation = new { ephemeral_5m_input_tokens = write }, output_tokens_details = new { thinking_tokens = output / 2 } } } });
    }
    static string ClaudeUser(string text, string time, string uuid, bool meta = false, bool sidechain = false)
    {
        return Line(new { type = "user", timestamp = time, sessionId = "claude-session", uuid = uuid,
            isMeta = meta, isSidechain = sidechain, message = new { role = "user", content = text } });
    }
    static string ClaudeUserBlocks(object[] blocks, string time, string uuid)
    {
        return Line(new { type = "user", timestamp = time, sessionId = "claude-session", uuid = uuid,
            message = new { role = "user", content = blocks } });
    }
    static string ClaudeAssistantTools(string id, int tools, long output, string time)
    {
        var blocks = new object[tools]; for (int i = 0; i < tools; i++) blocks[i] = new { type = "tool_use", name = "Bash" };
        return Line(new { type = "assistant", timestamp = time, sessionId = "claude-session", requestId = "req-1",
            message = new { id = id, model = "测试模型", content = blocks, usage = new { input_tokens = 10, output_tokens = output } } });
    }
    static string CodexUser(string time, string text)
    {
        return Line(new { timestamp = time, type = "response_item", payload = new { type = "message", role = "user",
            content = new object[] { new { type = "input_text", text = text } } } });
    }
    static string CodexCall(string kind, string time)
    { return Line(new { timestamp = time, type = "response_item", payload = new { type = kind, name = "shell" } }); }
    static List<Usage> Parse(string platform, params string[] lines)
    { return UsageReader.Parse(new StringReader(String.Join("\n", lines)), platform, "fixture", new ScanResult()); }
    static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(String.Format("期望 {0}，实际 {1}", expected, actual)); }
    static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("通过：" + name); }
        catch (Exception ex) { failed++; Console.WriteLine("失败：" + name + "\n" + ex); }
    }
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Test("Codex 累计差分、重复快照及推理子项不重复计数", () => {
            var rows = Parse("Codex", Codex(100, 40, 20, "2026-09-12T01:00:00Z"),
                Codex(100, 40, 20, "2026-09-12T01:01:00Z"), Codex(250, 140, 50, "2026-09-13T01:00:00Z"));
            Equal(2, rows.Count); Equal(150L, rows[1].AllInput); Equal(100L, rows[1].CacheRead);
            Equal(300L, Totals.From(rows).Total); Equal(50L, Totals.From(rows).Output);
        });
        Test("Codex 缓存写入从输入中拆分", () => {
            var row = Parse("Codex", Codex(100, 40, 20, "2026-09-13T01:00:00Z", 10)).Single();
            Equal(50L, row.Input); Equal(10L, row.CacheWrite); Equal(120L, row.Total);
        });
        Test("Codex 跨日差分仅归入事件发生日", () => {
            string a = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(12)).ToString("o");
            string b = new DateTimeOffset(DateTime.Today.AddHours(12)).ToString("o");
            var rows = Parse("Codex", Codex(100, 40, 20, a), Codex(250, 140, 50, b));
            Equal(180L, Totals.From(Analytics.Filter(rows, "全部平台", DateTime.Today, DateTime.Today.AddDays(1).AddTicks(-1), "")).Total);
        });
        Test("Codex 累计回退时使用单次用量", () => {
            var rows = Parse("Codex", Codex(100, 40, 20, "2026-09-13T01:00:00Z"), Codex(20, 5, 3, "2026-09-13T01:01:00Z"));
            Equal(143L, Totals.From(rows).Total);
        });
        Test("Codex 回退缺少单次用量时显式提示", () => {
            var report = new ScanResult();
            string reset = Line(new { timestamp = "2026-09-13T01:01:00Z", type = "event_msg", payload = new { type = "token_count", info = new {
                total_token_usage = new { input_tokens = 20, output_tokens = 3 } } } });
            var rows = UsageReader.Parse(new StringReader(Codex(100, 40, 20, "2026-09-13T01:00:00Z") + "\n" + reset), "Codex", "test", report);
            Equal(1, rows.Count); Equal(1, report.Warnings.Count);
        });
        Test("Codex 会话元数据与模型关联", () => {
            string meta = Line(new { type = "session_meta", payload = new { id = "test-session", cwd = "E:/测试项目" } });
            string context = Line(new { type = "turn_context", payload = new { model = "测试模型" } });
            var row = Parse("Codex", meta, context, Codex(10, 0, 2, "2026-09-13T01:00:00Z")).Single();
            Equal("test-session", row.Session); Equal("测试模型", row.Model); Equal("E:/测试项目", row.Project);
        });
        Test("Claude 普通输入、缓存读取、缓存写入独立相加", () => {
            var row = Parse("Claude Code", Claude("m1", 10, 70, 20, 15, "2026-09-13T01:00:00Z")).Single();
            Equal(100L, row.AllInput); Equal(115L, row.Total); Equal(20L, row.CacheWrite);
            Equal((0.7).ToString("P1"), Totals.From(new[] { row }).HitRate);
        });
        Test("Claude 同一响应的流式快照保留完整输出", () => {
            var rows = Parse("Claude Code", Claude("m1", 10, 70, 20, 2, "2026-09-13T01:00:00Z"),
                Claude("m1", 10, 70, 20, 30, "2026-09-13T01:00:01Z"), Claude("m2", 5, 0, 0, 1, "2026-09-13T01:00:02Z"));
            var merged = UsageReader.Deduplicate(rows);
            Equal(2, merged.Count); Equal(136L, Totals.From(merged).Total);
        });
        Test("Claude 不同请求 ID 独立保留", () => {
            var rows = Parse("Claude Code", Claude("m1", 10, 0, 0, 2, "2026-09-13T01:00:00Z", "r1"),
                Claude("m1", 10, 0, 0, 2, "2026-09-13T01:00:01Z", "r2"));
            Equal(2, UsageReader.Deduplicate(rows).Count);
        });
        Test("跨文件重复日志去重", () => {
            string entry = Codex(100, 40, 20, "2026-09-13T01:00:00Z");
            var rows = Parse("Codex", entry).Concat(Parse("Codex", entry));
            Equal(120L, Totals.From(UsageReader.Deduplicate(rows)).Total);
        });
        Test("命中率按 Token 加权而非百分比平均", () => {
            var rows = Parse("Claude Code", Claude("m1", 1, 9, 0, 0, "2026-09-13T01:00:00Z"),
                Claude("m2", 90, 0, 0, 0, "2026-09-13T01:01:00Z"));
            Equal((0.09).ToString("P1"), Totals.From(rows).HitRate);
        });
        Test("空数据和零输入显示无命中率", () => {
            Equal("—", Totals.From(new Usage[0]).HitRate);
            Equal("—", Totals.From(Parse("Codex", Codex(0, 0, 10, "2026-09-13T01:00:00Z"))).HitRate);
        });
        Test("不完整 JSON 和缺失时间不会使扫描失败", () => {
            var report = new ScanResult();
            string bad = "{\"type\":\"assistant\"";
            string noTime = Claude("m1", 10, 0, 0, 3, "无效时间");
            var rows = UsageReader.Parse(new StringReader(bad + "\n" + noTime + "\n" + Claude("m2", 10, 0, 0, 3, "2026-09-13T01:00:00Z")), "Claude Code", "test", report);
            Equal(2, report.InvalidLines); Equal(1, rows.Count);
        });
        Test("未知事件与空用量安全跳过", () => {
            var rows = Parse("Codex", "null", "[]", "{}", Line(new { type = "event_msg", payload = new { type = "token_count", info = (object)null } }));
            Equal(0, rows.Count);
            Equal(0, Parse("Claude Code", Line(new { type = "user", message = new { content = "测试" } })).Count);
        });
        Test("筛选使用本机时区而非 UTC 日期", () => {
            DateTime target = new DateTime(2026, 9, 12);
            var moment = new DateTimeOffset(target.AddMinutes(10), TimeZoneInfo.Local.GetUtcOffset(target));
            var row = Parse("Claude Code", Claude("m1", 10, 0, 0, 2, moment.ToUniversalTime().ToString("o")));
            Equal(1, Analytics.Filter(row, "全部平台", target, target.AddDays(1).AddTicks(-1), "").Count);
            Equal(0, Analytics.Filter(row, "全部平台", target.AddDays(1), target.AddDays(1), "").Count);
        });
        Test("秒级筛选排除同一天范围外的记录", () => {
            DateTime start = DateTime.Today.AddHours(10).AddSeconds(20);
            var rows = new[] { -1, 0, 1, 2 }.Select((offset, index) => new Usage {
                Key = "second-" + index, Platform = "Codex", Time = new DateTimeOffset(start.AddSeconds(offset)), Input = 10
            }).ToList();
            Equal(2, Analytics.Filter(rows, "全部平台", start, start.AddSeconds(1), "").Count);
        });
        Test("结束秒包含毫秒尾部但不包含下一秒", () => {
            DateTime time = DateTime.Today.AddHours(10).AddSeconds(20);
            var rows = new[] { time.AddTicks(-1), time, time.AddMilliseconds(999), time.AddSeconds(1) }
                .Select((moment, index) => new Usage { Key = "edge-" + index, Platform = "Claude Code", Time = new DateTimeOffset(moment), Input = 1 });
            Equal(2, Analytics.Filter(rows, "全部平台", time, time, "").Count);
        });
        Test("跨午夜秒级筛选包含两天的边界记录", () => {
            DateTime midnight = DateTime.Today;
            var rows = new[] { -2, -1, 0, 1, 2 }.Select((offset, index) => new Usage {
                Key = "midnight-" + index, Platform = "Codex", Time = new DateTimeOffset(midnight.AddSeconds(offset)), Input = 1
            });
            Equal(3, Analytics.Filter(rows, "全部平台", midnight.AddSeconds(-1), midnight.AddSeconds(1), "").Count);
        });
        Test("平台与项目、模型、会话搜索组合筛选", () => {
            var rows = Parse("Claude Code", Claude("m1", 10, 0, 0, 2, "2026-09-13T01:00:00Z"));
            Equal(1, Analytics.Filter(rows, "Claude Code", null, DateTime.MaxValue, "测试项目").Count);
            Equal(1, Analytics.Filter(rows, "全部平台", null, DateTime.MaxValue, "CLAUDE-SESSION").Count);
            Equal(0, Analytics.Filter(rows, "Codex", null, DateTime.MaxValue, "").Count);
        });
        Test("会话聚合保留全部模型与精确 Token", () => {
            var rows = Parse("Claude Code", Claude("m1", 10, 20, 30, 40, "2026-09-13T01:00:00Z"), Claude("m2", 1, 2, 3, 4, "2026-09-13T01:01:00Z"));
            var session = Analytics.Sessions(rows).Single();
            Equal(110L, session.Total); Equal("测试项目", session.Project); Equal(44L, session.Output);
        });
        Test("Claude 用户文本消息计为用户请求", () => {
            var rows = Parse("Claude Code", ClaudeUser("帮我改代码", "2026-09-13T01:00:00Z", "u-1"),
                ClaudeUserBlocks(new object[] { new { type = "text", text = "继续" } }, "2026-09-13T01:01:00Z", "u-2"));
            Equal(2, rows.Count); Equal(1L, rows[0].UserRequests); Equal(0L, rows[0].Total);
            Equal(2L, Totals.From(rows).UserRequests);
        });
        Test("Claude 工具结果、元消息与子代理 prompt 不算用户请求", () => {
            var rows = Parse("Claude Code",
                ClaudeUserBlocks(new object[] { new { type = "tool_result", content = "结果" } }, "2026-09-13T01:00:00Z", "u-1"),
                ClaudeUser("元消息", "2026-09-13T01:01:00Z", "u-2", true),
                ClaudeUser("子代理任务", "2026-09-13T01:02:00Z", "u-3", false, true));
            Equal(0, rows.Count);
        });
        Test("Claude 工具调用按 tool_use 块计数且流式快照取最大", () => {
            var rows = Parse("Claude Code", ClaudeAssistantTools("m1", 1, 5, "2026-09-13T01:00:00Z"),
                ClaudeAssistantTools("m1", 2, 9, "2026-09-13T01:00:01Z"));
            var merged = UsageReader.Deduplicate(rows);
            Equal(1, merged.Count); Equal(2L, merged[0].ToolCalls); Equal(9L, merged[0].Output);
        });
        Test("Codex 用户消息计入且环境上下文排除", () => {
            var rows = Parse("Codex", CodexUser("2026-09-13T01:00:00Z", "帮我改代码"),
                CodexUser("2026-09-13T01:01:00Z", "<environment_context><cwd>E:/x</cwd></environment_context>"));
            Equal(1, rows.Count); Equal(1L, rows[0].UserRequests); Equal(0L, rows[0].ToolCalls);
        });
        Test("Codex 两种工具调用记录均计数", () => {
            var rows = Parse("Codex", CodexCall("function_call", "2026-09-13T01:00:00Z"),
                CodexCall("custom_tool_call", "2026-09-13T01:01:00Z"), CodexCall("reasoning", "2026-09-13T01:02:00Z"));
            Equal(2, rows.Count); Equal(2L, Totals.From(rows).ToolCalls);
        });
        Test("Codex 归档与活动副本去重后请求计数不翻倍", () => {
            string entry = CodexUser("2026-09-13T01:00:00Z", "帮我改代码");
            var rows = Parse("Codex", entry).Concat(Parse("Codex", entry));
            Equal(1L, Totals.From(UsageReader.Deduplicate(rows)).UserRequests);
        });
        Test("会话模型列表不受用户请求记录影响", () => {
            var rows = Parse("Claude Code", ClaudeUser("你好", "2026-09-13T01:00:00Z", "u-1"),
                Claude("m1", 10, 0, 0, 2, "2026-09-13T01:01:00Z"));
            var session = Analytics.Sessions(rows).Single();
            Equal("测试模型", session.Model); Equal(1L, session.UserRequests); Equal(0L, session.ToolCalls);
        });
        Test("计数超过 32 位整数仍准确", () => {
            var rows = Parse("Codex", Codex(4000000000L, 3000000000L, 500000000L, "2026-09-13T01:00:00Z"));
            Equal(4500000000L, Totals.From(rows).Total);
        });
        Test("CSV 导出转义引号并抑制公式执行", () => {
            var writer = new StringWriter();
            CsvExport.Write(writer, new[] { new SessionRow { Platform = "Codex", Session = "s", ProjectPath = "=测试\"项目", Model = "测试", Total = 10, HitRate = "—" } });
            string csv = writer.ToString();
            Equal(true, csv.Contains("\"'=测试\"\"项目\"")); Equal(true, csv.Contains("普通输入,缓存读取,缓存写入"));
        });
        Test("不存在的数据源显示两平台提示", () => {
            string missing = Path.Combine(Path.GetTempPath(), "AI_Assistant-不存在-" + Guid.NewGuid().ToString("N"));
            var scan = UsageReader.Scan(new SourcePaths { Codex = missing, Claude = missing }, null);
            Equal(0, scan.Records.Count); Equal(2, scan.Warnings.Count);
        });
        Test("项目聚合合并同目录多会话并精确求和", () => {
            var rows = new[] {
                new Usage { Key = "a", Platform = "Codex", Session = "codex-s1", Project = "E:/测试项目",
                    Time = new DateTimeOffset(2026, 9, 13, 1, 0, 0, TimeSpan.Zero), Input = 10, CacheRead = 20, Output = 5 },
                new Usage { Key = "b", Platform = "Claude Code", Session = "claude-s1", Project = "E:/测试项目",
                    Time = new DateTimeOffset(2026, 9, 13, 2, 0, 0, TimeSpan.Zero), Input = 30, CacheRead = 0, Output = 15 } };
            var proj = Analytics.Projects(rows).Single();
            Equal("测试项目", proj.Project); Equal(80L, proj.Total); Equal(40L, proj.Input);
            Equal(20L, proj.CacheRead); Equal(20L, proj.Output); Equal(2, proj.Sessions);
            Equal(rows[1].Time.LocalDateTime, proj.Last);
            Equal((20.0 / 60).ToString("P1"), proj.HitRate);
        });
        Test("项目分组键统一分隔符与尾斜杠", () => {
            var rows = new[] {
                new Usage { Key = "a", Platform = "Codex", Session = "s1", Project = "E:\\测试项目",
                    Time = new DateTimeOffset(2026, 9, 13, 2, 0, 0, TimeSpan.Zero), Input = 10 },
                new Usage { Key = "b", Platform = "Codex", Session = "s2", Project = "E:/测试项目/",
                    Time = new DateTimeOffset(2026, 9, 13, 1, 0, 0, TimeSpan.Zero), Input = 20 } };
            var proj = Analytics.Projects(rows).Single();
            Equal(30L, proj.Total); Equal("测试项目", proj.Project);
            Equal("E:\\测试项目", proj.ProjectPath);
        });
        Test("项目分组键大小写不敏感且不误合并相近目录", () => {
            var rows = new[] {
                new Usage { Key = "a", Platform = "Codex", Session = "s1", Project = "E:\\Foo",
                    Time = new DateTimeOffset(2026, 9, 13, 1, 0, 0, TimeSpan.Zero), Input = 10 },
                new Usage { Key = "b", Platform = "Codex", Session = "s2", Project = "e:\\foo",
                    Time = new DateTimeOffset(2026, 9, 13, 2, 0, 0, TimeSpan.Zero), Input = 20 },
                new Usage { Key = "c", Platform = "Codex", Session = "s3", Project = "E:\\Foo2",
                    Time = new DateTimeOffset(2026, 9, 13, 3, 0, 0, TimeSpan.Zero), Input = 40 } };
            var proj = Analytics.Projects(rows);
            Equal(2, proj.Count);
            var merged = proj.Single(x => x.Project == "foo");
            Equal(30L, merged.Total); Equal("e:\\foo", merged.ProjectPath);
            Equal(40L, proj.Single(x => x.Project == "Foo2").Total);
        });
        Test("未知项目独立成组且空值兜底", () => {
            var rows = new[] {
                new Usage { Key = "a", Platform = "Codex", Session = "s1", Project = "未知项目",
                    Time = new DateTimeOffset(2026, 9, 13, 1, 0, 0, TimeSpan.Zero), Input = 10 },
                new Usage { Key = "b", Platform = "Codex", Session = "s2", Project = "",
                    Time = new DateTimeOffset(2026, 9, 13, 2, 0, 0, TimeSpan.Zero), Input = 20 },
                new Usage { Key = "c", Platform = "Codex", Session = "s3", Project = null,
                    Time = new DateTimeOffset(2026, 9, 13, 3, 0, 0, TimeSpan.Zero), Input = 40 } };
            var proj = Analytics.Projects(rows).Single();
            Equal("未知项目", proj.Project); Equal(70L, proj.Total); Equal(3, proj.Sessions);
        });
        Test("项目排行默认按 Token 总量降序", () => {
            var rows = new[] {
                new Usage { Key = "a", Platform = "Codex", Session = "s1", Project = "E:/小项目",
                    Time = new DateTimeOffset(2026, 9, 13, 1, 0, 0, TimeSpan.Zero), Input = 10 },
                new Usage { Key = "b", Platform = "Codex", Session = "s2", Project = "E:/大项目",
                    Time = new DateTimeOffset(2026, 9, 13, 2, 0, 0, TimeSpan.Zero), Input = 90 } };
            var proj = Analytics.Projects(rows);
            Equal(2, proj.Count); Equal("大项目", proj[0].Project); Equal("小项目", proj[1].Project);
        });
        Test("项目命中率无输入显示破折号否则按 Token 加权", () => {
            var zero = Analytics.Projects(new[] { new Usage { Key = "a", Platform = "Codex", Session = "s1",
                Project = "E:/x", Time = new DateTimeOffset(2026, 9, 13, 1, 0, 0, TimeSpan.Zero), Output = 10 } }).Single();
            Equal(true, zero.HitRateValue == null); Equal("—", zero.HitRate);
            var one = Analytics.Projects(new[] { new Usage { Key = "b", Platform = "Codex", Session = "s2",
                Project = "E:/x", Time = new DateTimeOffset(2026, 9, 13, 2, 0, 0, TimeSpan.Zero), Input = 10, CacheRead = 90, Output = 5 } }).Single();
            Equal((0.9).ToString("P1"), one.HitRate); Equal(0.9, one.HitRateValue.Value);
        });
        Test("跨平台同名会话按平台去重计数", () => {
            var rows = new[] {
                new Usage { Key = "a", Platform = "Codex", Session = "s1", Project = "E:/同一项目",
                    Time = new DateTimeOffset(2026, 9, 13, 1, 0, 0, TimeSpan.Zero), Input = 10 },
                new Usage { Key = "b", Platform = "Claude Code", Session = "s1", Project = "E:/同一项目",
                    Time = new DateTimeOffset(2026, 9, 13, 2, 0, 0, TimeSpan.Zero), Input = 20 } };
            var proj = Analytics.Projects(rows).Single();
            Equal("同一项目", proj.Project); Equal(2, proj.Sessions); Equal(30L, proj.Total);
        });
        Console.WriteLine(String.Format("\n测试结果：{0} 项通过，{1} 项失败。", passed, failed));
        return failed == 0 ? 0 : 1;
    }
}
