using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AI_Assistant
{
    public sealed class Usage
    {
        public string Key, Platform, Session, Project, Model;
        public DateTimeOffset Time;
        public long Input, CacheRead, CacheWrite, Output;
        public long UserRequests, ToolCalls;
        public long AllInput { get { return Input + CacheRead + CacheWrite; } }
        public long Total { get { return AllInput + Output; } }
    }

    public sealed class ScanResult
    {
        public List<Usage> Records = new List<Usage>();
        public List<string> Warnings = new List<string>();
        public int Files, InvalidLines;
        public void Warn(string message) { if (!Warnings.Contains(message)) Warnings.Add(message); }
    }

    public sealed class SourcePaths
    {
        public string Codex { get; set; }
        public string Claude { get; set; }
        public static SourcePaths Defaults()
        {
            string home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (String.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string codex = Environment.GetEnvironmentVariable("CODEX_HOME");
            string claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return new SourcePaths {
                Codex = String.IsNullOrWhiteSpace(codex) ? Path.Combine(home, ".codex") : codex,
                Claude = String.IsNullOrWhiteSpace(claude) ? Path.Combine(home, ".claude") : claude
            };
        }
    }

    public static class UsageReader
    {
        static readonly Dictionary<string, object> Empty = new Dictionary<string, object>();
        static Dictionary<string, object> Map(object value)
        { return value as Dictionary<string, object> ?? Empty; }
        static object Get(Dictionary<string, object> obj, string key)
        { object value; return obj.TryGetValue(key, out value) ? value : null; }
        static Dictionary<string, object> Child(Dictionary<string, object> obj, string key)
        { return Map(Get(obj, key)); }
        static string Str(Dictionary<string, object> obj, string key, string fallback)
        { return Get(obj, key) as string ?? fallback; }
        static long Number(Dictionary<string, object> obj, string key)
        {
            object value = Get(obj, key);
            if (!(value is int) && !(value is long) && !(value is decimal)) return 0;
            long number;
            return Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out number) && number > 0 ? number : 0;
        }
        static long[] Counters(Dictionary<string, object> obj)
        {
            return new long[] { Number(obj, "input_tokens"), Number(obj, "cached_input_tokens"),
                Number(obj, "cache_write_input_tokens"), Number(obj, "output_tokens") };
        }
        static System.Collections.IEnumerable List(Dictionary<string, object> obj, string key)
        { return Get(obj, key) as System.Collections.IEnumerable; }
        static bool Flag(Dictionary<string, object> obj, string key)
        { object value = Get(obj, key); return value is bool && (bool)value; }
        static bool IsEnvironmentContext(Dictionary<string, object> payload)
        {
            var blocks = List(payload, "content");
            if (blocks == null) return false;
            foreach (object block in blocks)
                return Str(Map(block), "text", "").StartsWith("<environment_context>");
            return false;
        }

        public static List<Usage> Parse(TextReader reader, string platform, string fileKey, ScanResult report)
        {
            var records = new List<Usage>();
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            string session = fileKey, project = "未知项目", model = "未记录模型";
            long[] previous = null;
            string line;
            int lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (String.IsNullOrWhiteSpace(line)) continue;
                // 预检：先做廉价的子串判断，跳过肯定无产出的行，避免对整行做完整 JSON 解析。
                // 这些子串是所有有效行的必要条件（值文本以引号包裹原样出现，与键值间空格无关），
                // 不含即整行无用；宁可多解析几行（假阳性不影响结果），也不能漏（假阴性会丢数据）。
                bool relevant;
                if (platform == "Codex")
                    relevant = line.IndexOf("session_meta", StringComparison.Ordinal) >= 0
                        || line.IndexOf("turn_context", StringComparison.Ordinal) >= 0
                        || line.IndexOf("token_count", StringComparison.Ordinal) >= 0
                        || (line.IndexOf("response_item", StringComparison.Ordinal) >= 0
                            && (line.IndexOf("\"user\"", StringComparison.Ordinal) >= 0
                                || line.IndexOf("\"function_call\"", StringComparison.Ordinal) >= 0
                                || line.IndexOf("\"custom_tool_call\"", StringComparison.Ordinal) >= 0));
                else
                    relevant = line.IndexOf("\"user\"", StringComparison.Ordinal) >= 0
                        || line.IndexOf("\"assistant\"", StringComparison.Ordinal) >= 0
                        || line.IndexOf("\"usage\"", StringComparison.Ordinal) >= 0;
                if (!relevant) continue;
                Dictionary<string, object> obj;
                try { obj = Map(json.DeserializeObject(line)); }
                catch (ArgumentException) { report.InvalidLines++; continue; }
                catch (InvalidOperationException) { report.InvalidLines++; continue; }
                var payload = Child(obj, "payload");
                string type = Str(obj, "type", "");
                if (platform == "Codex")
                {
                    if (type == "session_meta")
                    {
                        session = Str(payload, "id", session);
                        project = Str(payload, "cwd", project);
                        continue;
                    }
                    if (type == "turn_context")
                    {
                        model = Str(payload, "model", model);
                        project = Str(payload, "cwd", project);
                        continue;
                    }
                    if (type == "response_item")
                    {
                        // 用户消息与工具调用标记为零 Token 记录，Key 不含文件路径，归档副本经 Deduplicate 归一。
                        string itemType = Str(payload, "type", "");
                        bool isUserMessage = itemType == "message" && Str(payload, "role", "") == "user" && !IsEnvironmentContext(payload);
                        bool isToolCall = itemType == "function_call" || itemType == "custom_tool_call";
                        if (!isUserMessage && !isToolCall) continue;
                        DateTimeOffset itemTime;
                        if (!TryTime(obj, out itemTime)) { report.InvalidLines++; continue; }
                        records.Add(new Usage {
                            Key = "Codex|" + session + "|" + itemType + "|" + itemTime.ToString("o"),
                            Platform = platform, Session = session, Project = project, Model = "", Time = itemTime,
                            UserRequests = isUserMessage ? 1 : 0, ToolCalls = isToolCall ? 1 : 0
                        });
                        continue;
                    }
                    if (type != "event_msg" || Str(payload, "type", "") != "token_count") continue;
                    var info = Child(payload, "info");
                    var total = Child(info, "total_token_usage");
                    if (total.Count == 0) continue;
                    long[] current = Counters(total);
                    long[] delta = (long[])current.Clone();
                    if (previous != null)
                    {
                        if (current.SequenceEqual(previous)) continue;
                        if (current.Where((value, i) => value < previous[i]).Any())
                        {
                            var last = Child(info, "last_token_usage");
                            if (last.Count == 0)
                            {
                                previous = current;
                                report.Warn("Codex 累计计数发生回退且缺少单次用量；该条未计入，请核对原始日志。");
                                continue;
                            }
                            delta = Counters(last);
                        }
                        else for (int i = 0; i < 4; i++) delta[i] -= previous[i];
                    }
                    previous = current;
                    DateTimeOffset timestamp;
                    if (!TryTime(obj, out timestamp)) { report.InvalidLines++; continue; }
                    long cached = Math.Min(delta[0], delta[1]);
                    long written = Math.Min(delta[0] - cached, delta[2]);
                    var item = new Usage {
                        Key = "Codex|" + session + "|" + timestamp.ToString("o") + "|" + String.Join(",", current),
                        Platform = platform, Session = session, Project = project, Model = model, Time = timestamp,
                        Input = delta[0] - cached - written, CacheRead = cached, CacheWrite = written, Output = delta[3]
                    };
                    if (item.Total > 0) records.Add(item);
                }
                else
                {
                    if (type == "user")
                    {
                        // 只统计真实用户消息：排除元消息、压缩摘要、子代理 prompt 及工具结果回传。
                        if (Flag(obj, "isMeta") || Flag(obj, "isCompactSummary") || Flag(obj, "isSidechain")) continue;
                        var userMessage = Child(obj, "message");
                        bool hasText = false, hasToolResult = false;
                        object userContent = Get(userMessage, "content");
                        if (userContent is string) hasText = ((string)userContent).Trim().Length > 0;
                        else
                        {
                            var userBlocks = List(userMessage, "content");
                            if (userBlocks != null)
                                foreach (object block in userBlocks)
                                {
                                    string blockType = Str(Map(block), "type", "");
                                    if (blockType == "text") hasText = true;
                                    else if (blockType == "tool_result") hasToolResult = true;
                                }
                        }
                        if (!hasText || hasToolResult) continue;
                        DateTimeOffset userTime;
                        if (!TryTime(obj, out userTime)) { report.InvalidLines++; continue; }
                        string userSession = Str(obj, "sessionId", session);
                        records.Add(new Usage {
                            Key = "Claude Code|" + userSession + "|user|" + Str(obj, "uuid", fileKey + ":" + lineNumber),
                            Platform = platform, Session = userSession, Project = Str(obj, "cwd", project),
                            Model = "", Time = userTime, UserRequests = 1
                        });
                        continue;
                    }
                    if (type != "assistant") continue;
                    var message = Child(obj, "message");
                    var usage = Child(message, "usage");
                    if (usage.Count == 0) continue;
                    DateTimeOffset timestamp;
                    if (!TryTime(obj, out timestamp)) { report.InvalidLines++; continue; }
                    string messageId = Str(message, "id", Str(obj, "uuid", fileKey + ":" + lineNumber));
                    string requestId = Str(obj, "requestId", "");
                    string sessionId = Str(obj, "sessionId", session);
                    long toolCalls = 0;
                    var contentBlocks = List(message, "content");
                    if (contentBlocks != null)
                        foreach (object block in contentBlocks)
                            if (Str(Map(block), "type", "") == "tool_use") toolCalls++;
                    var item = new Usage {
                        Key = "Claude Code|" + sessionId + "|" + messageId + "|" + requestId,
                        Platform = platform, Session = sessionId, Project = Str(obj, "cwd", project),
                        Model = Str(message, "model", "未记录模型"), Time = timestamp,
                        Input = Number(usage, "input_tokens"), CacheRead = Number(usage, "cache_read_input_tokens"),
                        CacheWrite = Number(usage, "cache_creation_input_tokens"), Output = Number(usage, "output_tokens"),
                        ToolCalls = toolCalls
                    };
                    if (item.Total > 0 || item.ToolCalls > 0) records.Add(item);
                }
            }
            return records;
        }

        static bool TryTime(Dictionary<string, object> obj, out DateTimeOffset time)
        {
            return DateTimeOffset.TryParse(Str(obj, "timestamp", ""), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out time);
        }

        public static List<Usage> Deduplicate(IEnumerable<Usage> records)
        {
            var unique = new Dictionary<string, Usage>();
            foreach (var item in records)
            {
                Usage existing;
                if (!unique.TryGetValue(item.Key, out existing)) unique[item.Key] = item;
                else
                {
                    // 同一响应的流式快照取各字段最大值，避免重复累加或丢失最终输出。
                    existing.Input = Math.Max(existing.Input, item.Input);
                    existing.CacheRead = Math.Max(existing.CacheRead, item.CacheRead);
                    existing.CacheWrite = Math.Max(existing.CacheWrite, item.CacheWrite);
                    existing.Output = Math.Max(existing.Output, item.Output);
                    existing.UserRequests = Math.Max(existing.UserRequests, item.UserRequests);
                    existing.ToolCalls = Math.Max(existing.ToolCalls, item.ToolCalls);
                }
            }
            return unique.Values.OrderBy(x => x.Time).ToList();
        }

        // 待解析的日志文件：平台标签随目录确定。
        sealed class ScanTarget
        {
            public string Path, Platform;
        }

        public static ScanResult Scan(SourcePaths paths, Action<int> progress)
        {
            var result = new ScanResult();
            var targets = new List<ScanTarget>();
            CollectDirectory(Path.Combine(paths.Codex, "sessions"), "Codex", result, targets, true);
            CollectDirectory(Path.Combine(paths.Codex, "archived_sessions"), "Codex", result, targets, false);
            CollectDirectory(Path.Combine(paths.Claude, "projects"), "Claude Code", result, targets, true);
            ParseFiles(targets, result, progress);
            result.Records = Deduplicate(result.Records);
            if (result.InvalidLines > 0)
                result.Warn(String.Format("跳过 {0:N0} 条无法解析的记录（可能正在写入），刷新后会重新读取。", result.InvalidLines));
            return result;
        }

        // 只收集待解析文件；目录遍历本身很快，瓶颈在逐行 JSON 解析，交给 ParseFiles 并行处理。
        static void CollectDirectory(string root, string platform, ScanResult result, List<ScanTarget> targets, bool required)
        {
            if (!Directory.Exists(root))
            {
                if (required) result.Warn(platform + " 日志目录不存在，请在“数据源”中检查路径。");
                return;
            }
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                string[] files, children;
                try
                {
                    files = Directory.GetFiles(directory, "*.jsonl");
                    children = Directory.GetDirectories(directory);
                }
                catch (UnauthorizedAccessException) { result.Warn(platform + " 部分目录无读取权限。"); continue; }
                catch (IOException) { result.Warn(platform + " 部分目录暂时无法读取。"); continue; }
                foreach (string child in children)
                {
                    try { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child); }
                    catch (IOException) { result.Warn(platform + " 部分目录已变更，请刷新重试。"); }
                    catch (UnauthorizedAccessException) { result.Warn(platform + " 部分目录无读取权限。"); }
                }
                foreach (string file in files)
                    targets.Add(new ScanTarget { Path = file, Platform = platform });
            }
        }

        // 文件之间互不依赖，按文件并行解析；结果仍按收集顺序合并，保持与顺序扫描一致的确定性。
        static void ParseFiles(List<ScanTarget> targets, ScanResult result, Action<int> progress)
        {
            var parts = new List<Usage>[targets.Count];
            var warnings = new List<string>[targets.Count];
            var invalid = new int[targets.Count];
            int done = 0;
            Parallel.For(0, targets.Count, i =>
            {
                var local = new ScanResult();
                try
                {
                    using (var stream = new FileStream(targets[i].Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true, 65536))
                        parts[i] = Parse(reader, targets[i].Platform, targets[i].Path, local);
                }
                catch (UnauthorizedAccessException) { local.Warn(targets[i].Platform + " 部分日志无读取权限。"); }
                catch (IOException) { local.Warn(targets[i].Platform + " 部分日志暂时无法读取，请刷新重试。"); }
                warnings[i] = local.Warnings;
                invalid[i] = local.InvalidLines;
                if (parts[i] != null)
                {
                    int current = Interlocked.Increment(ref done);
                    if (progress != null && current % 20 == 0) progress(current);
                }
            });
            for (int i = 0; i < targets.Count; i++)
            {
                result.InvalidLines += invalid[i];
                foreach (string warning in warnings[i]) result.Warn(warning);
                if (parts[i] == null) continue;
                result.Files++;
                result.Records.AddRange(parts[i]);
            }
        }
    }

    public sealed class Totals
    {
        public long Input, CacheRead, CacheWrite, Output;
        public long UserRequests, ToolCalls;
        public int Sessions;
        public long AllInput { get { return Input + CacheRead + CacheWrite; } }
        public long Total { get { return AllInput + Output; } }
        public string HitRate { get { return AllInput == 0 ? "—" : ((double)CacheRead / AllInput).ToString("P1"); } }
        public static Totals From(IEnumerable<Usage> source)
        {
            var items = source.ToList();
            return new Totals { Input = items.Sum(x => x.Input), CacheRead = items.Sum(x => x.CacheRead),
                CacheWrite = items.Sum(x => x.CacheWrite), Output = items.Sum(x => x.Output),
                UserRequests = items.Sum(x => x.UserRequests), ToolCalls = items.Sum(x => x.ToolCalls),
                Sessions = items.Select(x => x.Platform + "|" + x.Session).Distinct().Count() };
        }
    }

    public sealed class SessionRow
    {
        public string Platform { get; set; }
        public string Project { get; set; }
        public string ProjectPath { get; set; }
        public string Model { get; set; }
        public string Session { get; set; }
        public DateTime Last { get; set; }
        public long Total { get; set; }
        public long Input { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }
        public long Output { get; set; }
        public long UserRequests { get; set; }
        public long ToolCalls { get; set; }
        public string HitRate { get; set; }
        public double? HitRateValue { get { return Input + CacheRead + CacheWrite == 0 ? (double?)null : (double)CacheRead / (Input + CacheRead + CacheWrite); } }
        public string LastText { get { return Last.ToString("MM-dd HH:mm"); } }
        public string TotalText { get { return Total.ToString("N0"); } }
    }

    public sealed class ProjectRow
    {
        public string Project { get; set; }
        public string ProjectPath { get; set; }
        public DateTime Last { get; set; }
        public int Sessions { get; set; }
        public long Total { get; set; }
        public long Input { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }
        public long Output { get; set; }
        public long UserRequests { get; set; }
        public long ToolCalls { get; set; }
        public string HitRate { get; set; }
        public double? HitRateValue { get { return Input + CacheRead + CacheWrite == 0 ? (double?)null : (double)CacheRead / (Input + CacheRead + CacheWrite); } }
        public string LastText { get { return Last.ToString("MM-dd HH:mm"); } }
        public string TotalText { get { return Total.ToString("N0"); } }
    }

    public static class Analytics
    {
        public static List<Usage> Filter(IEnumerable<Usage> source, string platform, DateTime? start, DateTime end, string query)
        {
            // 路径分隔符统一为正斜杠：Codex 日志里的项目路径是反斜杠，Claude 是正斜杠，
            // 项目页双击跳转会把组内某条记录的原始路径带进搜索框，不归一化则漏掉另一平台的会话。
            query = (query ?? "").Trim().Replace('\\', '/');
            return source.Where(x => (platform == "全部平台" || x.Platform == platform)
                && (!start.HasValue || x.Time.LocalDateTime >= start.Value)
                // 包含结束时间的整个秒，保留日志中带毫秒的边界记录。
                && x.Time.LocalDateTime.Ticks / TimeSpan.TicksPerSecond <= end.Ticks / TimeSpan.TicksPerSecond
                && (query.Length == 0 || (x.Project + " " + x.Model + " " + x.Session).Replace('\\', '/')
                    .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }
        public static List<SessionRow> Sessions(IEnumerable<Usage> source)
        {
            return source.GroupBy(x => x.Platform + "|" + x.Session).Select(group => {
                var last = group.OrderBy(x => x.Time).Last();
                var total = Totals.From(group);
                // 用户请求标记记录不带模型，过滤空值避免拼入模型列表。
                var models = group.Select(x => x.Model).Where(m => !String.IsNullOrEmpty(m)).Distinct().ToList();
                return new SessionRow { Platform = last.Platform, Session = last.Session, ProjectPath = last.Project,
                    Project = last.Project.TrimEnd('\\', '/').Split('\\', '/').Last(),
                    Model = models.Count > 0 ? String.Join(" / ", models) : "未记录模型", Last = last.Time.LocalDateTime,
                    Total = total.Total, Input = total.Input, CacheRead = total.CacheRead, CacheWrite = total.CacheWrite,
                    Output = total.Output, UserRequests = total.UserRequests, ToolCalls = total.ToolCalls, HitRate = total.HitRate };
            }).OrderByDescending(x => x.Last).ToList();
        }
        // 按项目聚合：跨平台同目录合并为一行，默认按 Token 总量降序（Top 排行）。
        public static List<ProjectRow> Projects(IEnumerable<Usage> source)
        {
            return source.GroupBy(x => NormalizeProject(x.Project)).Select(group => {
                var last = group.OrderBy(x => x.Time).Last();
                var total = Totals.From(group);
                return new ProjectRow { Project = Basename(last.Project), ProjectPath = last.Project,
                    Last = last.Time.LocalDateTime, Sessions = total.Sessions,
                    Total = total.Total, Input = total.Input, CacheRead = total.CacheRead,
                    CacheWrite = total.CacheWrite, Output = total.Output,
                    UserRequests = total.UserRequests, ToolCalls = total.ToolCalls,
                    HitRate = total.HitRate };
            }).OrderByDescending(x => x.Total).ToList();
        }
        // 分组键：空值归入“未知项目”，去尾部分隔符、统一分隔符、大小写归一，避免同一目录拆成多行。
        static string NormalizeProject(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "未知项目";
            return path.TrimEnd('\\', '/').Replace('\\', '/').ToLowerInvariant();
        }
        // 显示名：取路径最后一段，保留原始大小写；空值显示“未知项目”。
        static string Basename(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "未知项目";
            return path.TrimEnd('\\', '/').Split('\\', '/').Last();
        }
    }
}
