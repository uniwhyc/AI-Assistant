using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AI_Assistant;

public static class ClaudeProviderTests
{
    static int passed;
    static string root;
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception("检查失败：" + message);
        passed++; Console.WriteLine("通过：" + message);
    }
    static ClaudeProviders Service(string name)
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

    [STAThread]
    public static int Main()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            ClaudeModelsTests.Run().GetAwaiter().GetResult();
            root = Path.Combine(Environment.CurrentDirectory, "artifacts", "claude-tests", Guid.NewGuid().ToString("N"));
            var service = Service("全文切换");
            string original = "{\r\n  \"env\": {\"原环境变量\": \"保留在原配置\"},\r\n  \"permissions\": {\"allow\": [\"Read\"]}\r\n}\r\n";
            File.WriteAllText(service.SettingsPath, original, new UTF8Encoding(true));
            byte[] originalBytes = File.ReadAllBytes(service.SettingsPath);
            var a = new ClaudeProvider { Name = "配置甲", Json = "{\n  \"model\": \"sonnet\",\n  \"env\": {},\n  \"hooks\": {},\n  \"自定义字段\": {\"列表\": [1, true, null]}\n}\n" };
            var b = new ClaudeProvider { Name = "配置乙", Json = "{\n  \"model\": \"opus\",\n  \"apiKeyHelper\": \"由用户填写的助手命令\",\n  \"env\": {\"自定义变量\": \"值\"}\n}\n" };
            service.Save(a); service.Save(b);
            Check(File.ReadAllBytes(service.SettingsPath).SequenceEqual(originalBytes), "仅保存不修改原 Claude 配置");
            Check(!File.Exists(service.OriginalPath), "仅保存不会提前创建原始备份");
            Check(File.ReadAllText(Path.Combine(service.ConfigDirectory, a.Name + ".json")) == a.Json, "每套配置保存为官方格式 JSON 文件，保留全文和缩进");
            Check(service.Load().Count == 2 && service.Load()[0].ToString() == service.Load()[0].Name, "可加载独立配置文件并显示名称");
            Reject(() => service.Save(a), "新增同名配置不会覆盖已有文件");
            Reject(() => service.Save(new ClaudeProvider { Name = "../越界", Json = "{}" }), "配置名称不能逃逸配置目录");
            Reject(() => service.Save(new ClaudeProvider { Name = "CON", Json = "{}" }), "拒绝 Windows 保留文件名");
            service.Apply(a);
            Check(File.ReadAllText(service.SettingsPath) == a.Json, "启用时逐字写入新配置，不合并旧字段");
            Check(File.ReadAllBytes(service.OriginalPath).SequenceEqual(originalBytes), "原始备份精确保留内容、换行和 BOM");
            Check(File.ReadAllBytes(service.BackupPath).SequenceEqual(originalBytes), "上一份备份精确保留原文件");
            Check(service.CurrentName(service.Load()) == a.Name, "当前配置从实际文件全文匹配");
            service.Apply(b);
            Check(File.ReadAllText(service.SettingsPath) == b.Json, "官方格式中的字段原样写入，不限制认证方案");
            Check(File.ReadAllText(service.BackupPath) == a.Json, "再次切换保留上一份配置");
            Check(File.ReadAllBytes(service.OriginalPath).SequenceEqual(originalBytes), "多次切换不会覆盖首次原始备份");
            service.Restore();
            Check(File.ReadAllBytes(service.SettingsPath).SequenceEqual(originalBytes), "恢复原配置逐字节还原首次切换前的文件");
            Check(File.ReadAllText(service.BackupPath) == b.Json, "恢复原配置前仍保留被替换的配置");
            a.Json = "{ \"model\": \"haiku\" }\n"; service.Save(a, true);
            Check(service.Load().Count == 2 && File.ReadAllText(Path.Combine(service.ConfigDirectory, "配置甲.json")) == a.Json, "编辑已选配置更新原配置文件");
            var invalid = new[] { "", "{", "[]", "null", "{'model':'sonnet'}", "{model:\"sonnet\"}", "{\"env\":{},}",
                "{\".env\":{}}", "{\"env\":{\"ANTOROPIC_BASE_URL\":\"https://example.invalid\"}}",
                "{\"env\":null}", "{\"env\":[]}", "{\"env\":{\"变量\":1}}", "{\"permissions\":[]}", "{\"hooks\":false}",
                "{\"model\":1}", "{\"$schema\":{}}", "{\"env\":{},\"env\":{}}", "{\"n\":NaN}", "{\"n\":01}", "{\"n\":+1}",
                "{\"n\":1.}", "{\"n\":Infinity}", "{\"a\":[1,]}", "{} 后缀", "{/* 注释 */}", "{\"a\":\"未转义\n换行\"}" };
            foreach (string json in invalid)
            {
                Reject(() => service.Apply(new ClaudeProvider { Name = "无效配置", Json = json }), "拒绝不符合 JSON 语法或基本字段类型的配置");
                Check(File.ReadAllBytes(service.SettingsPath).SequenceEqual(originalBytes), "无效新配置不影响当前文件");
            }
            foreach (string json in new[] { "{}", ClaudeProviders.Template, "{\"n\":-1.25e+2,\"a\":[{},[],true,false,null]}",
                "{\"env\":{\"变量\":\"换行\\n及\\\"引号\\\"与\\u4e2d文\"}}", "{\"未知字段\":{\"允许扩展\":true}}" })
            {
                ClaudeProviders.Validate(json); Check(true, "接受标准 JSON 和未作限制的扩展字段");
            }
            using (var locked = new FileStream(service.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { service.Apply(a); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                Check(failed && File.ReadAllBytes(service.SettingsPath).SequenceEqual(originalBytes), "文件占用时原子写入失败，不损坏原文件");
            }
            Check(Directory.GetFiles(Path.GetDirectoryName(service.SettingsPath), "*.tmp").Length == 0, "失败后不残留临时配置文件");
            var fresh = Service("首次创建");
            Check(fresh.ReadCurrent() == ClaudeProviders.Template, "没有当前配置时提供官方格式模板");
            fresh.Apply(a);
            Check(File.ReadAllText(fresh.SettingsPath) == a.Json && !File.Exists(fresh.OriginalPath), "首次创建目标文件，不伪造原始备份");
            var damaged = Service("原文损坏");
            File.WriteAllText(damaged.SettingsPath, "{未完成");
            damaged.Apply(b);
            Check(File.ReadAllText(damaged.OriginalPath) == "{未完成", "损坏的原文也完整备份，不阻止写入有效新配置");

            var deletion = Service("删除配置");
            File.WriteAllText(deletion.SettingsPath, original);
            deletion.Save(a); deletion.Save(a, true); deletion.Save(b); deletion.Apply(a);
            Check(new ClaudeProviders(Path.GetDirectoryName(deletion.SettingsPath), deletion.ConfigDirectory).CanDelete(a.Name),
                "程序创建记录在重新打开服务后仍有效");
            string savedPath = Path.Combine(deletion.ConfigDirectory, a.Name + ".json");
            byte[] savedBackup = File.ReadAllBytes(savedPath + ".bak");
            foreach (string invalidName in new[] { null, "", "../settings", "..\\settings", "C:\\settings", "CON", "配置甲.", " 配置甲" })
                Reject(() => deletion.Delete(invalidName), "删除拒绝无效名称和目录外路径");
            deletion.Delete(a.Name);
            Check(!File.Exists(savedPath) && deletion.Load().Single().Name == b.Name, "删除只移除选中的独立配置");
            Check(File.ReadAllText(deletion.SettingsPath) == a.Json && File.ReadAllText(deletion.OriginalPath) == original
                && File.ReadAllText(deletion.BackupPath) == original && File.ReadAllBytes(savedPath + ".bak").SequenceEqual(savedBackup),
                "删除已启用配置仍保留当前文件及全部备份");
            Reject(() => deletion.Delete(a.Name), "已删除的配置不再具有删除权限");
            Check(!File.Exists(savedPath + ".ai-assistant-created"), "删除配置后同时清理对应创建记录");
            File.WriteAllText(Path.Combine(deletion.ConfigDirectory, b.Name + ".json"), "{无效");
            deletion.Delete(b.Name);
            Check(deletion.Load().Count == 0, "允许删除内容损坏的配置及最后一项配置");
            File.WriteAllText(savedPath, original);
            Check(!deletion.CanDelete(a.Name), "同名手动放回的原有配置不会继承删除权限");
            Reject(() => deletion.Delete(a.Name), "后端拒绝删除没有程序创建记录的配置");
            Check(File.ReadAllText(savedPath) == original, "拒绝删除后原有配置全文不变");
            deletion.Save(a, true);
            Check(!deletion.CanDelete(a.Name), "编辑保存原有配置不会将其认领为程序新建");
            Reject(() => deletion.Delete(a.Name), "原有配置编辑保存后仍禁止删除");
            var sameDirectory = new ClaudeProviders(Path.GetDirectoryName(deletion.SettingsPath), Path.GetDirectoryName(deletion.SettingsPath));
            File.WriteAllText(deletion.SettingsPath + ".ai-assistant-created", "AI_Assistant");
            Check(!sameDirectory.CanDelete("settings"), "即使存在创建记录也始终保护 Claude 当前配置路径");
            Reject(() => sameDirectory.Delete("settings"), "后端不可删除 Claude 当前配置文件");

            var renaming = Service("重命名配置");
            File.WriteAllText(renaming.SettingsPath, original);
            renaming.Save(a); renaming.Save(a, true); renaming.Save(b); renaming.Apply(a);
            string oldNamePath = Path.Combine(renaming.ConfigDirectory, a.Name + ".json");
            byte[] oldNameBackup = File.ReadAllBytes(oldNamePath + ".bak");
            var renamed = new ClaudeProvider { Name = "重命名后的配置", Json = b.Json };
            renaming.RenameAndSave(a.Name, renamed);
            string newNamePath = Path.Combine(renaming.ConfigDirectory, renamed.Name + ".json");
            Check(!File.Exists(oldNamePath) && File.ReadAllText(newNamePath) == b.Json && renaming.Load().Count == 2,
                "重命名同时保存编辑内容，不新增重复配置");
            Check(renaming.CanDelete(renamed.Name) && !File.Exists(oldNamePath + ".ai-assistant-created"),
                "程序创建记录随重命名迁移");
            Check(File.ReadAllBytes(oldNamePath + ".bak").SequenceEqual(oldNameBackup)
                && File.ReadAllText(newNamePath + ".bak") == a.Json, "改名保留旧备份并备份编辑前的配置内容");
            Check(File.ReadAllText(renaming.SettingsPath) == a.Json && File.ReadAllText(renaming.OriginalPath) == original
                && File.ReadAllText(renaming.BackupPath) == original, "仅重命名保存不影响 Claude 当前文件及备份");
            Reject(() => renaming.RenameAndSave(renamed.Name, new ClaudeProvider { Name = b.Name, Json = a.Json }), "重命名拒绝覆盖其他配置");
            Check(File.ReadAllText(newNamePath) == b.Json && File.ReadAllText(Path.Combine(renaming.ConfigDirectory, b.Name + ".json")) == b.Json,
                "名称冲突后源配置和目标配置内容均不变");
            Reject(() => renaming.RenameAndSave(renamed.Name, new ClaudeProvider { Name = "../越界", Json = a.Json }), "重命名拒绝目录外路径");
            Reject(() => renaming.RenameAndSave(renamed.Name, new ClaudeProvider { Name = "无效内容", Json = "{" }), "无效 JSON 在重命名前被拒绝");
            Reject(() => renaming.RenameAndSave("不存在", a), "原配置不存在时不创建新文件");
            File.WriteAllText(Path.Combine(renaming.ConfigDirectory, "备份占用.json.bak"), original);
            Reject(() => renaming.RenameAndSave(renamed.Name, new ClaudeProvider { Name = "备份占用", Json = a.Json }), "重命名不会覆盖目标名称的历史备份");
            using (var locked = new FileStream(newNamePath + ".ai-assistant-created", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { renaming.RenameAndSave(renamed.Name, new ClaudeProvider { Name = "迁移失败", Json = a.Json }); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                Check(failed && File.ReadAllText(newNamePath) == b.Json && renaming.CanDelete(renamed.Name)
                    && !File.Exists(Path.Combine(renaming.ConfigDirectory, "迁移失败.json")), "创建记录迁移失败时还原原文件名和内容");
            }
            renaming.Save(new ClaudeProvider { Name = "RenameCase", Json = a.Json });
            renaming.RenameAndSave("RenameCase", new ClaudeProvider { Name = "renamecase", Json = b.Json });
            Check(renaming.Load().Any(config => config.Name == "renamecase" && config.Json == b.Json) && renaming.CanDelete("renamecase"),
                "支持仅修改名称大小写并保留创建记录");
            Reject(() => sameDirectory.RenameAndSave("settings", a), "重命名始终保护 Claude 当前配置路径");

            var app = new Application(); var desktop = new Desktop();
            var uiService = Service("界面");
            File.WriteAllText(uiService.SettingsPath, original);
            bool allowDelete = false;
            int confirmations = 0;
            string confirmedName = null;
            var panel = new ClaudeProvidersPanel(uiService, selectedName => {
                confirmations++; confirmedName = selectedName; return allowDelete;
            }) { Resources = desktop.Window.Resources };
            Layout(panel, 1020, 690);
            var editor = Field<TextBox>(panel, "Claude 官方配置 JSON");
            var name = Field<TextBox>(panel, "配置名称（保存为同名 JSON 文件）");
            Check(editor.Text == original, "打开编辑器直接参考当前配置全文");
            var deleteButton = Children(panel).OfType<Button>().First(button => Object.Equals(button.Content, "删除配置"));
            Check(!deleteButton.IsEnabled, "新建状态没有可删除的已保存配置");
            Check(!Children(panel).OfType<ClaudeModelsPanel>().Any(), "配置窗口不再显示模型查询面板");
            Check(editor.ActualWidth == panel.ActualWidth, "JSON 编辑区使用配置窗口的完整内容宽度");
            name.Text = "界面测试配置"; editor.Text = a.Json; Click(panel, "仅保存");
            Check(File.ReadAllText(uiService.SettingsPath) == original, "界面仅保存不改动当前文件");
            Check(File.ReadAllText(Path.Combine(uiService.ConfigDirectory, "界面测试配置.json")) == editor.Text, "界面保存为独立官方格式文件");
            Check(deleteButton.IsEnabled, "保存并选中配置后启用删除入口");
            Check(!name.IsReadOnly, "已保存配置的名称支持编辑");
            name.Text = "重命名界面配置";
            Check(File.Exists(Path.Combine(uiService.ConfigDirectory, "界面测试配置.json")), "编辑名称时不立即改动文件");
            Click(panel, "仅保存");
            Check(!File.Exists(Path.Combine(uiService.ConfigDirectory, "界面测试配置.json"))
                && File.ReadAllText(Path.Combine(uiService.ConfigDirectory, "重命名界面配置.json")) == a.Json
                && ((ClaudeProvider)Field<ComboBox>(panel, "已保存的配置").SelectedItem).Name == name.Text,
                "保存后配置文件改名且下拉框选中新名称");
            Click(panel, "保存并启用");
            Check(File.ReadAllText(uiService.SettingsPath) == editor.Text, "界面启用直接完整写入编辑器内容");
            editor.Text = "{无效"; Click(panel, "保存并启用");
            Check(File.ReadAllText(uiService.SettingsPath) == a.Json, "界面格式错误不会写入目标");
            Check(editor.Text == "{无效", "校验失败保留输入，便于继续修正");
            Click(panel, "恢复原配置");
            Check(File.ReadAllText(uiService.SettingsPath) == original, "界面恢复最初的原配置");
            Click(panel, "从当前配置新建");
            Check(editor.Text == original && name.Text == "" && !name.IsReadOnly, "可复制当前全文另存为新配置");
            Click(panel, "新增配置");
            Check(editor.Text == ClaudeProviders.Template, "新增配置提供标准 JSON 模板");
            Check(!deleteButton.IsEnabled, "切换新增配置后禁用删除入口");
            name.Text = "官方配置示例"; editor.Text = ClaudeProviders.Template; Click(panel, "保存并启用");
            Layout(panel, 1020, 690);
            var bitmap = new RenderTargetBitmap(1020, 690, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(Environment.CurrentDirectory, "artifacts", "claude-providers.png"))) png.Save(stream);
            Layout(panel, 780, 550);
            Check(editor.ActualHeight > 100 && Children(panel).OfType<Button>().First(button => Object.Equals(button.Content, "保存并启用")).ActualHeight > 0,
                "较矮布局保留编辑空间和启用按钮");
            Check(Field<ComboBox>(panel, "已保存的配置").ActualWidth >= 160 && deleteButton.ActualWidth > 0,
                "较窄布局仍完整显示配置选择和删除按钮");
            string deletingPath = Path.Combine(uiService.ConfigDirectory, "官方配置示例.json");
            byte[] activeBeforeDelete = File.ReadAllBytes(uiService.SettingsPath);
            byte[] originalBeforeDelete = File.ReadAllBytes(uiService.OriginalPath);
            byte[] previousBeforeDelete = File.ReadAllBytes(uiService.BackupPath);
            editor.Text = "{未保存的修改";
            Click(panel, "删除配置");
            Check(confirmations == 1 && confirmedName == "官方配置示例" && File.Exists(deletingPath)
                && editor.Text == "{未保存的修改" && name.Text == confirmedName, "取消删除保留配置、选择和未保存编辑");
            allowDelete = true;
            using (var locked = new FileStream(deletingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(panel, "删除配置");
                Check(File.Exists(deletingPath) && name.Text == "官方配置示例" && editor.Text == "{未保存的修改"
                    && Children(panel).OfType<TextBlock>().Any(text => text.Text.Contains("文件读写失败")),
                    "文件占用时提示删除失败并保留编辑内容");
            }
            Click(panel, "删除配置");
            var savedConfigs = Field<ComboBox>(panel, "已保存的配置");
            Check(!File.Exists(deletingPath) && savedConfigs.Items.Count == 1 && savedConfigs.SelectedIndex == -1
                && !deleteButton.IsEnabled && name.Text == "" && !name.IsReadOnly, "确认删除后刷新列表并进入新建状态");
            Check(File.ReadAllBytes(uiService.SettingsPath).SequenceEqual(activeBeforeDelete)
                && File.ReadAllBytes(uiService.OriginalPath).SequenceEqual(originalBeforeDelete)
                && File.ReadAllBytes(uiService.BackupPath).SequenceEqual(previousBeforeDelete)
                && editor.Text == uiService.ReadCurrent(), "删除已启用配置不影响当前配置和备份，并重新参考当前全文");
            savedConfigs.SelectedIndex = 0;
            Click(panel, "删除配置");
            Check(savedConfigs.Items.Count == 0 && !deleteButton.IsEnabled, "删除最后一项后空列表仍可新建配置");
            string protectedPath = Path.Combine(uiService.ConfigDirectory, "原有配置.json");
            File.WriteAllText(protectedPath, original);
            int protectedConfirmations = 0;
            var protectedPanel = new ClaudeProvidersPanel(uiService, selectedName => { protectedConfirmations++; return true; })
                { Resources = desktop.Window.Resources };
            Layout(protectedPanel, 1020, 690);
            Field<ComboBox>(protectedPanel, "已保存的配置").SelectedIndex = 0;
            var protectedDelete = Children(protectedPanel).OfType<Button>().First(button => Object.Equals(button.Content, "删除配置"));
            Check(!protectedDelete.IsEnabled && Children(protectedPanel).OfType<TextBlock>().Any(text => text.Text.Contains("禁止删除")),
                "选择原有配置时禁用删除并说明保护原因");
            Click(protectedPanel, "删除配置");
            Check(protectedConfirmations == 0 && File.ReadAllText(protectedPath) == original,
                "绕过禁用按钮触发事件也无法删除原有配置");
            Click(protectedPanel, "仅保存");
            Check(!protectedDelete.IsEnabled, "原有配置在界面保存后仍保持删除保护");
            Field<TextBox>(protectedPanel, "配置名称（保存为同名 JSON 文件）").Text = "改名后的原有配置";
            Click(protectedPanel, "仅保存");
            Check(!File.Exists(protectedPath) && File.ReadAllText(Path.Combine(uiService.ConfigDirectory, "改名后的原有配置.json")) == original
                && !protectedDelete.IsEnabled && !uiService.CanDelete("改名后的原有配置"), "原有配置支持改名但不会取得删除权限");
            int codex = CodexProviderTests.Run(app, desktop);
            if (codex != 0) return codex;
            Console.WriteLine("Claude 配置检查：" + passed + " 项通过。");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("Claude 配置检查失败：" + ex.GetType().Name + "\n" + ex.Message + "\n" + ex.StackTrace); return 1; }
    }
}
