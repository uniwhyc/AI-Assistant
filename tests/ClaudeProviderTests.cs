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

            // 留档时机：首次打开配置界面即保存当时的生效文件；已有留档永不被覆盖。
            var snapshot = Service("留档");
            File.WriteAllText(snapshot.SettingsPath, original, new UTF8Encoding(true));
            snapshot.PreserveOriginal();
            Check(File.ReadAllBytes(snapshot.OriginalPath).SequenceEqual(originalBytes), "打开配置界面留档当时的原配置");
            Check(!snapshot.CanRestore(), "生效文件与留档一致时无需恢复");
            File.WriteAllText(snapshot.SettingsPath, b.Json);
            Check(snapshot.CanRestore(), "生效文件偏离留档时恢复可用");
            snapshot.PreserveOriginal();
            Check(File.ReadAllBytes(snapshot.OriginalPath).SequenceEqual(originalBytes), "已有留档永不被覆盖");
            // 生效文件被外部删除同样视为偏离留档：恢复仍可用并能整体还原。
            var missing = Service("缺失");
            File.WriteAllText(missing.SettingsPath, original, new UTF8Encoding(true));
            missing.PreserveOriginal();
            File.Delete(missing.SettingsPath);
            Check(missing.CanRestore(), "生效文件被外部删除时恢复仍可用");
            missing.Restore();
            Check(File.ReadAllBytes(missing.SettingsPath).SequenceEqual(originalBytes), "缺失的生效文件可整体还原为留档原配置");
            var emptySnapshot = Service("留档空");
            emptySnapshot.PreserveOriginal();
            Check(!File.Exists(emptySnapshot.OriginalPath) && !emptySnapshot.CanRestore(), "没有原配置时不伪造留档也不提供恢复");
            // 读取原始配置：有留档取留档，尚无留档时回退当前生效文件。
            var originalReader = Service("原配置读取");
            Check(originalReader.ReadOriginal() == ClaudeProviders.Template, "没有留档也没有生效文件时读取原配置回退模板");
            File.WriteAllText(originalReader.SettingsPath, original, new UTF8Encoding(true));
            originalReader.PreserveOriginal();
            File.WriteAllText(originalReader.SettingsPath, b.Json);
            Check(originalReader.ReadOriginal() == original, "已切换配置后读取原配置仍返回首次留档内容");
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
            // 界面保存的美化：只重排空白，键序、字符串转义与数字原文逐字保留；结果稳定、可重复。
            Check(ClaudeProviders.Pretty("{\"n\":-1.25e+2,\"a\":{\"x\":[1,true,null]},\"s\":\"含 {} 与 \\\"引号\\\"\"}")
                == "{\n  \"n\": -1.25e+2,\n  \"a\": {\n    \"x\": [\n      1,\n      true,\n      null\n    ]\n  },\n  \"s\": \"含 {} 与 \\\"引号\\\"\"\n}\n",
                "美化只重排缩进与换行，键序、字符串与数字原文不变");
            Check(ClaudeProviders.Pretty("{ \"a\" : [ ] , \"b\" : { } }") == "{\n  \"a\": [],\n  \"b\": {}\n}\n",
                "空对象与空数组保持紧凑，冒号逗号周围空白规范化");
            Check(ClaudeProviders.Pretty(ClaudeProviders.Template) == ClaudeProviders.Template
                && ClaudeProviders.Pretty(ClaudeProviders.Pretty("{ \"model\" : \"sonnet\" }")) == ClaudeProviders.Pretty("{ \"model\" : \"sonnet\" }"),
                "已规范的官方模板与美化结果再次美化均逐字不变");
            Check(ClaudeProviders.Pretty("{未完成") == "{未完成" && ClaudeProviders.Pretty("") == "" && ClaudeProviders.Pretty(null) == null,
                "无法完整识别的内容原样返回，不干扰后续校验");
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
            // 界面保存会把 JSON 重排为两空格缩进；以下为对应期望全文（a.Json 此时已是紧凑格式）。
            string prettyA = ClaudeProviders.Pretty(a.Json);
            string prettyB = ClaudeProviders.Pretty(b.Json);
            bool allowDelete = false;
            int confirmations = 0;
            string confirmedName = null;
            bool allowDiscard = true, allowRestore = true;
            int discardPrompts = 0, restorePrompts = 0;
            var panel = new ClaudeProvidersPanel(uiService, selectedName => {
                confirmations++; confirmedName = selectedName; return allowDelete;
            }, () => { discardPrompts++; return allowDiscard; }, () => { restorePrompts++; return allowRestore; })
            { Resources = desktop.Window.Resources };
            Layout(panel, 1020, 690);
            var editor = Field<TextBox>(panel, "Claude 官方配置 JSON");
            var name = Field<TextBox>(panel, "配置名称（保存为同名 JSON 文件）");
            Check(editor.Text == original, "打开编辑器直接参考当前配置全文");
            Check(File.Exists(uiService.OriginalPath) && File.ReadAllBytes(uiService.OriginalPath).SequenceEqual(File.ReadAllBytes(uiService.SettingsPath)),
                "打开配置界面即把当前原配置留档为原始备份");
            var deleteButton = Children(panel).OfType<Button>().First(button => Object.Equals(button.Content, "删除配置"));
            Check(!deleteButton.IsEnabled, "新建状态没有可删除的已保存配置");
            var restoreButton = Children(panel).OfType<Button>().First(button => Object.Equals(button.Content, "恢复原配置"));
            Check(!restoreButton.IsEnabled && restoreButton.ToolTip != null && restoreButton.ToolTip.ToString().Contains("无需恢复"),
                "未偏离原配置时恢复入口禁用并说明原因");
            Check(Field<ComboBox>(panel, "已保存的配置").ToolTip != null, "切换入口下拉框带操作说明");
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
                && File.ReadAllText(Path.Combine(uiService.ConfigDirectory, "重命名界面配置.json")) == prettyA
                && ((ClaudeProvider)Field<ComboBox>(panel, "已保存的配置").SelectedItem).Name == name.Text,
                "保存后配置文件改名且下拉框选中新名称");
            Check(Children(panel).OfType<Button>().Any(button => Object.Equals(button.Content, "启用此配置")), "未修改的已保存配置展示直接启用");
            editor.Text = a.Json + "\n";
            Check(Children(panel).OfType<Button>().Any(button => Object.Equals(button.Content, "保存并启用")), "修改内容后按钮恢复为保存并启用");
            editor.Text = prettyA;
            string configBackupPath = Path.Combine(uiService.ConfigDirectory, "重命名界面配置.json") + ".bak";
            File.WriteAllText(configBackupPath, "marker");
            Click(panel, "启用此配置");
            Check(File.ReadAllText(uiService.SettingsPath) == editor.Text, "界面启用直接完整写入编辑器内容");
            Check(File.ReadAllText(configBackupPath) == "marker", "未修改时直接启用不重复保存配置及备份");
            editor.Text = "{无效"; Click(panel, "保存并启用");
            Check(File.ReadAllText(uiService.SettingsPath) == prettyA, "界面格式错误不会写入目标");
            Check(editor.Text == "{无效", "校验失败保留输入，便于继续修正");
            Check(restoreButton.IsEnabled && restoreButton.ToolTip != null && restoreButton.ToolTip.ToString().Contains("还原"),
                "切换后恢复入口说明用途并保留备份说明");
            Click(panel, "恢复原配置");
            Check(restorePrompts == 1 && File.ReadAllText(uiService.SettingsPath) == original, "恢复原配置经确认后还原最初的原配置");
            Check(!restoreButton.IsEnabled && restoreButton.ToolTip != null && restoreButton.ToolTip.ToString().Contains("无需恢复"),
                "恢复完成后入口回到禁用并说明当前已是原配置");
            Click(panel, "新建配置");
            Check(editor.Text == "{无效" && name.Text == "" && !name.IsReadOnly && discardPrompts == 0 && !deleteButton.IsEnabled,
                "有未保存修改时“新建配置”以当前展示内容为初始内容，不丢弃草稿");
            name.Text = "官方配置示例"; editor.Text = ClaudeProviders.Template; Click(panel, "保存并启用");
            Layout(panel, 1020, 690);
            var bitmap = new RenderTargetBitmap(1020, 690, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(Environment.CurrentDirectory, "artifacts", "claude-providers.png"))) png.Save(stream);
            Layout(panel, 780, 550);
            Check(editor.ActualHeight > 100 && Children(panel).OfType<Button>().First(button => Object.Equals(button.Content, "启用此配置")).ActualHeight > 0,
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
            Check(savedConfigs.Items.Count == 0 && savedConfigs.SelectedIndex == -1
                && savedConfigs.Visibility == Visibility.Collapsed
                && Children(panel).OfType<TextBlock>().Any(text => text.Text.Contains("暂无已保存的配置") && text.Visibility == Visibility.Visible)
                && !deleteButton.IsEnabled, "删除最后一项后不显示下拉框，直接给出空态提示并保持可新建");

            // —— 未保存编辑保护：手动切换选择/恢复前先确认，拒绝则保留编辑内容 ——
            name.Text = "保留测试"; editor.Text = b.Json; Click(panel, "仅保存");
            Click(panel, "新建配置");
            name.Text = "第二配置"; editor.Text = a.Json; Click(panel, "仅保存");
            var switchCombo = Field<ComboBox>(panel, "已保存的配置");
            Check(switchCombo.Items.Count == 2, "准备两个配置用于切换保护测试");
            editor.Text = "{草稿";
            allowDiscard = false;
            int keepIndex = switchCombo.Items.Cast<ProviderConfig>().ToList().FindIndex(p => p.Name == "保留测试");
            switchCombo.SelectedIndex = keepIndex;
            Check(((ProviderConfig)switchCombo.SelectedItem).Name == "第二配置" && editor.Text == "{草稿" && name.Text == "第二配置",
                "选择其他配置被拒绝时保留未保存编辑并回滚选中");
            allowDiscard = true;
            switchCombo.SelectedIndex = keepIndex;
            Check(editor.Text == prettyB && name.Text == "保留测试" && ((ProviderConfig)switchCombo.SelectedItem).Name == "保留测试",
                "确认后载入所选配置内容");
            Click(panel, "新建配置");
            Check(editor.Text == prettyB && name.Text == "" && Field<ComboBox>(panel, "已保存的配置").SelectedIndex == -1,
                "“新建配置”以显示中的所选配置内容为初始内容并进入新建状态");
            editor.Text = "{草稿二";
            int discardBefore = discardPrompts;
            Click(panel, "新建配置");
            Check(editor.Text == "{草稿二" && discardPrompts == discardBefore, "“新建配置”不弹丢弃确认、草稿原样成为初稿");
            allowRestore = false;
            int restoreBefore = restorePrompts;
            Click(panel, "恢复原配置");
            Check(restorePrompts == restoreBefore + 1 && File.ReadAllText(uiService.SettingsPath) == ClaudeProviders.Template,
                "恢复原配置被拒绝时当前文件不变");
            // —— 留档被外部删除：恢复入口禁用并说明暂无原配置，且保存动作不会重建留档 ——
            File.Delete(uiService.OriginalPath);
            name.Text = "留档删除"; editor.Text = a.Json; Click(panel, "仅保存");
            Check(!File.Exists(uiService.OriginalPath) && !restoreButton.IsEnabled
                && restoreButton.ToolTip != null && restoreButton.ToolTip.ToString().Contains("暂无可恢复"),
                "留档被删除后恢复入口禁用并说明暂无原配置");
            string protectedPath = Path.Combine(uiService.ConfigDirectory, "原有配置.json");
            File.WriteAllText(protectedPath, original);
            int protectedConfirmations = 0;
            var protectedPanel = new ClaudeProvidersPanel(uiService, selectedName => { protectedConfirmations++; return true; }, () => true, () => true)
                { Resources = desktop.Window.Resources };
            Layout(protectedPanel, 1020, 690);
            var protectedCombo = Field<ComboBox>(protectedPanel, "已保存的配置");
            protectedCombo.SelectedIndex = protectedCombo.Items.Cast<ProviderConfig>().ToList().FindIndex(p => p.Name == "原有配置");
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
            Check(!File.Exists(protectedPath) && File.ReadAllText(Path.Combine(uiService.ConfigDirectory, "改名后的原有配置.json")) == ClaudeProviders.Pretty(original)
                && !protectedDelete.IsEnabled && !uiService.CanDelete("改名后的原有配置"), "原有配置支持改名但不会取得删除权限");
            // 构造器内回调必须引用字段而非被同名参数遮蔽的注入参数：无注入构造（App 真实路径）时新建不抛空引用。
            var plainPanel = new ClaudeProvidersPanel(uiService) { Resources = desktop.Window.Resources };
            Click(plainPanel, "新建配置");
            Check(Field<TextBox>(plainPanel, "Claude 官方配置 JSON").Text == ClaudeProviders.Template,
                "无注入构造时“新建配置”可用且以当前内容为底稿，不抛空引用");
            int codex = CodexProviderTests.Run(app, desktop);
            if (codex != 0) return codex;
            Console.WriteLine("Claude 配置检查：" + passed + " 项通过。");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("Claude 配置检查失败：" + ex.GetType().Name + "\n" + ex.Message + "\n" + ex.StackTrace); return 1; }
    }
}
