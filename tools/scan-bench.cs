using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AI_Assistant;

// 扫描性能基准：在沙箱目录里合成接近真实规模与构成的 Codex / Claude 日志，测量 UsageReader.Scan 的耗时。
// 用法：
//   scan-bench gen <root> <claudeFiles> <claudeKB> <codexFiles> <codexKB>
//   scan-bench run <root> <times>   （times 为全量扫描次数；另附各平台单独计时）
public static class ScanBench
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 0) { Console.WriteLine("用法: gen <root> <claudeFiles> <claudeKB> <codexFiles> <codexKB> | run <root> <times>"); return 2; }
            if (args[0] == "gen") return Generate(args[1], Int32.Parse(args[2]), Int32.Parse(args[3]), Int32.Parse(args[4]), Int32.Parse(args[5]));
            if (args[0] == "run") return Measure(args[1], Int32.Parse(args[2]));
            Console.WriteLine("未知命令：" + args[0]); return 2;
        }
        catch (Exception ex) { Console.Error.WriteLine("基准失败：" + ex); return 1; }
    }

    static int Generate(string root, int claudeFiles, int claudeKB, int codexFiles, int codexKB)
    {
        string claudeRoot = Path.Combine(root, "claude", "projects");
        string codexRoot = Path.Combine(root, "codex", "sessions", "2026", "10");
        string archiveRoot = Path.Combine(root, "codex", "archived_sessions");
        Directory.CreateDirectory(claudeRoot); Directory.CreateDirectory(codexRoot); Directory.CreateDirectory(archiveRoot);
        var random = new Random(20261003);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < claudeFiles; i++)
        {
            string project = Path.Combine(claudeRoot, "E--work--project-" + (i % 40).ToString("D2"));
            Directory.CreateDirectory(project);
            WriteClaudeSession(Path.Combine(project, "session-" + i.ToString("D4") + ".jsonl"), i, claudeKB * 1024, random);
        }
        for (int i = 0; i < codexFiles; i++)
        {
            string dir = (i % 10 == 0) ? archiveRoot : codexRoot;
            WriteCodexSession(Path.Combine(dir, "rollout-2026-10-03T08-00-" + i.ToString("D4") + ".jsonl"), i, codexKB * 1024, random);
        }
        watch.Stop();
        long bytes = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Console.WriteLine("已生成 " + claudeFiles + " 个 Claude 会话 + " + codexFiles + " 个 Codex rollout，共 " + (bytes / 1048576.0).ToString("N1") + " MB，用时 " + watch.ElapsedMilliseconds + " ms");
        return 0;
    }

    static int Measure(string root, int times)
    {
        string claudePath = Path.Combine(root, "claude");
        string codexPath = Path.Combine(root, "codex");
        var files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);
        long totalBytes = files.Sum(f => new FileInfo(f).Length);
        Console.WriteLine("数据规模：" + files.Length + " 个文件，" + (totalBytes / 1048576.0).ToString("N1") + " MB");

        var watch = Stopwatch.StartNew();
        long readBytes = 0;
        var buffer = new byte[65536];
        foreach (string file in files)
            using (var stream = File.OpenRead(file))
            {
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) readBytes += count;
            }
        watch.Stop();
        Console.WriteLine("纯读字节（无解析）：" + watch.ElapsedMilliseconds + " ms，" + (readBytes / 1048576.0).ToString("N1") + " MB");

        var full = new SourcePaths { Codex = codexPath, Claude = claudePath };
        for (int i = 1; i <= times; i++) Run("全量", i, full);
        for (int i = 1; i <= 2; i++) Run("仅 Codex", i, new SourcePaths { Codex = codexPath, Claude = Path.Combine(root, "空-claude") });
        for (int i = 1; i <= 2; i++) Run("仅 Claude", i, new SourcePaths { Codex = Path.Combine(root, "空-codex"), Claude = claudePath });
        return 0;
    }

    static void Run(string label, int round, SourcePaths paths)
    {
        var watch = Stopwatch.StartNew();
        var result = UsageReader.Scan(paths, null);
        watch.Stop();
        Console.WriteLine(label + " 第 " + round + " 次：" + watch.ElapsedMilliseconds + " ms，读取 " + result.Files + " 个文件，得到 " + result.Records.Count + " 条用量记录，警告 " + result.Warnings.Count + " 条");
    }

    static string Stamp(DateTimeOffset time)
    {
        return time.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    static string Filler(int length)
    {
        const string sample = "The quick brown fox jumps over the lazy dog while the token counter keeps rolling forward across the river bank and far beyond. ";
        var builder = new StringBuilder(length + sample.Length);
        while (builder.Length < length) builder.Append(sample);
        return builder.ToString(0, length);
    }

    static void WriteClaudeSession(string path, int index, int targetBytes, Random random)
    {
        var builder = new StringBuilder(targetBytes + 8192);
        string sessionId = "1f0c" + index.ToString("D4") + "-bench-session";
        string cwd = "E:/work/project-" + (index % 40).ToString("D2");
        string model = (index % 3 == 0) ? "claude-opus-5-5" : "claude-sonnet-5-5";
        var time = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero).AddMinutes(index % 1440);
        int turn = 0;
        while (builder.Length < targetBytes)
        {
            turn++;
            time = time.AddSeconds(random.Next(3, 90));
            string stamp = Stamp(time);
            switch (turn % 10)
            {
                case 0: case 1: case 2:
                    builder.Append("{\"type\":\"user\",\"uuid\":\"u-" + index + "-" + turn + "\",\"sessionId\":\"" + sessionId + "\",\"cwd\":\"" + cwd + "\",\"timestamp\":\"" + stamp + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"")
                        .Append(Filler(random.Next(400, 3000))).Append("\"}]}}\n");
                    break;
                case 3: case 4: case 5:
                    builder.Append("{\"type\":\"assistant\",\"uuid\":\"a-" + index + "-" + turn + "\",\"requestId\":\"req_" + index + "_" + turn + "\",\"sessionId\":\"" + sessionId + "\",\"cwd\":\"" + cwd + "\",\"timestamp\":\"" + stamp + "\",\"message\":{\"id\":\"msg_" + index + "_" + turn + "\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":" + random.Next(1, 400) + ",\"cache_read_input_tokens\":" + random.Next(0, 40000) + ",\"cache_creation_input_tokens\":" + random.Next(0, 20000) + ",\"output_tokens\":" + random.Next(20, 3000) + "},\"content\":[{\"type\":\"text\",\"text\":\"")
                        .Append(Filler(random.Next(800, 4000))).Append("\"}");
                    if (turn % 3 == 0) builder.Append(",{\"type\":\"tool_use\",\"id\":\"toolu_" + index + "_" + turn + "\",\"name\":\"Read\",\"input\":{\"file_path\":\"" + cwd + "/file-" + turn + ".cs\"}}");
                    builder.Append("]}}\n");
                    break;
                default:
                    builder.Append("{\"type\":\"progress\",\"uuid\":\"p-" + index + "-" + turn + "\",\"sessionId\":\"" + sessionId + "\",\"timestamp\":\"" + stamp + "\",\"data\":{\"type\":\"hook_progress\",\"payload\":\"")
                        .Append(Filler(random.Next(300, 2500))).Append("\"}}\n");
                    break;
            }
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    static void WriteCodexSession(string path, int index, int targetBytes, Random random)
    {
        var builder = new StringBuilder(targetBytes + 8192);
        string sessionId = "0199bench-" + index.ToString("D4");
        string cwd = "E:/work/project-" + (index % 40).ToString("D2");
        var time = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero).AddMinutes(index % 1440);
        long[] total = new long[4];
        int turn = 0;
        builder.Append("{\"timestamp\":\"" + Stamp(time) + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + sessionId + "\",\"cwd\":\"" + cwd + "\",\"originator\":\"codex_cli_rs\",\"cli_version\":\"0.45.0\"}}\n");
        while (builder.Length < targetBytes)
        {
            turn++;
            time = time.AddSeconds(random.Next(3, 120));
            string stamp = Stamp(time);
            switch (turn % 10)
            {
                case 0:
                    builder.Append("{\"timestamp\":\"" + stamp + "\",\"type\":\"turn_context\",\"payload\":{\"cwd\":\"" + cwd + "\",\"model\":\"gpt-5.1-codex-max\",\"approval_policy\":\"on-request\"}}\n");
                    break;
                case 1:
                    builder.Append("{\"timestamp\":\"" + stamp + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"call_" + index + "_" + turn + "\",\"output\":\"")
                        .Append(Filler(random.Next(5000, 25000))).Append("\"}}\n");
                    break;
                case 2:
                    builder.Append("{\"timestamp\":\"" + stamp + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"")
                        .Append(Filler(random.Next(1000, 6000))).Append("\"}]}}\n");
                    break;
                case 3: case 4:
                    builder.Append("{\"timestamp\":\"" + stamp + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"")
                        .Append(Filler(random.Next(300, 2500))).Append("\"}]}}\n");
                    break;
                case 5:
                    builder.Append("{\"timestamp\":\"" + stamp + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"shell\",\"arguments\":\"ls -la " + cwd + "\",\"call_id\":\"call_" + index + "_" + turn + "\"}}\n");
                    break;
                default:
                    total[0] += random.Next(100, 2000); total[1] += random.Next(0, 3000); total[2] += random.Next(0, 1500); total[3] += random.Next(50, 900);
                    builder.Append("{\"timestamp\":\"" + stamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":" + total[0] + ",\"cached_input_tokens\":" + total[1] + ",\"cache_write_input_tokens\":" + total[2] + ",\"output_tokens\":" + total[3] + "},\"last_token_usage\":{\"input_tokens\":600,\"cached_input_tokens\":900,\"cache_write_input_tokens\":300,\"output_tokens\":150}}}}\n");
                    break;
            }
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }
}
