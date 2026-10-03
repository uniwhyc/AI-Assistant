using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AI_Assistant
{
    public class ProviderConfig
    {
        public string Name { get; set; }
        public string Json { get; set; }
        public override string ToString() { return Name; }
    }

    // 平台描述符：文件名、模板、校验器与界面文案，存储逻辑与平台无关。
    public sealed class ProviderSpec
    {
        public string PlatformName;                  // 如 "Claude Code" / "Codex"
        public string ConfigExtension;               // 如 ".json" / ".toml"
        public string SettingsFileName;              // 如 "settings.json" / "config.toml"
        public string Template;                      // 目标文件不存在时提供的模板全文
        public string EditorAutomationName;          // 编辑框的无障碍名称（测试契约）
        public string NameFieldAutomationName;       // 名称输入框的无障碍名称
        public string FormatHint;                    // 编辑区上方格式说明
        public string ValidatePassedMessage;         // 校验通过提示
        public Func<string, List<string>> Validator; // 语法或类型错误抛 InvalidOperationException；返回警告列表
        public Func<string, string> Formatter;       // 界面保存时重排全文的格式化器；null 表示原样保存
    }

    public abstract class ProviderConfigStore
    {
        public readonly string SettingsPath;
        public readonly string ConfigDirectory;
        public readonly ProviderSpec Spec;
        public string BackupPath { get { return SettingsPath + ".ai-assistant.bak"; } }
        public string OriginalPath { get { return SettingsPath + ".ai-assistant.original.bak"; } }

        protected ProviderConfigStore(string root, string configDirectory, ProviderSpec spec)
        {
            Spec = spec;
            SettingsPath = Path.Combine(Path.GetFullPath(root), spec.SettingsFileName);
            ConfigDirectory = Path.GetFullPath(configDirectory);
        }

        protected abstract ProviderConfig Create(string name, string json);

        public List<ProviderConfig> Load()
        {
            if (!Directory.Exists(ConfigDirectory)) return new List<ProviderConfig>();
            return Directory.GetFiles(ConfigDirectory, "*" + Spec.ConfigExtension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Create(Path.GetFileNameWithoutExtension(p), File.ReadAllText(p))).ToList();
        }

        public string ReadCurrent()
        {
            return File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : Spec.Template;
        }

        // 用户原始配置（首次打开时留档的全文）；尚无留档时回退当前生效文件（其自身再回退模板）。
        public string ReadOriginal()
        {
            return File.Exists(OriginalPath) ? File.ReadAllText(OriginalPath) : ReadCurrent();
        }

        public void Save(ProviderConfig config, bool overwrite = false)
        {
            string path = ConfigPath(config == null ? null : config.Name);
            Spec.Validator(config.Json);
            bool existed = File.Exists(path);
            if (existed && !overwrite) throw new InvalidOperationException("已有同名配置，请选择它进行编辑，或使用其他名称。");
            WriteAtomic(path, Encoding.UTF8.GetBytes(config.Json), path + ".bak");
            // 来源记录独立保存，不向官方配置文件添加自定义字段。
            if (!existed) File.WriteAllText(path + ".ai-assistant-created", "AI_Assistant", Encoding.UTF8);
        }

        public void RenameAndSave(string originalName, ProviderConfig config)
        {
            string original = ConfigPath(originalName);
            string target = ConfigPath(config == null ? null : config.Name);
            Spec.Validator(config.Json);
            if (String.Equals(original, SettingsPath, StringComparison.OrdinalIgnoreCase)
                || String.Equals(target, SettingsPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能重命名 " + Spec.PlatformName + " 当前配置文件。");
            if (!File.Exists(original)) throw new InvalidOperationException("原配置文件已不存在，请重新打开配置窗口。");
            if (originalName == config.Name) { Save(config, true); return; }
            string originalRecord = original + ".ai-assistant-created";
            string targetRecord = target + ".ai-assistant-created";
            bool samePath = String.Equals(original, target, StringComparison.OrdinalIgnoreCase);
            if (!samePath && (File.Exists(target) || File.Exists(targetRecord) || File.Exists(target + ".bak")))
                throw new InvalidOperationException("目标名称的配置或备份已存在，请使用其他名称。");
            bool movedRecord = false;
            File.Move(original, target);
            try
            {
                if (File.Exists(originalRecord)) { File.Move(originalRecord, targetRecord); movedRecord = true; }
                Save(config, true);
            }
            catch
            {
                if (movedRecord) File.Move(targetRecord, originalRecord);
                File.Move(target, original);
                throw;
            }
        }

        public bool CanDelete(string name)
        {
            string path = ConfigPath(name);
            return !String.Equals(path, SettingsPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(path) && File.Exists(path + ".ai-assistant-created");
        }

        public void Delete(string name)
        {
            string path = ConfigPath(name);
            if (!CanDelete(name)) throw new InvalidOperationException("只能删除由 AI Assistant 新建的配置；原有配置或来源不明的配置不可删除。");
            File.Delete(path);
            File.Delete(path + ".ai-assistant-created");
        }

        string ConfigPath(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith(".")
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("请填写有效的配置名称，不能包含路径字符或 Windows 保留名称。");
            return Path.Combine(ConfigDirectory, name + Spec.ConfigExtension);
        }

        public string CurrentName(IList<ProviderConfig> configs)
        {
            if (!File.Exists(SettingsPath)) return "尚无配置文件";
            string current = File.ReadAllText(SettingsPath);
            var match = configs.FirstOrDefault(p => p.Json == current);
            return match == null ? "当前文件（未匹配已保存配置）" : match.Name;
        }

        // 软件启动或首次打开配置界面时调用：把当时的生效文件留档为原配置；留档只创建一次，之后永不被覆盖。
        public void PreserveOriginal()
        {
            if (File.Exists(SettingsPath) && !File.Exists(OriginalPath)) File.Copy(SettingsPath, OriginalPath, false);
        }

        // 有留档、且当前生效文件已偏离留档内容时才需要恢复；当前文件缺失同样视为可恢复。
        public bool CanRestore()
        {
            if (!File.Exists(OriginalPath)) return false;
            try { return !File.Exists(SettingsPath) || !File.ReadAllBytes(SettingsPath).SequenceEqual(File.ReadAllBytes(OriginalPath)); }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public void Apply(ProviderConfig config)
        {
            if (config == null) throw new InvalidOperationException("请先填写配置。");
            Spec.Validator(config.Json);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            // 启动/界面打开时已留档；这里兜底（例如首开时还没有生效文件），留档只创建一次。
            PreserveOriginal();
            WriteAtomic(SettingsPath, Encoding.UTF8.GetBytes(config.Json), BackupPath);
        }

        public void Restore()
        {
            if (!File.Exists(OriginalPath)) throw new InvalidOperationException("尚无留档的原配置。");
            WriteAtomic(SettingsPath, File.ReadAllBytes(OriginalPath), BackupPath);
        }

        static void WriteAtomic(string path, byte[] content, string backup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(content, 0, content.Length); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, backup);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
