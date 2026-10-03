using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AI_Assistant;

public static class CodexProviderTests
{
    static int passed;
    static string root;
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception("检查失败：" + message);
        passed++; Console.WriteLine("通过：" + message);
    }
    static CodexProviders Service(string name)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        return new CodexProviders(folder, Path.Combine(folder, "配置集合"));
    }
    static ClaudeProviders ClaudeService(string name)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        return new ClaudeProviders(folder, Path.Combine(folder, "配置集合"));
    }
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, message);
    }
    static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    static T Field<T>(FrameworkElement panel, string label) where T : DependencyObject
    { return Children(panel).OfType<T>().First(x => AutomationProperties.GetName(x) == label); }
    static void Click(FrameworkElement panel, string title)
    {
        Children(panel).OfType<Button>().First(b => Object.Equals(b.Content, title)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        panel.UpdateLayout();
    }
    static void Layout(FrameworkElement panel, int width, int height)
    {
        panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout();
    }

    public static int Run(Application app, Desktop desktop)
    {
        try
        {
            root = Path.Combine(Environment.CurrentDirectory, "artifacts", "codex-tests", Guid.NewGuid().ToString("N"));
            var service = Service("全文切换");
            string original = "model = \"original-model\"\napproval_policy = \"on-request\"\n";
            File.WriteAllText(service.SettingsPath, original);
            byte[] originalBytes = File.ReadAllBytes(service.SettingsPath);
            var a = new ProviderConfig { Name = "配置甲", Json = "model = \"gpt-5.1-codex-max\"\nmodel_provider = \"openai\"\n" };
            var b = new ProviderConfig { Name = "配置乙", Json = "model = \"gpt-5.1-codex-mini\"\nsandbox_mode = \"read-only\"\n" };
            service.Save(a); service.Save(b);
            Check(service.SettingsPath.EndsWith("config.toml"), "Codex 目标文件为 config.toml");
            Check(File.ReadAllBytes(service.SettingsPath).SequenceEqual(originalBytes), "Codex 仅保存不修改原配置");
            Check(!File.Exists(service.OriginalPath), "Codex 仅保存不会提前创建原始备份");
            Check(File.ReadAllText(Path.Combine(service.ConfigDirectory, a.Name + ".toml")) == a.Json, "Codex 配置保存为同名 TOML 文件");
            Check(service.Load().Count == 2 && service.Load()[0].ToString() == service.Load()[0].Name, "Codex 加载独立配置文件并显示名称");
            Reject(() => service.Save(a), "Codex 新增同名配置不会覆盖已有文件");
            Reject(() => service.Save(new ProviderConfig { Name = "../越界", Json = "model = \"x\"\n" }), "Codex 配置名称不能逃逸配置目录");
            service.Apply(a);
            Check(File.ReadAllText(service.SettingsPath) == a.Json, "Codex 启用时逐字写入新配置");
            Check(File.ReadAllBytes(service.OriginalPath).SequenceEqual(originalBytes), "Codex 原始备份精确保留内容");
            Check(File.ReadAllBytes(service.BackupPath).SequenceEqual(originalBytes), "Codex 上一份备份精确保留原文件");
            Check(service.CurrentName(service.Load()) == a.Name, "Codex 当前配置从实际文件全文匹配");
            service.Apply(b);
            Check(File.ReadAllText(service.BackupPath) == a.Json, "Codex 再次切换保留上一份配置");
            service.Restore();
            Check(File.ReadAllBytes(service.SettingsPath).SequenceEqual(originalBytes), "Codex 恢复原配置逐字节还原");
            var fresh = Service("首次创建");
            Check(fresh.ReadCurrent() == CodexProviders.Template, "Codex 没有当前配置时提供官方格式模板");
            fresh.Apply(a);
            Check(File.ReadAllText(fresh.SettingsPath) == a.Json && !File.Exists(fresh.OriginalPath), "Codex 首次创建目标文件不伪造原始备份");
            var damaged = Service("原文损坏");
            File.WriteAllText(damaged.SettingsPath, "model = \"未闭合");
            damaged.Apply(b);
            Check(File.ReadAllText(damaged.OriginalPath) == "model = \"未闭合", "Codex 损坏的原文也完整备份");

            // 留档时机（与 Claude 相同）：打开配置界面即保存当时的生效文件；已有留档永不被覆盖。
            var snapshot = Service("留档");
            File.WriteAllText(snapshot.SettingsPath, original);
            snapshot.PreserveOriginal();
            Check(File.ReadAllBytes(snapshot.OriginalPath).SequenceEqual(originalBytes), "Codex 打开配置界面留档当时的原配置");
            Check(!snapshot.CanRestore(), "Codex 生效文件与留档一致时无需恢复");
            File.WriteAllText(snapshot.SettingsPath, b.Json);
            Check(snapshot.CanRestore(), "Codex 生效文件偏离留档时恢复可用");
            snapshot.PreserveOriginal();
            Check(File.ReadAllBytes(snapshot.OriginalPath).SequenceEqual(originalBytes), "Codex 已有留档永不被覆盖");
            var emptySnapshot = Service("留档空");
            emptySnapshot.PreserveOriginal();
            Check(!File.Exists(emptySnapshot.OriginalPath) && !emptySnapshot.CanRestore(), "Codex 没有原配置时不伪造留档也不提供恢复");
            // 生效文件被外部删除同样视为偏离留档：恢复仍可用并能整体还原。
            var missing = Service("原文缺失");
            File.WriteAllText(missing.SettingsPath, original);
            missing.PreserveOriginal();
            File.Delete(missing.SettingsPath);
            Check(missing.CanRestore(), "Codex 生效文件被外部删除时恢复仍可用");
            missing.Restore();
            Check(File.ReadAllBytes(missing.SettingsPath).SequenceEqual(originalBytes), "Codex 缺失的生效文件可整体还原为留档原配置");
            // 读取原始配置：有留档取留档，尚无留档时回退当前生效文件。
            var originalReader = Service("原配置读取");
            Check(originalReader.ReadOriginal() == CodexProviders.Template, "Codex 没有留档也没有生效文件时读取原配置回退模板");
            File.WriteAllText(originalReader.SettingsPath, original);
            originalReader.PreserveOriginal();
            File.WriteAllText(originalReader.SettingsPath, b.Json);
            Check(originalReader.ReadOriginal() == original, "Codex 已切换配置后读取原配置仍返回首次留档内容");

            var deletion = Service("删除配置");
            File.WriteAllText(deletion.SettingsPath, original);
            deletion.Save(a); deletion.Save(a, true); deletion.Save(b); deletion.Apply(a);
            string savedPath = Path.Combine(deletion.ConfigDirectory, a.Name + ".toml");
            deletion.Delete(a.Name);
            Check(!File.Exists(savedPath) && deletion.Load().Single().Name == b.Name, "Codex 删除只移除选中的独立配置");
            Check(File.ReadAllText(deletion.SettingsPath) == a.Json && File.ReadAllText(deletion.OriginalPath) == original,
                "Codex 删除已启用配置仍保留当前文件及备份");
            Reject(() => deletion.Delete(a.Name), "Codex 已删除的配置不再具有删除权限");
            Check(!File.Exists(savedPath + ".ai-assistant-created"), "Codex 删除配置后同时清理创建记录");
            File.WriteAllText(savedPath, original);
            Reject(() => deletion.Delete(a.Name), "Codex 手动放回的原有配置不继承删除权限");
            var sameDirectory = new CodexProviders(Path.GetDirectoryName(deletion.SettingsPath), Path.GetDirectoryName(deletion.SettingsPath));
            File.WriteAllText(deletion.SettingsPath + ".ai-assistant-created", "AI_Assistant");
            Reject(() => sameDirectory.Delete("config"), "Codex 即使存在创建记录也始终保护当前配置文件");

            var renaming = Service("重命名配置");
            File.WriteAllText(renaming.SettingsPath, original);
            renaming.Save(a); renaming.Save(b); renaming.Apply(a);
            var renamed = new ProviderConfig { Name = "重命名后的配置", Json = b.Json };
            renaming.RenameAndSave(a.Name, renamed);
            string oldNamePath = Path.Combine(renaming.ConfigDirectory, a.Name + ".toml");
            string newNamePath = Path.Combine(renaming.ConfigDirectory, renamed.Name + ".toml");
            Check(!File.Exists(oldNamePath) && File.ReadAllText(newNamePath) == b.Json, "Codex 重命名同时保存编辑内容");
            Check(renaming.CanDelete(renamed.Name) && !File.Exists(oldNamePath + ".ai-assistant-created"), "Codex 程序创建记录随重命名迁移");
            Reject(() => renaming.RenameAndSave(renamed.Name, new ProviderConfig { Name = b.Name, Json = a.Json }), "Codex 重命名拒绝覆盖其他配置");
            Reject(() => renaming.RenameAndSave("不存在", a), "Codex 原配置不存在时不创建新文件");

            foreach (string valid in new[] {
                CodexProviders.Template,
                "# 注释\nmodel = 'gpt-5.1-codex-max'\n\n[sandbox_workspace_write]\nnetwork_access = true\n",
                "model = \"转义\\t和\\\"引号\"\napproval_policy = \"on-request\"\n",
                "count = 1_000\nhex = 0xFF\nfloat = -1.5e2\nflag = false\n",
                "when = 1979-05-27\n",
                "[model_providers.ollama]\nname = \"Ollama\"\nbase_url = \"http://localhost:11434/v1\"\n",
                "[profiles.review]\nmodel = \"gpt-5.1-codex-max\"\n",
                "list = [1, 2, 3]\ninline = { a = 1, b = \"x\" }\n",
                "\"quoted key\" = \"值\"\n",
                "multi = \"\"\"\n第一行\n第二行\n\"\"\"\n",
                "long = [\n  1,\n  2,\n]\n"
            }) { CodexProviders.Validate(valid); Check(true, "Codex 接受合法 TOML 样例"); }
            foreach (string invalid in new[] {
                "model = \"未闭合\n", "model = '未闭合\n", "没有等号的行\n", "= 值\n",
                "[]\n", "[.点开头]\n", "x =\n", "x = 未知值abc\n",
                "model = 123\n", "approval_policy = []\n", "model_provider = true\n",
                "a = 1\na = 2\n", "[[\n", "key = \"a\" 多余\n", "sandbox_mode = 5\n", "x = { a = 1\n"
            }) { Reject(() => CodexProviders.Validate(invalid), "Codex 拒绝不符合基本语法或字段类型的配置"); }
            try { CodexProviders.Validate("x = 1\nmodel = 123\n"); throw new Exception("应抛出错误"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("第 2 行"), "Codex 校验错误包含行号"); }
            var tableWarning = new TomlSyntax("[section]\nmodel = \"x\"\n").Check();
            Check(tableWarning.Count == 1 && tableWarning[0].Contains("顶层"), "Codex 表内常用字段提示不会作为顶层生效");
            var sandboxWarning = new TomlSyntax("sandbox_mode = \"full\"\n").Check();
            Check(sandboxWarning.Count == 1 && sandboxWarning[0].Contains("sandbox_mode"), "Codex 未知 sandbox_mode 取值产生警告");

            var uiClaude = ClaudeService("界面Claude");
            var uiCodex = Service("界面Codex");
            File.WriteAllText(uiCodex.SettingsPath, original);
            var panel = new ClaudeProvidersPanel(uiClaude, uiCodex, null, () => true, () => true) { Resources = desktop.Window.Resources };
            Layout(panel, 1020, 690);
            Check(Field<TextBox>(panel, "Claude 官方配置 JSON").Text == uiClaude.ReadCurrent(), "配置修改窗口默认打开 Claude 配置");
            Check(Children(panel).OfType<Button>().Count(button => Object.Equals(button.Content, "Codex")) == 1
                && Children(panel).OfType<Button>().Count(button => Object.Equals(button.Content, "Claude Code")) == 1,
                "双平台切换按钮同时显示");
            Click(panel, "Codex");
            Check(Field<TextBox>(panel, "Codex 官方配置 TOML").Text == original, "切换 Codex 后编辑当前 config.toml 全文");
            Check(Children(panel).OfType<TextBlock>().Any(text => text.Text.Contains("目标文件：") && text.Text.EndsWith("config.toml")),
                "切换后目标文件指向 config.toml");
            Check(File.Exists(uiCodex.OriginalPath) && File.ReadAllBytes(uiCodex.OriginalPath).SequenceEqual(File.ReadAllBytes(uiCodex.SettingsPath)),
                "Codex 打开配置界面即把当前原配置留档为原始备份");
            var codexName = Field<TextBox>(panel, "配置名称（保存为同名 TOML 文件）");
            codexName.Text = "界面测试配置";
            var codexEditor = Field<TextBox>(panel, "Codex 官方配置 TOML");
            codexEditor.Text = a.Json; Click(panel, "仅保存");
            Check(File.ReadAllText(uiCodex.SettingsPath) == original, "Codex 界面仅保存不改动当前文件");
            Check(File.ReadAllText(Path.Combine(uiCodex.ConfigDirectory, "界面测试配置.toml")) == a.Json, "Codex 界面保存为独立 TOML 文件");
            Click(panel, "启用此配置");
            Check(File.ReadAllText(uiCodex.SettingsPath) == a.Json, "Codex 界面启用完整写入 config.toml");
            var codexRestore = Children(panel).OfType<Button>().First(button => Object.Equals(button.Content, "恢复原配置"));
            Check(codexRestore.IsEnabled && codexRestore.ToolTip != null && codexRestore.ToolTip.ToString().Contains("Codex"),
                "Codex 切换后恢复入口说明用途与平台");
            // —— 留档被外部删除：恢复入口禁用并说明暂无原配置，且保存动作不会重建留档 ——
            File.Delete(uiCodex.OriginalPath);
            codexName.Text = "留档删除"; codexEditor.Text = a.Json; Click(panel, "仅保存");
            Check(!File.Exists(uiCodex.OriginalPath) && !codexRestore.IsEnabled
                && codexRestore.ToolTip != null && codexRestore.ToolTip.ToString().Contains("暂无可恢复"),
                "Codex 留档被删除后恢复入口禁用并说明暂无原配置");
            codexEditor.Text = "x = 1\nmodel = 123\n"; Click(panel, "检查格式");
            Check(Children(panel).OfType<TextBlock>().Any(text => text.Text.Contains("第 2 行")), "Codex 检查格式显示行号错误");
            codexEditor.Text = "未保存的修改";
            Click(panel, "Claude Code");
            var claudeConfigs = Field<ComboBox>(panel, "已保存的配置");
            Check(Field<TextBox>(panel, "Claude 官方配置 JSON").Text == uiClaude.ReadCurrent()
                && claudeConfigs.Items.Count == 0 && claudeConfigs.Visibility == Visibility.Collapsed
                && Children(panel).OfType<TextBlock>().Any(text => text.Text.Contains("暂无已保存的配置") && text.Visibility == Visibility.Visible),
                "切回 Claude 恢复其配置列表（空列表直接提示、不显示下拉框）与全文");
            Click(panel, "Codex");
            Check(Field<TextBox>(panel, "Codex 官方配置 TOML").Text == uiCodex.ReadCurrent(), "切换平台丢弃未保存编辑并重新参考当前全文");
            var bitmap = new RenderTargetBitmap(1020, 690, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(Environment.CurrentDirectory, "artifacts", "codex-providers.png"))) png.Save(stream);
            Console.WriteLine("Codex 配置检查：" + passed + " 项通过。");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("Codex 配置检查失败：" + ex.GetType().Name + "\n" + ex.Message + "\n" + ex.StackTrace); return 1; }
    }
}
