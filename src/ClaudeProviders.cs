using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AI_Assistant
{
    public sealed class ClaudeProvider : ProviderConfig { }

    public sealed class ClaudeProviders : ProviderConfigStore
    {
        public const string Template = "{\n  \"$schema\": \"https://json.schemastore.org/claude-code-settings.json\",\n  \"env\": {},\n  \"model\": \"sonnet\"\n}\n";

        public ClaudeProviders(string claudeRoot, string configDirectory)
            : base(claudeRoot, configDirectory, MakeSpec()) { }

        protected override ProviderConfig Create(string name, string json)
        { return new ClaudeProvider { Name = name, Json = json }; }

        static ProviderSpec MakeSpec()
        {
            return new ProviderSpec {
                PlatformName = "Claude Code",
                ConfigExtension = ".json",
                SettingsFileName = "settings.json",
                Template = Template,
                EditorAutomationName = "Claude 官方配置 JSON",
                NameFieldAutomationName = "配置名称（保存为同名 JSON 文件）",
                FormatHint = "按 Claude 官方 settings.json 格式填写完整内容。启用时完整重写，原配置另存备份。",
                ValidatePassedMessage = "JSON 语法与常用字段类型检查通过；具体配置项以 Claude 官方文档为准。",
                Validator = text => { Validate(text); return new List<string>(); },
                Formatter = Pretty
            };
        }

        // 界面保存时把 JSON 重排为两空格缩进的易读格式：只调整空白，键序、字符串转义与数字原文一律保留；
        // 内容无法完整识别（结构不完整或异常）时原样返回，交由校验器按原文报错。重排结果再次重排保持不变。
        public static string Pretty(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return json;
            var output = new StringBuilder();
            int depth = 0;
            for (int i = 0; i < json.Length; )
            {
                char c = json[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                if (c == '"')
                {
                    output.Append('"'); i++;
                    while (i < json.Length)
                    {
                        char s = json[i++]; output.Append(s);
                        if (s == '\\' && i < json.Length) { output.Append(json[i]); i++; }
                        else if (s == '"') break;
                    }
                    continue;
                }
                if (c == '{' || c == '[')
                {
                    char close = c == '{' ? '}' : ']';
                    int j = i + 1;
                    while (j < json.Length && " \t\r\n".IndexOf(json[j]) >= 0) j++;
                    if (j < json.Length && json[j] == close) { output.Append(c).Append(close); i = j + 1; continue; }
                    depth++;
                    output.Append(c).Append('\n');
                    Indent(output, depth);
                    i++;
                    continue;
                }
                if (c == '}' || c == ']')
                {
                    if (--depth < 0) return json;
                    output.Append('\n');
                    Indent(output, depth);
                    output.Append(c);
                    i++;
                    continue;
                }
                if (c == ',') { output.Append(",\n"); Indent(output, depth); i++; continue; }
                if (c == ':') { output.Append(": "); i++; continue; }
                int start = i;
                while (i < json.Length && " \t\r\n,{}[]:".IndexOf(json[i]) < 0) i++;
                if (i == start) return json;
                output.Append(json, start, i - start);
            }
            if (depth != 0) return json;
            return output.ToString() + "\n";
        }

        static void Indent(StringBuilder output, int depth)
        {
            for (int i = 0; i < depth; i++) output.Append("  ");
        }

        public static void Validate(string text)
        {
            try
            {
                new JsonSyntax(text).Check();
                var root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                if (root == null) throw new InvalidOperationException("配置必须是完整的 JSON 对象。");
                if (root.ContainsKey(".env")) throw new InvalidOperationException("Claude 官方配置的环境变量字段名是 env，请将 .env 改为 env。");
                object value;
                if (root.TryGetValue("env", out value))
                {
                    var env = value as Dictionary<string, object>;
                    if (env == null || env.Values.Any(v => !(v is string)))
                        throw new InvalidOperationException("env 必须是对象，且每个环境变量的值必须是字符串。");
                    if (env.ContainsKey("ANTOROPIC_BASE_URL"))
                        throw new InvalidOperationException("ANTOROPIC_BASE_URL 拼写错误，请使用 ANTHROPIC_BASE_URL；正确字段已存在时删除错误字段。");
                }
                foreach (string key in new[] { "permissions", "hooks" })
                    if (root.TryGetValue(key, out value) && !(value is Dictionary<string, object>))
                        throw new InvalidOperationException(key + " 必须是 JSON 对象。");
                foreach (string key in new[] { "$schema", "model" })
                    if (root.TryGetValue(key, out value) && !(value is string))
                        throw new InvalidOperationException(key + " 必须是字符串。");
            }
            catch (ArgumentException) { throw new InvalidOperationException("JSON 格式无效，请按 Claude 官方 settings.json 格式填写。"); }
        }

        // Framework 的 JSON 反序列化器接受单引号和尾随逗号，先按标准 JSON 语法校验。
        sealed class JsonSyntax
        {
            readonly string text;
            int position;
            public JsonSyntax(string text) { this.text = text ?? ""; }
            public void Check()
            {
                Value(0); Space();
                if (position != text.Length) Fail();
            }
            void Space() { while (position < text.Length && " \t\r\n".IndexOf(text[position]) >= 0) position++; }
            bool Take(char value)
            {
                Space();
                if (position >= text.Length || text[position] != value) return false;
                position++; return true;
            }
            void Require(char value) { if (!Take(value)) Fail(); }
            string String()
            {
                Space();
                // 从当前位置匹配，避免将后面的字符串误判成当前属性名。
                var match = new Regex("\\G\"(?:[^\"\\\\\\x00-\\x1F]|\\\\(?:[\"\\\\/bfnrt]|u[0-9a-fA-F]{4}))*\"").Match(text, position);
                if (!match.Success) Fail();
                position += match.Length;
                return new JavaScriptSerializer().Deserialize<string>(match.Value);
            }
            void Value(int depth)
            {
                if (depth > 100) Fail();
                Space();
                if (Take('{'))
                {
                    if (Take('}')) return;
                    var names = new HashSet<string>();
                    do { if (!names.Add(String())) Fail(); Require(':'); Value(depth + 1); } while (Take(','));
                    Require('}'); return;
                }
                if (Take('['))
                {
                    if (Take(']')) return;
                    do { Value(depth + 1); } while (Take(','));
                    Require(']'); return;
                }
                if (position < text.Length && text[position] == '"') { String(); return; }
                var match = new Regex(@"\G(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)").Match(text, position);
                if (!match.Success) Fail();
                position += match.Length;
            }
            void Fail() { throw new InvalidOperationException("JSON 语法无效（位置 " + (position + 1) + "）。请使用双引号，去掉注释、尾随逗号和重复字段。"); }
        }
    }
}
