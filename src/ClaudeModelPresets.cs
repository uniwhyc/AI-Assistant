using System;

namespace AI_Assistant
{
    public sealed class ClaudeModelPreset
    {
        public string Name { get; private set; }
        public string Address { get; private set; }
        public string Source { get; private set; }
        public string[] ReferenceModels { get; private set; }
        public bool IsReference { get { return ReferenceModels != null; } }
        public override string ToString() { return Name; }

        ClaudeModelPreset(string name, string address, string source, params string[] models)
        {
            Name = name; Address = address; Source = source;
            ReferenceModels = models.Length == 0 ? null : models;
        }

        // 套餐参考名称核对于 2026-09-14，不代表实时账户权限或完整模型清单。
        public static readonly ClaudeModelPreset[] All = {
            new ClaudeModelPreset("自定义服务", "", ""),
            new ClaudeModelPreset("Anthropic · Claude", "https://api.anthropic.com/v1/models", "https://platform.claude.com/docs/en/api/models/list"),
            new ClaudeModelPreset("OpenAI", "https://api.openai.com/v1/models", "https://developers.openai.com/api/reference/resources/models/methods/list"),
            new ClaudeModelPreset("Google · Gemini", "https://generativelanguage.googleapis.com/v1beta/models", "https://ai.google.dev/api/models"),
            new ClaudeModelPreset("DeepSeek", "https://api.deepseek.com/models", "https://api-docs.deepseek.com/api/list-models"),
            new ClaudeModelPreset("Moonshot · Kimi 按量 API", "https://api.moonshot.cn/v1/models", "https://platform.moonshot.cn/docs/api/models"),
            new ClaudeModelPreset("阿里云百炼 · 按量 API", "https://dashscope.aliyuncs.com/compatible-mode/v1/models", "https://help.aliyun.com/zh/model-studio/compatibility-of-openai-with-dashscope"),
            new ClaudeModelPreset("硅基流动 · SiliconFlow", "https://api.siliconflow.cn/v1/models", "https://docs.siliconflow.cn/"),
            new ClaudeModelPreset("OpenRouter", "https://openrouter.ai/api/v1/models", "https://openrouter.ai/docs/api-reference/models/get-models"),
            new ClaudeModelPreset("智谱 GLM Coding Plan", "https://open.bigmodel.cn/api/anthropic", "https://docs.bigmodel.cn/cn/coding-plan/tool/claude", "glm-5.3[1m]", "glm-5.3-flash[1m]"),
            new ClaudeModelPreset("Z.ai GLM Coding Plan", "https://api.z.ai/api/anthropic", "https://docs.z.ai/devpack/tool/claude", "glm-5.3[1m]", "glm-5.3-flash[1m]"),
            new ClaudeModelPreset("Kimi Code", "https://api.kimi.com/coding/", "https://www.kimi.com/code/docs/", "kimi-for-coding", "kimi-for-coding-highspeed"),
            new ClaudeModelPreset("MiniMax Coding Plan · 国内", "https://api.minimax.cn/anthropic", "https://platform.minimax.io/docs/coding-plan/claude-code", "MiniMax-M3[1m]"),
            new ClaudeModelPreset("MiniMax Coding Plan · 国际", "https://api.minimax.io/anthropic", "https://platform.minimax.io/docs/coding-plan/claude-code", "MiniMax-M3[1m]"),
            new ClaudeModelPreset("阿里云 Coding Plan · 国内", "https://coding.dashscope.aliyuncs.com/apps/anthropic", "https://help.aliyun.com/zh/model-studio/coding-plan",
                "qwen3.7-plus", "qwen3.6-plus", "kimi-k2.5", "glm-5", "MiniMax-M2.5", "qwen3.5-plus", "qwen3-max-2026-01-23", "qwen3-coder-next", "qwen3-coder-plus", "glm-4.7"),
            new ClaudeModelPreset("阿里云 Coding Plan · 国际", "https://coding-intl.dashscope.aliyuncs.com/apps/anthropic", "https://www.alibabacloud.com/help/en/model-studio/coding-plan",
                "qwen3.7-plus", "qwen3.6-plus", "kimi-k2.5", "glm-5", "MiniMax-M2.5", "qwen3.5-plus", "qwen3-max-2026-01-23", "qwen3-coder-next", "qwen3-coder-plus", "glm-4.7"),
            new ClaudeModelPreset("火山方舟 Coding Plan", "https://ark.cn-beijing.volces.com/api/coding", "https://github.com/farion1231/cc-switch/blob/main/src/config/claudeProviderPresets.ts", "ark-code-latest")
        };
    }
}
