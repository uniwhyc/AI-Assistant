using System;
using System.Collections.Generic;

namespace AI_Assistant
{
    public sealed class CodexProviders : ProviderConfigStore
    {
        public const string Template = "model = \"gpt-5.1-codex-max\"\nmodel_provider = \"openai\"\napproval_policy = \"on-request\"\nsandbox_mode = \"workspace-write\"\n";

        public CodexProviders(string codexRoot, string configDirectory)
            : base(codexRoot, configDirectory, MakeSpec()) { }

        protected override ProviderConfig Create(string name, string json)
        { return new ProviderConfig { Name = name, Json = json }; }

        static ProviderSpec MakeSpec()
        {
            return new ProviderSpec {
                PlatformName = "Codex",
                ConfigExtension = ".toml",
                SettingsFileName = "config.toml",
                Template = Template,
                EditorAutomationName = "Codex 官方配置 TOML",
                NameFieldAutomationName = "配置名称（保存为同名 TOML 文件）",
                FormatHint = "按 Codex 官方 config.toml 格式填写完整内容。启用时完整重写，原配置另存备份。",
                ValidatePassedMessage = "TOML 基本语法与常用字段类型检查通过；具体配置项以 Codex 官方文档为准。",
                Validator = text => new TomlSyntax(text).Check()
            };
        }

        public static void Validate(string text)
        {
            new TomlSyntax(text).Check();
        }
    }
}
