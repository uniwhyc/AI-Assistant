using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

// 新建配置旅程模拟：模拟一位真实用户在打包版程序里“新建配置 → 仅保存 → 启用 → 再建一套 →
// 下拉切换 → 关闭软件重开 → 恢复原配置”的完整过程。重点验证三件事：
// ① 用户原始配置在每一步都被逐字节保留（软件启动即留档、仅保存不动它、多次切换与重启都不覆盖留档、恢复逐字节还原）；
// ② “恢复原配置”的入口与说明明确（禁用时说明原因、可用时说明用途、确认框说明备份保留）；
// ③ “切换配置”的方式明确（下拉框选择 → “启用此配置/保存并启用”，页首“当前配置”即时反映生效者）。
// 全程 UI Automation 驱动真实 exe，CODEX_HOME / CLAUDE_CONFIG_DIR / LOCALAPPDATA 重定向到隔离沙箱。
public static class NewConfigSim
{
    static int passed;
    static int failed;
    static readonly StringBuilder log = new StringBuilder();
    static string workRoot;
    static string appDir;
    static string artifacts;
    static string original;
    static string configOne;
    static string configTwo;
    static string prettyOne;
    static string prettyTwo;

    static void Ok(string message) { passed++; Console.WriteLine("通过：" + message); log.AppendLine("通过：" + message); }
    static void No(string message) { failed++; Console.WriteLine("失败：" + message); log.AppendLine("失败：" + message); }
    static void Check(bool value, string message) { if (value) Ok(message); else No(message); }
    static void Observe(string message) { Console.WriteLine("[观察] " + message); log.AppendLine("[观察] " + message); }

    // ---------- Win32 ----------
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    static void Raise(IntPtr handle)
    {
        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }

    // ---------- 截图 ----------
    static void Shot(IntPtr handle, string name, int delayMs = 400)
    {
        Raise(handle);
        if (delayMs > 0) Thread.Sleep(delayMs);
        RECT rect;
        if (!GetWindowRect(handle, out rect)) { No("截图失败（取窗口矩形）：" + name); return; }
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) { No("截图失败（窗口尺寸异常）：" + name); return; }
        string path = Path.Combine(artifacts, name);
        using (var bitmap = new Bitmap(width, height))
        {
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
            bitmap.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("截图：" + path);
        log.AppendLine("截图：" + path);
    }

    // ---------- UI Automation ----------
    static string Name(AutomationElement element) { try { return element.Current.Name ?? ""; } catch (Exception) { return ""; } }
    static string HelpText(AutomationElement element) { try { return element.Current.HelpText ?? ""; } catch (Exception) { return ""; } }
    static bool IsEnabled(AutomationElement element) { try { return element != null && element.Current.IsEnabled; } catch (Exception) { return false; } }

    static AutomationElement[] Descendants(AutomationElement scope, ControlType type)
    {
        try
        {
            return scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type))
                .Cast<AutomationElement>().ToArray();
        }
        catch (Exception ex)
        {
            log.AppendLine("[警告] FindAll 失败（" + type.ProgrammaticName + "）：" + ex.GetType().Name + "：" + ex.Message);
            return new AutomationElement[0];
        }
    }

    static AutomationElement FindType(AutomationElement scope, ControlType type, string contains)
    {
        foreach (var element in Descendants(scope, type))
            if (Name(element).Contains(contains)) return element;
        return null;
    }

    static AutomationElement WaitType(AutomationElement scope, ControlType type, string contains, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            var element = FindType(scope, type, contains);
            if (element != null) return element;
            Thread.Sleep(250);
        } while (Environment.TickCount < end);
        log.AppendLine("[警告] 等待超时（" + type.ProgrammaticName + "，名称含“" + contains + "”）");
        return null;
    }

    static AutomationElement FindById(AutomationElement scope, string id)
    {
        try { return scope.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)); }
        catch (Exception) { return null; }
    }

    static AutomationElement WaitId(AutomationElement scope, string id, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            var element = FindById(scope, id);
            if (element != null) return element;
            Thread.Sleep(250);
        } while (Environment.TickCount < end);
        log.AppendLine("[警告] 等待超时（AutomationId=" + id + "）");
        return null;
    }

    // 任意类型后代中查找名称包含片段的元素（系统消息框的文案用 Text 之外的控件承载时也能读到）。
    static bool CanSeeText(AutomationElement scope, string needle)
    {
        try
        {
            foreach (AutomationElement element in scope.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                if (Name(element).Contains(needle)) return true;
        }
        catch (Exception) { }
        try { return Name(scope).Contains(needle); } catch (Exception) { return false; }
    }

    static bool WaitText(AutomationElement scope, string needle, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            if (FindType(scope, ControlType.Text, needle) != null) return true;
            Thread.Sleep(250);
        } while (Environment.TickCount < end);
        return false;
    }

    static bool Invoke(AutomationElement element, string what)
    {
        if (element == null) { log.AppendLine("[警告] 找不到要点击的元素：" + what); return false; }
        try { ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return true; }
        catch (Exception ex) { log.AppendLine("[警告] 点击失败（" + what + "）：" + ex.GetType().Name + "：" + ex.Message); return false; }
    }

    // 点击会弹出模态消息框的按钮时，UIA 调用可能一直等到消息框关闭才返回；
    // 放到后台线程点击，主线程才能继续发现并回答消息框。
    static void InvokeAsync(AutomationElement element, string what)
    {
        if (element == null) { log.AppendLine("[警告] 找不到要点击的元素：" + what); return; }
        var thread = new Thread(() => { try { ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch (Exception) { } });
        thread.IsBackground = true;
        thread.Start();
    }

    static void CloseAsync(AutomationElement window)
    {
        if (window == null) return;
        var thread = new Thread(() => { try { ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch (Exception) { } });
        thread.IsBackground = true;
        thread.Start();
    }

    static bool SetValue(AutomationElement element, string what, string text)
    {
        if (element == null) { log.AppendLine("[警告] 找不到要输入的编辑框：" + what); return false; }
        try { ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(text); return true; }
        catch (Exception ex) { log.AppendLine("[警告] 输入失败（" + what + "）：" + ex.GetType().Name + "：" + ex.Message); return false; }
    }

    static string Value(AutomationElement element)
    {
        try { return ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value; }
        catch (Exception) { return null; }
    }

    static AutomationElement WaitWindow(int pid, string titleContains, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            try
            {
                foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, condition))
                    if (Name(window).Contains(titleContains)) return window;
            }
            catch (Exception) { }
            Thread.Sleep(300);
        } while (Environment.TickCount < end);
        return null;
    }

    // ShowDialog 的模态窗口并入主窗口子树（不出现于 UIA 顶层枚举），因此从主窗口向下查找。
    static AutomationElement FindDialog(AutomationElement main, string title)
    {
        foreach (var window in Descendants(main, ControlType.Window))
            if (Name(window).Contains(title)) return window;
        return null;
    }

    static AutomationElement WaitDialog(AutomationElement main, string title, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            var found = FindDialog(main, title);
            if (found != null) return found;
            Thread.Sleep(300);
        } while (Environment.TickCount < end);
        log.AppendLine("[警告] 等待对话框超时（名称=" + title + "）");
        return null;
    }

    static bool WaitDialogGone(AutomationElement main, string title, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            if (FindDialog(main, title) == null) return true;
            Thread.Sleep(300);
        } while (Environment.TickCount < end);
        return false;
    }

    // 系统 MessageBox 是独立顶层窗口；实测模态系统框（#32770）有时不出现在 UIA 顶层枚举中，
    // 故用 Win32 EnumWindows 按进程 + 标题兜底找到 HWND，再转回 UIA 元素供子树查找。
    static AutomationElement FindMessageBox(int pid, string titleContains)
    {
        try
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, condition))
                if (Name(window).Contains(titleContains)) return window;
        }
        catch (Exception) { }
        try
        {
            AutomationElement found = null;
            EnumWindows((hwnd, lParam) =>
            {
                uint windowPid; GetWindowThreadProcessId(hwnd, out windowPid);
                if (windowPid != pid) return true;
                var text = new StringBuilder(256); GetWindowText(hwnd, text, 256);
                if (!text.ToString().Contains(titleContains)) return true;
                try { found = AutomationElement.FromHandle(hwnd); } catch (Exception) { }
                return false;
            }, IntPtr.Zero);
            return found;
        }
        catch (Exception) { return null; }
    }

    static AutomationElement WaitMessageBox(int pid, string titleContains, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            var found = FindMessageBox(pid, titleContains);
            if (found != null) return found;
            Thread.Sleep(200);
        } while (Environment.TickCount < end);
        log.AppendLine("[警告] 等待消息框超时（标题含“" + titleContains + "”）");
        return null;
    }

    // 展开下拉框，找到指定文本的选项并选中（用户切换配置的标准操作）。
    static bool SelectComboItem(AutomationElement dialog, AutomationElement combo, string itemName)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand(); } catch (Exception) { }
            AutomationElement item = null;
            int end = Environment.TickCount + 4000;
            do
            {
                item = Descendants(combo, ControlType.ListItem).FirstOrDefault(e => Name(e).Contains(itemName));
                if (item == null) item = Descendants(dialog, ControlType.ListItem).FirstOrDefault(e => Name(e).Contains(itemName));
                if (item != null) break;
                Thread.Sleep(200);
            } while (Environment.TickCount < end);
            if (item != null)
            {
                try { ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); }
                catch (Exception ex) { log.AppendLine("[警告] 选择下拉项失败：" + ex.Message); }
                Thread.Sleep(200);
                try { ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); } catch (Exception) { }
                return true;
            }
            log.AppendLine("[诊断] 第 " + (attempt + 1) + " 次展开后未找到下拉项“" + itemName + "”，现有项："
                + String.Join(" / ", Descendants(combo, ControlType.ListItem).Select(Name).ToArray()));
            try { ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); } catch (Exception) { }
            Thread.Sleep(400);
        }
        return false;
    }

    // 文件内容与期望文本逐字节一致（UTF-8 无 BOM，与程序写入方式一致）。
    static bool SameBytes(string path, string text)
    {
        try { return File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(new UTF8Encoding(false).GetBytes(text)); }
        catch (Exception) { return false; }
    }

    public static int Main()
    {
        try { SetProcessDPIAware(); } catch (Exception) { }
        try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }

        POINT cursor;
        GetCursorPos(out cursor);

        Process process = null;
        try
        {
            string projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(typeof(NewConfigSim).Assembly.Location));
            artifacts = Path.Combine(projectRoot, "artifacts");
            Directory.CreateDirectory(artifacts);

            // 1. 隔离沙箱：打包版 exe + 一份带鲜明标记的用户原始配置
            workRoot = Path.Combine(Path.GetTempPath(), "ai-new-config-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            appDir = Path.Combine(workRoot, "app");
            Directory.CreateDirectory(appDir);
            File.Copy(Path.Combine(projectRoot, "bin", "AI Assistant.exe"), Path.Combine(appDir, "AI Assistant.exe"));
            File.Copy(Path.Combine(projectRoot, "bin", "AI Assistant.exe.config"), Path.Combine(appDir, "AI Assistant.exe.config"));
            Directory.CreateDirectory(Path.Combine(workRoot, "codex-home", "sessions"));
            Directory.CreateDirectory(Path.Combine(workRoot, "claude-home", "projects"));
            Directory.CreateDirectory(Path.Combine(workRoot, "localappdata"));
            Console.WriteLine("隔离沙箱：" + workRoot);
            log.AppendLine("隔离沙箱：" + workRoot);

            original = "{\r\n  \"env\": { \"ANTHROPIC_BASE_URL\": \"https://original.example.invalid\" },\r\n  \"model\": \"sonnet\",\r\n  \"note\": \"用户原始配置-必须保留\"\r\n}\r\n";
            configOne = "{\r\n  \"env\": { \"ANTHROPIC_BASE_URL\": \"https://new-one.example.invalid\" },\r\n  \"model\": \"opus\",\r\n  \"note\": \"新配置一\"\r\n}\r\n";
            configTwo = "{\r\n  \"env\": { \"ANTHROPIC_BASE_URL\": \"https://new-two.example.invalid\" },\r\n  \"model\": \"haiku\",\r\n  \"note\": \"新配置二\"\r\n}\r\n";
            // 保存时程序把 JSON 重排为两空格缩进（键序与内容不变），以下是重排后的期望全文。
            prettyOne = "{\n  \"env\": {\n    \"ANTHROPIC_BASE_URL\": \"https://new-one.example.invalid\"\n  },\n  \"model\": \"opus\",\n  \"note\": \"新配置一\"\n}\n";
            prettyTwo = "{\n  \"env\": {\n    \"ANTHROPIC_BASE_URL\": \"https://new-two.example.invalid\"\n  },\n  \"model\": \"haiku\",\n  \"note\": \"新配置二\"\n}\n";
            string settingsPath = Path.Combine(workRoot, "claude-home", "settings.json");
            string originalBackupPath = settingsPath + ".ai-assistant.original.bak";
            string previousBackupPath = settingsPath + ".ai-assistant.bak";
            File.WriteAllText(settingsPath, original, new UTF8Encoding(false));

            // 2. 启动真实程序（重定向数据目录）
            var startInfo = new ProcessStartInfo(Path.Combine(appDir, "AI Assistant.exe"));
            startInfo.WorkingDirectory = appDir;
            startInfo.UseShellExecute = false;
            startInfo.EnvironmentVariables["CODEX_HOME"] = Path.Combine(workRoot, "codex-home");
            startInfo.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = Path.Combine(workRoot, "claude-home");
            startInfo.EnvironmentVariables["LOCALAPPDATA"] = Path.Combine(workRoot, "localappdata");
            process = Process.Start(startInfo);
            int pid = process.Id;
            Console.WriteLine("已启动真实程序，PID " + pid);
            log.AppendLine("已启动真实程序，PID " + pid);

            var main = WaitWindow(pid, "AI Assistant", 30000);
            Check(main != null, "真实程序主窗口已出现");
            if (main == null) return Finish(process);
            IntPtr mainHandle = new IntPtr(main.Current.NativeWindowHandle);
            Check(WaitText(main, "更新于", 60000), "首屏读取完成（状态栏出现“更新于”）");
            // 以“第一次打开软件”为准：还没打开“配置修改”，原配置留档就应该已经存在。
            Check(SameBytes(originalBackupPath, original), "软件启动即留档原配置（无需先打开配置修改）");

            // 3. 打开“配置修改”，确认编辑器载入的是用户原始配置
            Check(Invoke(WaitId(main, "ClaudeSettingsButton", 10000), "侧栏“配置修改”"), "点击侧栏“配置修改”");
            var dialog = WaitDialog(main, "配置修改", 10000);
            Check(dialog != null, "配置修改窗口已打开");
            if (dialog == null) return Finish(process);
            IntPtr dialogHandle = new IntPtr(dialog.Current.NativeWindowHandle);
            var editor = WaitType(dialog, ControlType.Edit, "Claude 官方配置 JSON", 10000);
            var nameBox = WaitType(dialog, ControlType.Edit, "配置名称", 10000);
            Check(editor != null && Value(editor) == original, "首次打开：编辑器展示用户原始配置全文");
            Shot(dialogHandle, "new-config-01-first-open.png");

            // —— 恢复入口的清晰度：尚未切换过配置时不可用，且应说明原因 ——
            var restoreButton = WaitType(dialog, ControlType.Button, "恢复原配置", 8000);
            Check(restoreButton != null, "窗口提供“恢复原配置”入口");
            Observe("首次打开时“恢复原配置”按钮 启用=" + IsEnabled(restoreButton) + "，说明=\"" + HelpText(restoreButton) + "\"");
            Check(!IsEnabled(restoreButton), "尚未切换过配置时“恢复原配置”为禁用态（当前已是原配置）");
            Check(HelpText(restoreButton).Length > 0, "禁用状态给出原因说明（悬停提示）");
            // 留档时机：首次打开配置界面就该保存原配置，而不是等到首次启用。
            Check(SameBytes(originalBackupPath, original), "打开配置修改后原配置留档仍逐字节保留");
            Check(SameBytes(settingsPath, original), "留档动作不改动生效文件");

            // 4. 新建配置一：新建 → 命名 → 编辑 → 仅保存
            Check(Invoke(WaitType(dialog, ControlType.Button, "新建配置", 8000), "新建配置"), "点击“新建配置”");
            bool originalLoaded = false;
            for (int i = 0; i < 20 && !originalLoaded; i++)
            {
                originalLoaded = Value(editor) == original;
                if (!originalLoaded) Thread.Sleep(150);
            }
            Check(originalLoaded, "首次新建以编辑器展示的用户原配置为初始内容");
            Check(WaitText(dialog, "请命名并编辑完整配置", 5000), "状态栏提示先命名再保存");
            SetValue(nameBox, "配置名称框", "配置一");
            SetValue(editor, "编辑器", configOne);
            Check(Invoke(WaitType(dialog, ControlType.Button, "仅保存", 8000), "仅保存"), "点击“仅保存”（另存为独立配置）");
            Check(WaitText(dialog, "未改动", 8000), "状态栏说明当前生效文件未被改动");
            string onePath = Path.Combine(appDir, "claude-configs", "配置一.json");
            Check(File.Exists(onePath) && SameBytes(onePath, prettyOne), "配置一保存为独立文件并重排为两空格缩进");
            Check(SameBytes(settingsPath, original), "仅保存后用户原始配置逐字节未动");
            Check(!IsEnabled(restoreButton), "仅保存不算切换：恢复入口仍为禁用");

            // 5. 启用配置一（切换生效）
            Check(WaitType(dialog, ControlType.Button, "启用此配置", 10000) != null, "未修改的配置一键即可启用（按钮为“启用此配置”）");
            Check(Invoke(WaitType(dialog, ControlType.Button, "启用此配置", 8000), "启用此配置"), "点击“启用此配置”");
            Check(WaitText(dialog, "已启用“配置一”", 8000), "状态栏提示已启用配置一");
            Check(SameBytes(settingsPath, prettyOne), "启用后目标文件完整写入配置一内容");
            Check(SameBytes(originalBackupPath, original), "用户原始配置已逐字节备份为原配置备份");
            Check(WaitText(dialog, "当前配置：配置一", 5000), "页首“当前配置”明确显示生效的是配置一");
            Check(IsEnabled(restoreButton), "切换发生后“恢复原配置”变为可用");
            Observe("切换后“恢复原配置”说明=\"" + HelpText(restoreButton) + "\"");
            Check(HelpText(restoreButton).Length > 0, "“恢复原配置”给出明确的用途说明");
            Shot(dialogHandle, "new-config-02-enabled.png");
            var combo = WaitType(dialog, ControlType.ComboBox, "已保存的配置", 8000);
            Observe("“已保存的配置”下拉框说明=\"" + HelpText(combo) + "\"");
            Check(HelpText(combo).Length > 0, "切换入口（下拉框）给出明确的操作说明");

            // 6. 再建配置二并启用（每次点击后先等界面进入新状态，再断言文件，避免与异步派发竞态）
            Check(Invoke(WaitType(dialog, ControlType.Button, "新建配置", 8000), "新建配置（第二个）"), "再次点击“新建配置”");
            Check(WaitText(dialog, "请命名并编辑完整配置", 8000), "界面进入新建状态（提示先命名）");
            bool currentBase = false;
            for (int i = 0; i < 20 && !currentBase; i++)
            {
                currentBase = Value(editor) == prettyOne;
                if (!currentBase) Thread.Sleep(150);
            }
            Check(currentBase, "已切换配置后“新建配置”以编辑器当前展示的配置一为初始内容");
            SetValue(nameBox, "配置名称框", "配置二");
            SetValue(editor, "编辑器", configTwo);
            Check(Invoke(WaitType(dialog, ControlType.Button, "仅保存", 8000), "仅保存（配置二）"), "把第二套配置“仅保存”");
            Check(WaitText(dialog, "配置文件已保存", 8000), "配置二保存完成");
            string twoPath = Path.Combine(appDir, "claude-configs", "配置二.json");
            Check(File.Exists(twoPath) && SameBytes(twoPath, prettyTwo), "配置二保存为独立文件");
            Check(SameBytes(settingsPath, prettyOne), "仅保存配置二后生效文件仍是配置一");
            Check(Invoke(WaitType(dialog, ControlType.Button, "启用此配置", 8000), "启用配置二"), "启用配置二");
            Check(WaitText(dialog, "已启用“配置二”", 8000), "状态栏提示已启用配置二");
            Check(SameBytes(settingsPath, prettyTwo), "生效文件切换为配置二");
            Check(!SameBytes(settingsPath, prettyOne) && !SameBytes(settingsPath, original), "切换后生效文件完整替换为配置二，不残留配置一或原配置内容");
            Check(SameBytes(originalBackupPath, original), "第二次切换后原始配置备份仍逐字节保留");
            Check(SameBytes(previousBackupPath, prettyOne), "上一份生效内容进入“上一份备份”链条");

            // 7. 通过下拉框切回配置一（用户切换配置的标准方式）
            Check(SelectComboItem(dialog, combo, "配置一"), "在下拉框中选择配置一");
            bool loadedOne = false;
            for (int i = 0; i < 20 && !loadedOne; i++)
            {
                string now = Value(editor);
                loadedOne = now == prettyOne;
                if (!loadedOne) Thread.Sleep(150);
            }
            Check(loadedOne, "选择后编辑器载入配置一内容");
            Check(WaitText(dialog, "可直接启用", 5000), "界面提示未修改可直接启用");
            Check(Invoke(WaitType(dialog, ControlType.Button, "启用此配置", 8000), "启用配置一（切回）"), "点击“启用此配置”切回配置一");
            Check(WaitText(dialog, "已启用“配置一”", 8000), "状态栏提示已启用配置一（切回）");
            Check(SameBytes(settingsPath, prettyOne), "生效文件切回配置一");
            Check(!SameBytes(settingsPath, prettyTwo), "切回后生效文件不再是配置二内容");
            Check(SameBytes(originalBackupPath, original), "多次切换后原始配置备份依旧逐字节保留");
            // 7c. 再次验证“新建配置”以编辑器当前展示的配置为初始内容（所见即所得）
            Check(Invoke(WaitType(dialog, ControlType.Button, "新建配置", 8000), "新建配置"), "点击“新建配置”");
            Check(WaitText(dialog, "请命名并编辑完整配置", 5000), "新建配置进入新建状态（提示先命名）");
            bool copyBase = false;
            for (int i = 0; i < 20 && !copyBase; i++)
            {
                copyBase = Value(editor) == prettyOne;
                if (!copyBase) Thread.Sleep(150);
            }
            Check(copyBase, "新建配置以当前展示的配置一为初始内容");
            Shot(dialogHandle, "new-config-03-switch-back.png");

            // 7b. 关闭软件再重开：用户切到新建配置后关掉软件，重开后仍能恢复最初的原配置（留档持久化在磁盘上）
            CloseAsync(dialog);
            Check(WaitDialogGone(main, "配置修改", 8000), "关闭配置窗口（准备重启验证）");
            CloseAsync(main);
            Check(process.WaitForExit(10000), "软件正常退出（留档与配置已落盘）");
            process = Process.Start(startInfo);
            pid = process.Id;
            Console.WriteLine("重新启动真实程序，PID " + pid);
            log.AppendLine("重新启动真实程序，PID " + pid);
            main = WaitWindow(pid, "AI Assistant", 30000);
            Check(main != null, "重启后主窗口已出现");
            if (main == null) return Finish(process);
            mainHandle = new IntPtr(main.Current.NativeWindowHandle);
            Check(WaitText(main, "更新于", 60000), "重启后首屏读取完成");
            Check(SameBytes(originalBackupPath, original), "重启后原配置留档仍逐字节保留");
            Check(Invoke(WaitId(main, "ClaudeSettingsButton", 10000), "重启后侧栏“配置修改”"), "重启后再次打开“配置修改”");
            dialog = WaitDialog(main, "配置修改", 10000);
            Check(dialog != null, "重启后配置修改窗口已打开");
            if (dialog == null) return Finish(process);
            dialogHandle = new IntPtr(dialog.Current.NativeWindowHandle);
            editor = WaitType(dialog, ControlType.Edit, "Claude 官方配置 JSON", 10000);
            Check(editor != null && Value(editor) == prettyOne, "重启后编辑器载入的仍是切换后的配置一（重启不改变生效内容）");
            Check(WaitText(dialog, "当前配置：配置一", 5000), "重启后页首仍显示当前配置为配置一");
            restoreButton = WaitType(dialog, ControlType.Button, "恢复原配置", 8000);
            Check(IsEnabled(restoreButton), "重启后“恢复原配置”仍然可用（不依赖本次运行过程）");
            Check(HelpText(restoreButton).Contains("还原"), "重启后恢复入口仍给出用途说明");
            Shot(dialogHandle, "new-config-03b-after-restart.png");

            // 8. 重启后恢复原配置：明确的恢复方式 + 二次确认
            InvokeAsync(restoreButton, "恢复原配置");
            var restoreBox = WaitMessageBox(pid, "恢复原配置", 8000);
            Check(restoreBox != null, "点击“恢复原配置”弹出二次确认框");
            if (restoreBox != null)
            {
                Shot(new IntPtr(restoreBox.Current.NativeWindowHandle), "new-config-04-restore-confirm.png", 100);
                Check(CanSeeText(restoreBox, "备份"), "确认框说明内容仍保留在备份文件中");
                Check(Invoke(WaitType(restoreBox, ControlType.Button, "是", 5000), "确认恢复"), "点击“是”确认恢复");
            }
            Check(WaitText(dialog, "已恢复", 8000), "状态栏提示已恢复原配置");
            Check(SameBytes(settingsPath, original), "重启后恢复仍把目标文件还原为最初的原配置（逐字节一致）");
            Check(SameBytes(originalBackupPath, original) && File.Exists(onePath) && File.Exists(twoPath),
                "恢复不影响原配置留档与已保存的两套配置");
            Check(!IsEnabled(restoreButton) && HelpText(restoreButton).Contains("无需恢复"), "恢复后入口回到禁用并说明当前已是原配置");
            Check(WaitText(dialog, "未匹配已保存配置", 5000), "页首“当前配置”说明当前已不是任何已保存配置");
            Shot(dialogHandle, "new-config-05-restored.png");

            // 9. 收尾：关闭配置窗口与主窗口（未修改状态，不应有额外确认）
            CloseAsync(dialog);
            Check(WaitDialogGone(main, "配置修改", 8000), "未修改状态下关闭配置窗口（无多余确认）");
            CloseAsync(main);
            Check(process.WaitForExit(10000), "关闭主窗口后程序正常退出");

            return Finish(process);
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine("模拟中断：" + ex.GetType().Name + "\n" + ex.Message + "\n" + ex.StackTrace);
            log.AppendLine("模拟中断：" + ex);
            return Finish(process);
        }
        finally
        {
            SetCursorPos(cursor.X, cursor.Y);
        }
    }

    static int Finish(Process process)
    {
        if (process != null && !process.HasExited)
        {
            // 只清理本次模拟自己启动的实例，避免窗口残留在桌面。
            try { process.CloseMainWindow(); } catch (Exception) { }
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch (Exception) { }
                Console.WriteLine("模拟实例未及时退出，已强制结束（仅本次沙箱进程）。");
            }
        }
        Console.WriteLine();
        Console.WriteLine("新建配置模拟：{0} 项通过，{1} 项失败。", passed, failed);
        log.AppendLine();
        log.AppendLine("新建配置模拟：" + passed + " 项通过，" + failed + " 项失败。");
        Console.WriteLine("隔离沙箱保留在：" + workRoot);
        try { File.WriteAllText(Path.Combine(artifacts, "new-config-sim.log"), log.ToString(), new UTF8Encoding(false)); } catch (Exception) { }
        return failed == 0 ? 0 : 1;
    }
}
