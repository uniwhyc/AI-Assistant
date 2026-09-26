using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AI_Assistant
{
    // Codex config.toml 的基本语法检查：只做保守的结构与常用字段检查，不实现完整 TOML 规范，
    // 具体配置项以 Codex 官方文档及实际读取结果为准。
    public sealed class TomlSyntax
    {
        readonly string[] lines;

        public TomlSyntax(string text)
        {
            text = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            lines = text.Split('\n');
        }

        // 语法或类型错误抛 InvalidOperationException（"第 N 行：…"）；返回警告列表。
        public List<string> Check()
        {
            var warnings = new List<string>();
            string table = "";
            var keys = new HashSet<string>();
            int arrayDepth = 0;
            int multilineLine = 0;
            char multilineQuote = '"';

            for (int i = 0; i < lines.Length; i++)
            {
                int lineNo = i + 1;
                string line = lines[i];

                if (multilineLine > 0)
                {
                    string closing = multilineQuote == '"' ? "\"\"\"" : "'''";
                    int end = line.IndexOf(closing, StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        // 闭合符之后允许行尾注释。
                        if (!String.IsNullOrWhiteSpace(StripComment(line.Substring(end + 3)))) Fail(lineNo, "三引号字符串闭合后只能有空白。");
                        multilineLine = 0;
                    }
                    continue;
                }
                if (arrayDepth > 0)
                {
                    ScanBrackets(StripComment(line), lineNo, ref arrayDepth);
                    continue;
                }

                string content = StripComment(line);
                if (String.IsNullOrWhiteSpace(content)) continue;

                if (content[0] == '[')
                {
                    bool array = content.Length > 1 && content[1] == '[';
                    string close = array ? "]]" : "]";
                    int closeAt = content.IndexOf(close, array ? 2 : 1, StringComparison.Ordinal);
                    if (closeAt < 0 || !String.IsNullOrWhiteSpace(content.Substring(closeAt + close.Length)))
                        Fail(lineNo, "表头必须单独占一行，以 " + close + " 结束。");
                    string name = content.Substring(array ? 2 : 1, closeAt - (array ? 2 : 1)).Trim();
                    if (!Regex.IsMatch(name, @"^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*$")) Fail(lineNo, "表名无效，只支持点分裸键。");
                    table = name; keys.Clear();
                    continue;
                }

                int eq = FindEquals(content);
                if (eq < 0) Fail(lineNo, "缺少 = 的键值行。");
                string key = content.Substring(0, eq).Trim();
                string value = content.Substring(eq + 1).Trim();
                if (key.Length == 0) Fail(lineNo, "键名不能为空。");
                if (!Regex.IsMatch(key, @"^[A-Za-z0-9_-]+$") && !IsQuotedKey(key)) Fail(lineNo, "键名无效。");
                if (value.Length == 0) Fail(lineNo, "值不能为空。");
                string type = ValueType(value, lineNo, ref multilineLine, ref multilineQuote, ref arrayDepth);
                string fullKey = (table.Length == 0 ? "" : table + ".") + key;
                if (!keys.Add(fullKey)) Fail(lineNo, "重复键：" + key + "。");
                if (table.Length > 0 && (key == "model" || key == "model_provider" || key == "approval_policy" || key == "sandbox_mode"))
                    warnings.Add("第 " + lineNo + " 行：" + key + " 将写入 [" + table + "] 表，不会作为顶层配置生效。");
                CheckField(lineNo, key, value, type, warnings);
            }
            if (multilineLine > 0) Fail(multilineLine, "三引号字符串未闭合。");
            if (arrayDepth > 0) Fail(lines.Length, "数组未闭合。");
            return warnings;
        }

        void CheckField(int lineNo, string key, string value, string type, List<string> warnings)
        {
            if ((key == "model" || key == "model_provider" || key == "approval_policy") && type != "string")
                Fail(lineNo, key + " 必须是字符串。");
            if (key == "sandbox_mode")
            {
                if (type != "string") Fail(lineNo, "sandbox_mode 必须是字符串。");
                string content = value.Length >= 2 ? value.Substring(1, value.Length - 2) : "";
                if (content != "read-only" && content != "workspace-write" && content != "danger-full-access")
                    warnings.Add("第 " + lineNo + " 行：sandbox_mode 通常为 read-only、workspace-write 或 danger-full-access。");
            }
        }

        string ValueType(string value, int lineNo, ref int multilineLine, ref char multilineQuote, ref int arrayDepth)
        {
            if (value.StartsWith("\"\"\"") || value.StartsWith("'''"))
            {
                string closing = value.StartsWith("\"\"\"") ? "\"\"\"" : "'''";
                multilineQuote = closing[0];
                int end = value.IndexOf(closing, 3, StringComparison.Ordinal);
                if (end >= 0)
                {
                    if (!String.IsNullOrWhiteSpace(value.Substring(end + 3))) Fail(lineNo, "三引号字符串闭合后只能有空白。");
                    return "string";
                }
                multilineLine = lineNo; return "string";
            }
            if (value[0] == '"')
            {
                int close = SkipBasicString(value, 1);
                if (close < 0 || !String.IsNullOrWhiteSpace(value.Substring(close + 1))) Fail(lineNo, "字符串未闭合或闭合后有多余内容。");
                return "string";
            }
            if (value[0] == '\'')
            {
                int close = SkipLiteral(value, 1);
                if (close < 0 || !String.IsNullOrWhiteSpace(value.Substring(close + 1))) Fail(lineNo, "单引号字符串未闭合或闭合后有多余内容。");
                return "string";
            }
            if (value[0] == '[')
            {
                ScanBrackets(value, lineNo, ref arrayDepth);
                if (arrayDepth != 0) return "array"; // 多行数组：后续行按括号深度继续扫描
                return "array";
            }
            if (value[0] == '{')
            {
                int depth = 0;
                ScanBraces(value, lineNo, ref depth);
                if (depth != 0) Fail(lineNo, "内联表必须写在同一行。");
                return "table";
            }
            if (value == "true" || value == "false") return "bool";
            if (Regex.IsMatch(value, @"^[+-]?(?:inf|nan)$")) return "number";
            if (Regex.IsMatch(value, @"^[+-]?(?:0x[0-9A-Fa-f](?:_?[0-9A-Fa-f])*|0o[0-7](?:_?[0-7])*|0b[01](?:_?[01])*|\d(?:_?\d)*)(?:\.\d(?:_?\d)*)?(?:[eE][+-]?\d(?:_?\d)*)?$"))
                return "number";
            if (Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}(?:[Tt ].*)?$")) return "date";
            Fail(lineNo, "无法识别的值：" + value + "。");
            return null;
        }

        // 引号外的 [ ] 深度扫描；深度归零后只允许空白。
        void ScanBrackets(string line, int lineNo, ref int depth)
        {
            for (int c = 0; c < line.Length; c++)
            {
                char ch = line[c];
                if (ch == '"')
                {
                    int close = SkipBasicString(line, c + 1);
                    if (close < 0) Fail(lineNo, "字符串未闭合。");
                    c = close;
                }
                else if (ch == '\'')
                {
                    int close = SkipLiteral(line, c + 1);
                    if (close < 0) Fail(lineNo, "字符串未闭合。");
                    c = close;
                }
                else if (ch == '[') depth++;
                else if (ch == ']')
                {
                    depth--;
                    if (depth == 0 && !String.IsNullOrWhiteSpace(line.Substring(c + 1))) Fail(lineNo, "数组闭合后只能有空白。");
                }
            }
        }

        void ScanBraces(string line, int lineNo, ref int depth)
        {
            for (int c = 0; c < line.Length; c++)
            {
                char ch = line[c];
                if (ch == '"') { int close = SkipBasicString(line, c + 1); if (close < 0) Fail(lineNo, "字符串未闭合。"); c = close; }
                else if (ch == '\'') { int close = SkipLiteral(line, c + 1); if (close < 0) Fail(lineNo, "字符串未闭合。"); c = close; }
                else if (ch == '{') depth++;
                else if (ch == '}') depth--;
            }
        }

        // 字符串外的 # 之后是注释；引号内容原样保留。
        static string StripComment(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (i + 2 < line.Length && line[i + 1] == '"' && line[i + 2] == '"')
                    {
                        int end = line.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                        if (end < 0) return line; // 未闭合：本行其余内容属于多行字符串，交给值解析
                        i = end + 2;
                    }
                    else { int close = SkipBasicString(line, i + 1); if (close < 0) return line; i = close; }
                }
                else if (c == '\'')
                {
                    if (i + 2 < line.Length && line[i + 1] == '\'' && line[i + 2] == '\'')
                    {
                        int end = line.IndexOf("'''", i + 3, StringComparison.Ordinal);
                        if (end < 0) return line; // 未闭合：本行其余内容属于多行字符串，交给值解析
                        i = end + 2;
                    }
                    else { int close = SkipLiteral(line, i + 1); if (close < 0) return line; i = close; }
                }
                else if (c == '#') return line.Substring(0, i);
            }
            return line;
        }

        // 返回闭合引号的下标；\ 转义跳过下一字符；未闭合返回 -1。
        static int SkipBasicString(string line, int start)
        {
            for (int i = start; i < line.Length; i++)
            {
                if (line[i] == '\\') { i++; if (i >= line.Length) return -1; }
                else if (line[i] == '"') return i;
            }
            return -1;
        }

        static int SkipLiteral(string line, int start)
        {
            int at = line.IndexOf('\'', start);
            return at;
        }

        static int FindEquals(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { int close = SkipBasicString(line, i + 1); if (close < 0) return -1; i = close; }
                else if (c == '\'') { int close = SkipLiteral(line, i + 1); if (close < 0) return -1; i = close; }
                else if (c == '=') return i;
            }
            return -1;
        }

        static bool IsQuotedKey(string key)
        {
            if (key.Length < 2) return false;
            if (key[0] == '"') { int close = SkipBasicString(key, 1); return close == key.Length - 1; }
            if (key[0] == '\'') { int close = SkipLiteral(key, 1); return close == key.Length - 1; }
            return false;
        }

        static void Fail(int lineNo, string message) { throw new InvalidOperationException("第 " + lineNo + " 行：" + message); }
    }
}
