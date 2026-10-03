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

// 真实用户模拟：在隔离沙箱中启动 bin 里的打包版 exe 本体（不是进程内控件仿真），
// 用 UI Automation + 真实鼠标事件像用户一样操作：导航、搜索、双击项目跳转、
// 打开“配置修改”窗口走完整配置流程，全程截图，并断言沙箱内文件的实际变化。
// 通过 CODEX_HOME / CLAUDE_CONFIG_DIR / LOCALAPPDATA 重定向数据目录，
// 不读取、不写入本机真实的 Codex / Claude Code 配置。
// 查找策略：侧栏按钮、搜索框、页面标题等有 x:Name 的元素用 AutomationId 查找；
// 配置窗口内的编辑框与按钮按无障碍名称查找；全部查找带重试并记录诊断信息。
public static class RealUserSim
{
    static int passed;
    static int failed;
    static readonly StringBuilder log = new StringBuilder();
    static string workRoot;
    static string appDir;
    static string artifacts;
    static string original;
    static string modified;
    static string prettyOriginal;
    static string prettyModified;

    static void Ok(string message) { passed++; Console.WriteLine("通过：" + message); log.AppendLine("通过：" + message); }
    static void No(string message) { failed++; Console.WriteLine("失败：" + message); log.AppendLine("失败：" + message); }
    static void Check(bool value, string message) { if (value) Ok(message); else No(message); }
    // 新手探索观察：只记录事实，不计入通过/失败（用于评估交互是否让人困惑）。
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
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    // 把目标窗口提到前台并确保不是最小化。
    // 严禁模拟 Alt 键（曾用它解锁前台切换限制）：实测单独按一下 Alt 会让窗口进入“菜单模式”，
    // WPF 内容区随即从 UIA 树中消失（只剩标题栏框架的 11 个元素），所有跨进程查找全部超时，
    // 直到在内容区发生一次真实鼠标点击才会恢复。ShowWindow(SW_RESTORE) 与 SetForegroundWindow 无此副作用。
    static void Raise(IntPtr handle)
    {
        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }

    static void DoubleClickAt(double x, double y)
    {
        SetCursorPos((int)x, (int)y);
        Thread.Sleep(120);
        for (int i = 0; i < 2; i++)
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(70);
        }
    }

    static void ClickAt(double x, double y)
    {
        SetCursorPos((int)x, (int)y);
        Thread.Sleep(120);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    // ---------- 截图 ----------
    // delayMs 默认 400ms 让窗口置前后完成重绘；拍一闪而过的加载遮罩时传小值抢时间。
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

    // ---------- UI Automation 辅助 ----------
    static string Name(AutomationElement element)
    {
        try { return element.Current.Name ?? ""; } catch (Exception) { return ""; }
    }

    static bool IsEnabled(AutomationElement element)
    {
        try { return element != null && element.Current.IsEnabled; } catch (Exception) { return false; }
    }

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
        foreach (var candidate in Descendants(scope, type))
            log.AppendLine("[诊断] 候选 " + type.ProgrammaticName + "：\"" + Name(candidate) + "\"");
        try
        {
            var all = scope.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            log.AppendLine("[诊断] 超时瞬间全树元素数：" + all.Count);
        }
        catch (Exception ex)
        {
            log.AppendLine("[诊断] 全树 FindAll 失败：" + ex.GetType().Name + "：" + ex.Message);
        }
        return null;
    }

    static AutomationElement FindById(AutomationElement scope, string id)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var found = scope.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            watch.Stop();
            if (found == null && watch.ElapsedMilliseconds > 300)
                log.AppendLine("[诊断] FindById(" + id + ") 返回空且耗时 " + watch.ElapsedMilliseconds + "ms");
            return found;
        }
        catch (Exception ex)
        {
            log.AppendLine("[警告] FindById(" + id + ") 失败：" + ex.GetType().Name + "：" + ex.Message);
            return null;
        }
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
        DiagnoseTree(scope, id);
        return null;
    }

    // 超时瞬间的全树体检：树是否完整、条件查找是否漏掉了目标元素。
    static void DiagnoseTree(AutomationElement scope, string id)
    {
        try
        {
            var all = scope.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            log.AppendLine("[诊断] 超时瞬间全树元素数：" + all.Count);
            int withId = 0, matched = 0;
            foreach (AutomationElement element in all)
            {
                try
                {
                    string elementId = element.Current.AutomationId ?? "";
                    if (elementId.Length > 0) withId++;
                    if (elementId == id) matched++;
                }
                catch (Exception) { }
            }
            log.AppendLine("[诊断] 其中有 AutomationId 的：" + withId + "，遍历命中目标 Id 的：" + matched);
        }
        catch (Exception ex)
        {
            log.AppendLine("[诊断] 全树 FindAll 失败：" + ex.GetType().Name + "：" + ex.Message);
        }
    }

    static bool WaitPage(AutomationElement main, string title, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            var page = FindById(main, "PageTitle");
            if (page != null && Name(page) == title) return true;
            Thread.Sleep(250);
        } while (Environment.TickCount < end);
        var current = FindById(main, "PageTitle");
        log.AppendLine("[警告] 页面未切换：期望“" + title + "”，实际“" + (current == null ? "<未找到>" : Name(current)) + "”");
        return false;
    }

    static bool CanSeeText(AutomationElement scope, string needle)
    {
        foreach (var element in Descendants(scope, ControlType.Text))
            if (Name(element).Contains(needle)) return true;
        return false;
    }

    static bool WaitText(AutomationElement scope, string needle, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            if (CanSeeText(scope, needle)) return true;
            Thread.Sleep(250);
        } while (Environment.TickCount < end);
        return false;
    }

    // 折叠（Collapsed）的 WPF 元素仍留在 UIA 树里，只查“存在”会把遮罩的隐藏误判为仍在显示。
    // 可见判定 = 元素存在、未离屏、且仍有实际尺寸。
    static bool CanSeeVisibleText(AutomationElement scope, string needle)
    {
        foreach (var element in Descendants(scope, ControlType.Text))
        {
            if (!Name(element).Contains(needle)) continue;
            try { if (!element.Current.IsOffscreen && element.Current.BoundingRectangle.Width > 0) return true; }
            catch (Exception) { }
        }
        return false;
    }

    static AutomationElement FindText(AutomationElement scope, string needle)
    {
        foreach (var element in Descendants(scope, ControlType.Text))
            if (Name(element).Contains(needle)) return element;
        return null;
    }

    static string Describe(AutomationElement element)
    {
        if (element == null) return "已从 UIA 树移除";
        try { return "仍在树中（IsOffscreen=" + (element.Current.IsOffscreen ? "True" : "False") +
            "，宽=" + (int)element.Current.BoundingRectangle.Width + "）"; }
        catch (Exception ex) { return "仍在树中（读取属性失败：" + ex.GetType().Name + "）"; }
    }

    static bool WaitVisibleText(AutomationElement scope, string needle, int timeoutMs, int intervalMs = 250)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            if (CanSeeVisibleText(scope, needle)) return true;
            Thread.Sleep(intervalMs);
        } while (Environment.TickCount < end);
        return false;
    }

    static bool WaitVisibleTextGone(AutomationElement scope, string needle, int timeoutMs, int intervalMs = 250)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            if (!CanSeeVisibleText(scope, needle)) return true;
            Thread.Sleep(intervalMs);
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

    // 同理：关闭窗口前若弹确认框，WindowPattern.Close 也可能等待消息框，放到后台线程执行。
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

    // 读取只读编辑框文本：优先 ValuePattern，失败时退回 TextPattern（只读多行 TextBox 的保险路径）。
    static string ReadEditText(AutomationElement element)
    {
        if (element == null) return null;
        string value = Value(element);
        if (!String.IsNullOrEmpty(value)) return value;
        try { return ((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1); }
        catch (Exception) { return null; }
    }

    static bool WaitValue(AutomationElement element, string expected, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            string value = Value(element);
            if (value != null && (expected.Length == 0 ? value.Length == 0 : value.Contains(expected))) return true;
            Thread.Sleep(250);
        } while (Environment.TickCount < end);
        return false;
    }

    static AutomationElement[] Rows(AutomationElement grid)
    {
        if (grid == null) return new AutomationElement[0];
        return Descendants(grid, ControlType.DataItem);
    }

    static bool WaitRows(AutomationElement grid, int count, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            if (Rows(grid).Length == count) return true;
            Thread.Sleep(250);
        } while (Environment.TickCount < end);
        return false;
    }

    static string RowText(AutomationElement row)
    {
        var text = new StringBuilder();
        foreach (var element in Descendants(row, ControlType.Text)) text.Append(Name(element)).Append(' ');
        return text.ToString();
    }

    static AutomationElement WaitWindow(int pid, string titleContains, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, condition))
                if (Name(window).Contains(titleContains)) return window;
            Thread.Sleep(300);
        } while (Environment.TickCount < end);
        return null;
    }

    // 模态对话框（ShowDialog）不出现在 UIA 顶层窗口枚举（RootElement 的子级）里，
    // 它的 UIA 元素会并入主窗口子树（config-probe 实测：打开后主窗口树从 145 涨到 186，
    // 其中就有 ControlType.Window、名称“配置修改”的元素）。因此对话框必须从主窗口向下查找。
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

    static AutomationElement FindDialog(AutomationElement main, string title)
    {
        try
        {
            return main.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.NameProperty, title)));
        }
        catch (Exception) { return null; }
    }

    // 系统 MessageBox 是独立顶层窗口（不同于并入主窗口子树的 WPF ShowDialog），
    // 需从桌面根节点按进程 + 标题查找；按钮名称形如“是(Y)”“否(N)”。
    // 实测模态系统框（类名 #32770）有时不出现在 UIA 顶层枚举中（WaitMessageBox 超时但框实际已弹出并阻塞点击），
    // 故兜底用 Win32 EnumWindows 按进程 + 标题找到 HWND，再转回 UIA 元素供后续子树查找。
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

    static bool WaitMessageBoxGone(int pid, string titleContains, int timeoutMs)
    {
        int end = Environment.TickCount + timeoutMs;
        do
        {
            if (FindMessageBox(pid, titleContains) == null) return true;
            Thread.Sleep(200);
        } while (Environment.TickCount < end);
        return false;
    }

    // ---------- 数据准备 ----------
    static string Ts(int minutesAgo)
    {
        return DateTimeOffset.Now.AddMinutes(-minutesAgo).ToString("o");
    }

    static void WriteFixtures()
    {
        var utf8 = new UTF8Encoding(false);
        string codexSessions = Path.Combine(workRoot, "codex-home", "sessions", "2026-10-03");
        string claudeAlpha = Path.Combine(workRoot, "claude-home", "projects", "E--demo-project-alpha");
        string claudeGamma = Path.Combine(workRoot, "claude-home", "projects", "E--demo-project-gamma");
        Directory.CreateDirectory(codexSessions);
        Directory.CreateDirectory(claudeAlpha);
        Directory.CreateDirectory(claudeGamma);

        // Codex 会话 A：project-alpha，两条累计快照（首条整体计入，第二条按差分）
        File.WriteAllLines(Path.Combine(codexSessions, "rollout-codex-a.jsonl"), new[] {
            "{\"timestamp\":\"" + Ts(60) + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"codex-sess-a\",\"cwd\":\"E:\\\\demo\\\\project-alpha\"}}",
            "{\"timestamp\":\"" + Ts(59) + "\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5-codex\",\"cwd\":\"E:\\\\demo\\\\project-alpha\"}}",
            "{\"timestamp\":\"" + Ts(58) + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"帮我优化登录页的报错提示\"}]}}",
            "{\"timestamp\":\"" + Ts(57) + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"shell\",\"arguments\":\"{}\"}}",
            "{\"timestamp\":\"" + Ts(56) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":400,\"cache_write_input_tokens\":0,\"output_tokens\":300}}}}",
            "{\"timestamp\":\"" + Ts(40) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1600,\"cached_input_tokens\":700,\"cache_write_input_tokens\":0,\"output_tokens\":450}}}}"
        }, utf8);

        // Codex 会话 B：project-beta
        File.WriteAllLines(Path.Combine(codexSessions, "rollout-codex-b.jsonl"), new[] {
            "{\"timestamp\":\"" + Ts(35) + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"codex-sess-b\",\"cwd\":\"E:\\\\demo\\\\project-beta\"}}",
            "{\"timestamp\":\"" + Ts(34) + "\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5-codex\",\"cwd\":\"E:\\\\demo\\\\project-beta\"}}",
            "{\"timestamp\":\"" + Ts(33) + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"把周报导出改成 CSV\"}]}}",
            "{\"timestamp\":\"" + Ts(30) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":800,\"cached_input_tokens\":300,\"cache_write_input_tokens\":0,\"output_tokens\":200}}}}"
        }, utf8);

        // 造一个大日志文件（海量合法但无关的行）：让首次读取持续足够久，能稳定观察到加载遮罩；
        // 这些行不产生任何用量记录，不影响会话与项目数量断言。
        // 行数按实测调过：解析约 1~2µs/行，2M 行把读取撑到约 2 秒，遮罩探测和截图都来得及。
        var noise = new StringBuilder();
        for (int i = 0; i < 2000000; i++) noise.Append("{\"type\":\"noise\"}\n");
        File.WriteAllText(Path.Combine(codexSessions, "rollout-noise.jsonl"), noise.ToString(), utf8);

        // Claude 会话：project-alpha（与 Codex 同项目，验证跨平台合并）
        File.WriteAllLines(Path.Combine(claudeAlpha, "claude-sess-a.jsonl"), new[] {
            "{\"type\":\"user\",\"uuid\":\"u-alpha\",\"sessionId\":\"claude-sess-a\",\"timestamp\":\"" + Ts(20) + "\",\"cwd\":\"E:/demo/project-alpha\",\"message\":{\"role\":\"user\",\"content\":\"帮我写登录页的单元测试\"}}",
            "{\"type\":\"assistant\",\"uuid\":\"a-alpha\",\"sessionId\":\"claude-sess-a\",\"requestId\":\"req-alpha\",\"timestamp\":\"" + Ts(18) + "\",\"cwd\":\"E:/demo/project-alpha\",\"message\":{\"id\":\"msg-alpha\",\"model\":\"claude-sonnet-5-5\",\"usage\":{\"input_tokens\":1000,\"cache_read_input_tokens\":5000,\"cache_creation_input_tokens\":200,\"output_tokens\":800},\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{}}]}}"
        }, utf8);

        // Claude 会话：project-gamma
        File.WriteAllLines(Path.Combine(claudeGamma, "claude-sess-b.jsonl"), new[] {
            "{\"type\":\"user\",\"uuid\":\"u-gamma\",\"sessionId\":\"claude-sess-b\",\"timestamp\":\"" + Ts(15) + "\",\"cwd\":\"E:/demo/project-gamma\",\"message\":{\"role\":\"user\",\"content\":\"重构导出模块\"}}",
            "{\"type\":\"assistant\",\"uuid\":\"a-gamma\",\"sessionId\":\"claude-sess-b\",\"requestId\":\"req-gamma\",\"timestamp\":\"" + Ts(12) + "\",\"cwd\":\"E:/demo/project-gamma\",\"message\":{\"id\":\"msg-gamma\",\"model\":\"claude-sonnet-5-5\",\"usage\":{\"input_tokens\":300,\"cache_read_input_tokens\":600,\"cache_creation_input_tokens\":50,\"output_tokens\":200},\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{}}]}}"
        }, utf8);

        // 当前 Claude 配置（供“配置修改”窗口演练，模拟用户自己的官方配置）
        File.WriteAllText(Path.Combine(workRoot, "claude-home", "settings.json"), original);
    }

    // ---------- 主流程 ----------
    // 控制台自动化客户端用 MTA：与 UIA 官方建议一致，避免跨进程回调时在无消息泵的 STA 上死锁。
    [MTAThread]
    public static int Main()
    {
        try { SetProcessDPIAware(); } catch (Exception) { }
        try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }

        POINT cursor;
        GetCursorPos(out cursor);

        Process process = null;
        try
        {
            string projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(typeof(RealUserSim).Assembly.Location));
            artifacts = Path.Combine(projectRoot, "artifacts");
            Directory.CreateDirectory(artifacts);

            // 1. 隔离沙箱：复制打包版 exe 与日志、配置夹具
            workRoot = Path.Combine(Path.GetTempPath(), "ai-real-user-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            appDir = Path.Combine(workRoot, "app");
            Directory.CreateDirectory(appDir);
            File.Copy(Path.Combine(projectRoot, "bin", "AI Assistant.exe"), Path.Combine(appDir, "AI Assistant.exe"));
            File.Copy(Path.Combine(projectRoot, "bin", "AI Assistant.exe.config"), Path.Combine(appDir, "AI Assistant.exe.config"));
            Console.WriteLine("隔离沙箱：" + workRoot);
            log.AppendLine("隔离沙箱：" + workRoot);

            original = "{\r\n  \"env\": { \"ANTHROPIC_BASE_URL\": \"https://example.invalid\" },\r\n  \"model\": \"sonnet\"\r\n}\r\n";
            modified = original.Replace("\"model\": \"sonnet\"", "\"model\": \"opus\"");
            // 界面保存会把 JSON 重排为两空格缩进（键序与内容不变），以下是重排后的期望全文。
            prettyOriginal = "{\n  \"env\": {\n    \"ANTHROPIC_BASE_URL\": \"https://example.invalid\"\n  },\n  \"model\": \"sonnet\"\n}\n";
            prettyModified = prettyOriginal.Replace("\"model\": \"sonnet\"", "\"model\": \"opus\"");
            WriteFixtures();
            string settingsPath = Path.Combine(workRoot, "claude-home", "settings.json");

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

            // —— 首屏读取遮罩：扫描未完成时数据区应盖遮罩，避免把“正在读取”误当成“没有数据” ——
            // WPF 首次渲染完成前 IsOffscreen 也是 True，所以探测用 100ms 间隔，靠加长的噪声日志把
            // 可见窗口撑到数秒，保证判定既严格（折叠元素骗不过）又稳定（采样不会错过）。
            var scanWatch = Stopwatch.StartNew();
            bool overlaySeen = WaitVisibleText(main, "正在读取本地日志…", 15000, 100);
            Check(overlaySeen, "首次读取期间数据区显示加载遮罩");
            Shot(mainHandle, "real-user-18-loading-overlay.png", 60);
            Observe("拍摄瞬间遮罩仍可见=" + CanSeeVisibleText(main, "正在读取本地日志…"));
            Check(WaitText(main, "更新于", 60000), "本地日志扫描完成（状态栏出现“更新于”）");
            Check(File.Exists(settingsPath + ".ai-assistant.original.bak") && File.ReadAllText(settingsPath + ".ai-assistant.original.bak") == original,
                "软件启动即留档原配置（打开配置修改前已就绪）");
            Observe("首屏加载耗时约 " + scanWatch.ElapsedMilliseconds + " ms（自窗口出现起计，含探测采样延迟）");
            bool overlayGone = WaitVisibleTextGone(main, "正在读取本地日志…", 8000);
            Observe("读取完成后遮罩可见=" + (!overlayGone) + "；元素" + Describe(FindText(main, "正在读取本地日志…")));
            Check(overlayGone, "读取完成后加载遮罩自动消失");

            // —— 手动刷新沿用“界面仍可操作”：不再遮罩，状态栏明示仍在读取 ——
            // 刷新只要一两秒就完成，采样必须轻：预取元素引用，每轮只读单个属性；
            // 遮罩检测（全树查询，最重）每 4 轮做一次。刷新按钮被禁用是最可靠的“确实在刷新”证据。
            var refreshButton = WaitId(main, "RefreshButton", 5000);
            var statusElement = WaitId(main, "StatusText", 5000);
            if (refreshButton == null || statusElement == null) throw new Exception("未找到刷新按钮或状态栏");
            string statusBefore = Name(statusElement);
            Check(Invoke(refreshButton, "刷新数据"), "点击“刷新数据”");
            bool sawWorkingStatus = false, sawOverlayDuringRefresh = false, sawDisabledButton = false;
            string statusTrace = "";
            int ticks = 0;
            int refreshEnd = Environment.TickCount + 20000;
            while (Environment.TickCount < refreshEnd)
            {
                string statusNow = "";
                try { statusNow = Name(statusElement); } catch (Exception) { }
                bool enabled = true;
                try { enabled = refreshButton.Current.IsEnabled; } catch (Exception) { }
                if (!enabled) sawDisabledButton = true;
                string sample = statusNow + (enabled ? "" : "（刷新按钮已禁用）");
                if (statusTrace.IndexOf(sample) < 0) statusTrace += " | " + sample;
                if (statusNow.Contains("界面仍可操作")) sawWorkingStatus = true;
                if ((ticks++ & 3) == 0 && !sawOverlayDuringRefresh && CanSeeVisibleText(main, "正在读取本地日志…"))
                    sawOverlayDuringRefresh = true;
                if ((sawWorkingStatus || sawDisabledButton) && statusNow.Contains("更新于") && statusNow != statusBefore)
                    break; // 刷新完成
                Thread.Sleep(60);
            }
            Observe("手动刷新状态栏轨迹：" + statusTrace.TrimStart(' ', '|'));
            Observe("刷新期间刷新按钮禁用=" + sawDisabledButton);
            Check(sawWorkingStatus, "手动刷新时状态栏提示界面仍可操作");
            Check(!sawOverlayDuringRefresh, "手动刷新不显示遮罩（保留已有数据）");
            Check(WaitText(main, "更新于", 30000), "手动刷新完成后状态栏恢复更新于");

            Check(CanSeeText(main, "总 Token 用量") && CanSeeText(main, "缓存命中率"), "用量概览展示统计卡");
            Shot(mainHandle, "real-user-01-overview.png");

            // 3. 会话明细：4 个会话 → 搜索过滤 → 清空
            Check(Invoke(WaitId(main, "SessionsButton", 8000), "侧栏“会话明细”"), "点击侧栏“会话明细”");
            Check(WaitPage(main, "会话明细", 8000), "页面切换到会话明细");
            var sessionsGrid = WaitType(main, ControlType.DataGrid, "会话用量明细", 8000);
            Check(sessionsGrid != null, "会话明细表格已显示");
            Check(WaitRows(sessionsGrid, 4, 8000), "会话明细列出 4 个会话（Codex 2 + Claude 2）");
            Shot(mainHandle, "real-user-02-sessions.png");

            // —— 新手探索观察 A：未选中任何行时点“查看所选会话”（新手常见动作，观察是否产生困惑）——
            var detailButton = WaitId(main, "DetailButton", 5000);
            if (detailButton != null)
            {
                Observe("未选中行时“查看所选会话”按钮启用=" + IsEnabled(detailButton));
                Check(!IsEnabled(detailButton), "未选中行时“查看所选会话”按钮为禁用态");
                Invoke(detailButton, "查看所选会话（未选中）");
                Thread.Sleep(1200);
                bool popped = FindDialog(main, "会话详情") != null;
                Observe("未选中行点击后 1.2 秒内弹出“会话详情”=" + popped);
                if (popped)
                {
                    // 防御：若未来改为未选中也可查看，先把弹窗关掉，避免模态阻塞后续步骤。
                    try { ((WindowPattern)FindDialog(main, "会话详情").GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch (Exception) { }
                    WaitDialogGone(main, "会话详情", 5000);
                }
                Shot(mainHandle, "real-user-11-click-unselected.png");
            }
            else No("未找到“查看所选会话”按钮");

            // —— 新手探索观察 B：先单击选中首行，再点“查看所选会话”→ 应弹出会话详情 ——
            var sessionRows = Rows(sessionsGrid);
            if (detailButton != null && sessionRows.Length > 0)
            {
                var firstRect = sessionRows[0].Current.BoundingRectangle;
                Check(firstRect.Width > 0 && firstRect.Height > 0, "会话首行为可见行");
                ClickAt(firstRect.Left + firstRect.Width / 2, firstRect.Top + firstRect.Height / 2);
                Thread.Sleep(500);
                bool selected = false;
                try { selected = ((SelectionItemPattern)sessionRows[0].GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected; } catch (Exception) { }
                Observe("单击首行后其选中状态=" + selected);
                Check(IsEnabled(detailButton), "选中行后“查看所选会话”按钮恢复可用");
                Check(Invoke(detailButton, "查看所选会话（已选中）"), "选中行后点击“查看所选会话”");
                var detailDialog = WaitDialog(main, "会话详情", 10000);
                Check(detailDialog != null, "弹出“会话详情”窗口");
                if (detailDialog != null)
                {
                    Thread.Sleep(600);
                    Shot(new IntPtr(detailDialog.Current.NativeWindowHandle), "real-user-12-detail-dialog.png");
                    // 详情窗口应标注恢复命令用途，且命令可直接复制到终端继续该会话。
                    string detailText = ReadEditText(Descendants(detailDialog, ControlType.Edit).FirstOrDefault());
                    Check(detailText != null && detailText.Contains("恢复命令"), "详情窗口标注“恢复命令”用途");
                    Check(detailText != null && (detailText.Contains("claude --resume") || detailText.Contains("codex resume")),
                        "详情窗口显示可复制的恢复命令");
                    try { ((WindowPattern)detailDialog.GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch (Exception) { }
                    Check(WaitDialogGone(main, "会话详情", 8000), "关闭“会话详情”窗口");
                }
            }

            var search = WaitId(main, "SearchBox", 5000);
            if (search == null) search = WaitType(main, ControlType.Edit, "搜索项目、模型或会话", 5000);
            Check(SetValue(search, "搜索框", "project-gamma"), "在搜索框输入 project-gamma");
            Check(WaitValue(search, "project-gamma", 3000), "搜索框已接收输入");
            Check(WaitRows(sessionsGrid, 1, 8000), "搜索 project-gamma 后只剩 1 个会话");
            Shot(mainHandle, "real-user-03-search.png");
            SetValue(search, "搜索框", "");
            Check(WaitRows(sessionsGrid, 4, 8000), "清空搜索恢复 4 个会话");

            // —— 新手探索观察 C：搜索一个打错的词（新手常见），观察空结果提示是否说得通 ——
            SetValue(search, "搜索框", "project-zzz-typo");
            Check(WaitRows(sessionsGrid, 0, 8000), "搜索打错的词后表格为 0 行");
            var emptyText = WaitId(main, "EmptyText", 5000);
            Observe("搜索无结果时空态提示可见=" + (emptyText != null && !emptyText.Current.IsOffscreen) +
                "，文案=\"" + (emptyText == null ? "<未找到>" : Name(emptyText)) + "\"");
            Check(emptyText != null && Name(emptyText).Contains("更换关键词"), "搜索无结果提示引导更换关键词");
            Shot(mainHandle, "real-user-13-empty-search.png");
            SetValue(search, "搜索框", "");
            Check(WaitRows(sessionsGrid, 4, 8000), "清除搜索词后再次恢复 4 个会话");

            // 4. 项目统计：排行 → 双击跳转 → 返回后搜索自动清空
            Check(Invoke(WaitId(main, "ProjectsButton", 8000), "侧栏“项目统计”"), "点击侧栏“项目统计”");
            Check(WaitPage(main, "项目用量统计", 8000), "页面切换到项目统计");
            var projectsGrid = WaitType(main, ControlType.DataGrid, "项目用量排行", 8000);
            Check(projectsGrid != null && WaitRows(projectsGrid, 3, 8000), "项目排行列出 3 个项目");
            Check(CanSeeText(main, "共 3 个项目"), "项目计数文案正确");
            var rowList = Rows(projectsGrid);
            Check(rowList.Length == 3 && RowText(rowList[0]).Contains("project-alpha"), "默认按 Token 总量降序，project-alpha 居首");
            Shot(mainHandle, "real-user-04-projects.png");

            Raise(mainHandle);
            if (rowList.Length > 0)
            {
                var rowRect = rowList[0].Current.BoundingRectangle;
                Check(rowRect.Width > 0 && rowRect.Height > 0, "项目首行为可见行");
                DoubleClickAt(rowRect.Left + rowRect.Width / 2, rowRect.Top + rowRect.Height / 2);
                if (!WaitPage(main, "会话明细", 4000))
                {
                    // 窗口未处于前台时首击可能被激活吸收，补一次双击再等（真实用户也会重复双击）。
                    DoubleClickAt(rowRect.Left + rowRect.Width / 2, rowRect.Top + rowRect.Height / 2);
                }
            }
            else No("项目行为空，无法双击跳转");
            var jumpSearch = WaitId(main, "SearchBox", 5000);
            Check(WaitPage(main, "会话明细", 8000), "双击项目跳到会话明细页");
            Check(WaitValue(jumpSearch, "project-alpha", 8000), "跳转时搜索框带入项目路径");
            Check(WaitRows(sessionsGrid, 2, 8000), "跳转后只显示 project-alpha 的 2 个会话");
            Shot(mainHandle, "real-user-05-jump.png");

            Check(Invoke(WaitId(main, "ProjectsButton", 8000), "侧栏“项目统计”"), "点击“项目统计”返回项目页");
            Check(WaitPage(main, "项目用量统计", 8000), "页面切回项目统计");
            Check(WaitValue(jumpSearch, "", 8000), "返回项目页后搜索框已自动清空");
            Check(WaitRows(projectsGrid, 3, 8000), "返回后项目排行恢复 3 行");

            // —— 单实例竞态保护：程序运行中再启动一个相同数据目录的实例（相当于用户再双击一次图标或后台方式启动），
            // 应弹出“已在运行”提示，用户确认后以退出码 2 退出，原实例不受影响 ——
            var duplicate = Process.Start(startInfo);
            var notice = WaitMessageBox(duplicate.Id, "AI Assistant", 15000);
            Check(notice != null, "第二个实例弹出「已在运行」提示框");
            if (notice != null)
            {
                Check(WaitText(notice, "已在运行", 5000) && WaitText(notice, "任务栏", 5000), "提示框说明程序已在运行并引导切回已打开的窗口");
                Shot(new IntPtr(notice.Current.NativeWindowHandle), "real-user-22-single-instance.png", 100);
                Check(Invoke(WaitType(notice, ControlType.Button, "确定", 8000), "提示框“确定”"), "点击提示框“确定”");
            }
            bool duplicateExited = duplicate.WaitForExit(8000);
            if (!duplicateExited) duplicate.Kill();
            Check(duplicateExited && duplicate.ExitCode == 2, "确认提示后第二个实例退出（退出码 2）");
            Check(!main.Current.IsOffscreen, "原实例界面不受影响");

            // 5. 配置修改窗口：保存 → 直接启用 → 编辑 → 校验 → 保存并启用 → 无效拦截 → 恢复
            Check(Invoke(WaitId(main, "ClaudeSettingsButton", 8000), "侧栏“配置修改”"), "点击侧栏“配置修改”");
            var dialog = WaitDialog(main, "配置修改", 10000);
            Check(dialog != null, "配置修改窗口已打开");
            if (dialog == null) return Finish(process);
            IntPtr dialogHandle = new IntPtr(dialog.Current.NativeWindowHandle);

            var editor = WaitType(dialog, ControlType.Edit, "Claude 官方配置 JSON", 10000);
            var nameBox = WaitType(dialog, ControlType.Edit, "配置名称", 10000);
            Check(editor != null && Value(editor) == original, "编辑器展示当前 settings.json 全文");
            Check(File.Exists(settingsPath + ".ai-assistant.original.bak") && File.ReadAllText(settingsPath + ".ai-assistant.original.bak") == original,
                "打开配置界面即已把原配置留档");
            Check(nameBox != null && Value(nameBox) == "", "尚未选择已保存配置（名称框为空）");
            Shot(dialogHandle, "real-user-06-config-open.png");

            // —— 空状态：还没有任何已保存配置时，直接显示文字提示、不出现下拉框（也就不会弹出空白细条） ——
            var configsCombo = FindType(dialog, ControlType.ComboBox, "已保存的配置");
            bool comboVisible = false;
            try { comboVisible = configsCombo != null && !configsCombo.Current.IsOffscreen && configsCombo.Current.BoundingRectangle.Width > 0; }
            catch (Exception) { }
            Check(!comboVisible, "没有已保存配置时不出现下拉框");
            Check(CanSeeVisibleText(dialog, "暂无已保存的配置"), "没有已保存配置时直接显示提示文字");
            Observe("空配置时下拉框元素=" + (configsCombo == null ? "未找到" : "在树中") + "，可见=" + comboVisible);
            Shot(dialogHandle, "real-user-17-config-dropdown-empty.png");

            SetValue(nameBox, "配置名称框", "模拟用户配置");
            Check(Invoke(WaitType(dialog, ControlType.Button, "仅保存", 8000), "仅保存"), "点击“仅保存”");
            Check(WaitType(dialog, ControlType.Button, "启用此配置", 10000) != null, "保存后按钮变为“启用此配置”");
            // 保存第一个配置后，下拉框应重新出现、空态提示消失。
            bool comboBack = false;
            for (int i = 0; i < 20 && !comboBack; i++)
            {
                try { comboBack = configsCombo != null && !configsCombo.Current.IsOffscreen && configsCombo.Current.BoundingRectangle.Width > 0; }
                catch (Exception) { }
                if (!comboBack) Thread.Sleep(100);
            }
            Check(comboBack && !CanSeeVisibleText(dialog, "暂无已保存的配置"), "保存配置后下拉框恢复显示、提示文字消失");
            string configPath = Path.Combine(appDir, "claude-configs", "模拟用户配置.json");
            Check(File.Exists(configPath) && File.ReadAllText(configPath) == prettyOriginal, "配置另存为独立文件并重排为两空格缩进，与编辑器一致");
            Shot(dialogHandle, "real-user-07-unchanged.png");

            Check(Invoke(WaitType(dialog, ControlType.Button, "启用此配置", 8000), "启用此配置"), "点击“启用此配置”");
            Check(WaitText(dialog, "已启用", 10000), "状态栏提示已启用");
            Check(File.ReadAllText(settingsPath) == prettyOriginal, "启用后 settings.json 完整写入编辑器内容");
            string originalBackupPath = settingsPath + ".ai-assistant.original.bak";
            Check(File.Exists(originalBackupPath) && File.ReadAllText(originalBackupPath) == original, "原配置留档在启用后仍逐字节保留");

            SetValue(editor, "配置编辑器", modified);
            Check(WaitType(dialog, ControlType.Button, "保存并启用", 5000) != null, "修改内容后按钮恢复“保存并启用”");
            Shot(dialogHandle, "real-user-08-edited.png");
            Check(Invoke(WaitType(dialog, ControlType.Button, "检查格式", 8000), "检查格式"), "点击“检查格式”（有效内容）");
            Check(WaitText(dialog, "检查通过", 8000), "格式检查通过");

            Check(Invoke(WaitType(dialog, ControlType.Button, "保存并启用", 8000), "保存并启用"), "点击“保存并启用”");
            Check(WaitText(dialog, "已完整写入", 8000), "状态栏提示已完整写入");
            Check(File.ReadAllText(settingsPath) == prettyModified, "settings.json 已更新为修改后内容");
            Check(File.ReadAllText(configPath) == prettyModified, "已保存配置同步更新");

            SetValue(editor, "配置编辑器", "{\r\n  \"env\": 123\r\n}\r\n");
            Check(Invoke(WaitType(dialog, ControlType.Button, "检查格式", 8000), "检查格式"), "点击“检查格式”（无效内容）");
            Check(WaitText(dialog, "env", 8000), "无效 env 类型给出提示");
            Invoke(WaitType(dialog, ControlType.Button, "保存并启用", 8000), "保存并启用");
            Thread.Sleep(800);
            Check(File.ReadAllText(settingsPath) == prettyModified, "无效内容不会写入 settings.json");
            Shot(dialogHandle, "real-user-09-invalid.png");

            SetValue(editor, "配置编辑器", modified);
            var restoreButton = WaitType(dialog, ControlType.Button, "恢复原配置", 8000);
            InvokeAsync(restoreButton, "恢复原配置");
            Check(restoreButton != null, "点击“恢复原配置”");
            var restoreBox = WaitMessageBox(process.Id, "恢复原配置", 5000);
            Check(restoreBox != null, "恢复原配置前弹出二次确认框");
            if (restoreBox != null) Shot(new IntPtr(restoreBox.Current.NativeWindowHandle), "real-user-19-restore-confirm.png", 100);
            Check(Invoke(WaitType(restoreBox, ControlType.Button, "是", 5000), "确认恢复"), "点击“是”确认恢复原配置");
            Check(WaitText(dialog, "已恢复", 8000), "状态栏提示已恢复原配置");
            Check(File.ReadAllText(settingsPath) == original, "settings.json 还原为最初留档的原配置");
            Shot(dialogHandle, "real-user-10-restored.png");

            // —— 未保存编辑保护：“新建配置”把草稿固化为初稿（所见即所得、不弹确认、不丢内容）；再次改动后关闭窗口仍先确认 ——
            SetValue(editor, "配置编辑器", modified + "\n// 尚未保存的草稿");
            var newButton = WaitType(dialog, ControlType.Button, "新建配置", 8000);
            Check(newButton != null, "配置选择行提供“新建配置”入口");
            InvokeAsync(newButton, "新建配置");
            Thread.Sleep(800);
            Check(FindMessageBox(process.Id, "丢弃未保存的修改") == null, "“新建配置”不弹丢弃确认框");
            Check(Value(editor) != null && Value(editor).Contains("尚未保存的草稿") && Value(nameBox) == "",
                "草稿原样成为新配置初稿并进入新建状态（名称清空）");
            Shot(dialogHandle, "real-user-21-new-from-draft.png");

            // 6. 关闭配置窗口与主窗口（再次改动后有草稿，关闭前先确认；“否”保留窗口，确认后关闭）
            SetValue(editor, "配置编辑器", modified + "\n// 关闭前的又一处修改");
            CloseAsync(dialog);
            var closeBox = WaitMessageBox(process.Id, "丢弃未保存的修改", 5000);
            Check(closeBox != null, "关闭窗口前提示存在未保存修改");
            if (closeBox != null) Shot(new IntPtr(closeBox.Current.NativeWindowHandle), "real-user-20-unsaved-guard.png", 100);
            Check(Invoke(WaitType(closeBox, ControlType.Button, "否", 5000), "拒绝丢弃修改"), "点击“否”取消关闭");
            Check(WaitMessageBoxGone(process.Id, "丢弃未保存的修改", 5000), "确认框已关闭");
            Check(WaitDialog(main, "配置修改", 3000) != null, "取消关闭后配置窗口保留");
            CloseAsync(dialog);
            var closeBox2 = WaitMessageBox(process.Id, "丢弃未保存的修改", 5000);
            Check(closeBox2 != null, "再次关闭仍提示存在未保存修改");
            Check(Invoke(WaitType(closeBox2, ControlType.Button, "是", 5000), "确认丢弃并关闭"), "点击“是”确认关闭");
            Check(WaitDialogGone(main, "配置修改", 8000), "配置修改窗口已关闭");
            try { ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch (Exception) { }
            Check(process.WaitForExit(10000), "关闭主窗口后程序正常退出");

            // 7. 新手探索观察 D：全新环境（没有任何用量记录）的首屏体验
            ObserveEmptyEnvironment();

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

    // 新手探索观察 D：全新环境（没有任何用量日志）的首屏体验——空态文案与按钮启停。
    static void ObserveEmptyEnvironment()
    {
        string emptyRoot = Path.Combine(Path.GetTempPath(), "ai-real-user-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-empty");
        string emptyApp = Path.Combine(emptyRoot, "app");
        Process process = null;
        try
        {
            string projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(typeof(RealUserSim).Assembly.Location));
            Directory.CreateDirectory(emptyApp);
            File.Copy(Path.Combine(projectRoot, "bin", "AI Assistant.exe"), Path.Combine(emptyApp, "AI Assistant.exe"));
            File.Copy(Path.Combine(projectRoot, "bin", "AI Assistant.exe.config"), Path.Combine(emptyApp, "AI Assistant.exe.config"));
            Directory.CreateDirectory(Path.Combine(emptyRoot, "codex-home", "sessions"));
            Directory.CreateDirectory(Path.Combine(emptyRoot, "claude-home", "projects"));
            Directory.CreateDirectory(Path.Combine(emptyRoot, "localappdata"));

            var startInfo = new ProcessStartInfo(Path.Combine(emptyApp, "AI Assistant.exe"));
            startInfo.WorkingDirectory = emptyApp;
            startInfo.UseShellExecute = false;
            startInfo.EnvironmentVariables["CODEX_HOME"] = Path.Combine(emptyRoot, "codex-home");
            startInfo.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = Path.Combine(emptyRoot, "claude-home");
            startInfo.EnvironmentVariables["LOCALAPPDATA"] = Path.Combine(emptyRoot, "localappdata");
            process = Process.Start(startInfo);

            var main = WaitWindow(process.Id, "AI Assistant", 30000);
            if (main == null) { Observe("空环境：主窗口未出现"); return; }
            // 空目录扫描很快，留足首屏稳定时间。
            Thread.Sleep(9000);
            IntPtr handle = new IntPtr(main.Current.NativeWindowHandle);
            var status = FindById(main, "StatusText");
            Observe("空环境状态栏 = \"" + (status == null ? "<未找到>" : Name(status)) + "\"");
            Observe("空环境按钮：导出=" + (IsEnabled(FindById(main, "ExportButton")) ? "启用" : "禁用") +
                "，查看所选会话=" + (IsEnabled(FindById(main, "DetailButton")) ? "启用" : "禁用"));
            Shot(handle, "real-user-14-empty-overview.png");

            Invoke(WaitId(main, "SessionsButton", 8000), "侧栏“会话明细”");
            WaitPage(main, "会话明细", 8000);
            var emptyText = WaitId(main, "EmptyText", 5000);
            Observe("空环境会话页空态提示 = \"" + (emptyText == null ? "<未找到>" : Name(emptyText)) + "\"，可见=" +
                (emptyText != null && !emptyText.Current.IsOffscreen));
            Shot(handle, "real-user-15-empty-sessions.png");

            Invoke(WaitId(main, "ProjectsButton", 8000), "侧栏“项目统计”");
            WaitPage(main, "项目用量统计", 8000);
            var projectsEmpty = WaitId(main, "ProjectsEmpty", 5000);
            Observe("空环境项目页空态提示 = \"" + (projectsEmpty == null ? "<未找到>" : Name(projectsEmpty)) + "\"，可见=" +
                (projectsEmpty != null && !projectsEmpty.Current.IsOffscreen));
            Shot(handle, "real-user-16-empty-projects.png");

            try { ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch (Exception) { }
            process.WaitForExit(8000);
        }
        catch (Exception ex)
        {
            Observe("空环境观察失败：" + ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            if (process != null && !process.HasExited)
            {
                try { process.CloseMainWindow(); } catch (Exception) { }
                if (!process.WaitForExit(3000)) { try { process.Kill(); } catch (Exception) { } }
            }
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
        Console.WriteLine("真实用户模拟：{0} 项通过，{1} 项失败。", passed, failed);
        log.AppendLine();
        log.AppendLine("真实用户模拟：" + passed + " 项通过，" + failed + " 项失败。");
        Console.WriteLine("隔离沙箱保留在：" + workRoot);
        try { File.WriteAllText(Path.Combine(artifacts, "real-user-sim.log"), log.ToString(), new UTF8Encoding(false)); } catch (Exception) { }
        return failed == 0 ? 0 : 1;
    }
}
