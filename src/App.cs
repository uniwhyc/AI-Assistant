using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("AI Assistant")]
[assembly: AssemblyProduct("AI Assistant")]

namespace AI_Assistant
{
    public sealed partial class Desktop
    {
        public readonly Window Window;
        SourcePaths paths = SourcePaths.Defaults();
        ScanResult result = new ScanResult();
        List<Usage> filtered = new List<Usage>();
        List<SessionRow> sessions = new List<SessionRow>();
        string selectedPlatform = "全部平台";
        bool ready, busy, syncingRange, syncingLists;
        DateTime? rangeStart;
        DateTime rangeEnd = DateTime.Today.AddDays(1).AddTicks(-1);
        readonly DispatcherTimer searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly string settingsFile = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Assistant", "settings.json");

        public Desktop()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(stream);
            DarkTitleBar(Window);
            try
            {
                if (File.Exists(settingsFile))
                {
                    var saved = new JavaScriptSerializer().Deserialize<SourcePaths>(File.ReadAllText(settingsFile));
                    if (saved != null && !String.IsNullOrWhiteSpace(saved.Codex) && !String.IsNullOrWhiteSpace(saved.Claude)) paths = saved;
                }
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException) && !(ex is InvalidOperationException)) throw;
                result.Warn("数据源设置无法读取，已使用默认目录。");
            }
            Find<DatePicker>("StartDate").SelectedDate = DateTime.Today;
            Find<DatePicker>("EndDate").SelectedDate = DateTime.Today;
            Find<Button>("RefreshButton").Click += async (s, e) => await Refresh();
            Find<Button>("ExportButton").Click += (s, e) => Export();
            Find<Button>("SourcesButton").Click += (s, e) => Sources();
            Find<Button>("ClaudeSettingsButton").Click += (s, e) => ClaudeSettings();
            Find<Button>("RulesButton").Click += (s, e) => ShowText("统计口径", Rules);
            Find<Button>("OverviewButton").Click += (s, e) => Navigate(false);
            Find<Button>("SessionsButton").Click += (s, e) => Navigate(true);
            Find<Button>("DetailButton").Click += (s, e) => Details();
            Find<DataGrid>("SessionsGrid").MouseDoubleClick += (s, e) => Details();
            Find<DataGrid>("SessionsGrid").KeyDown += (s, e) => { if (e.Key == Key.Enter) { Details(); e.Handled = true; } };
            Find<ComboBox>("PlatformFilter").SelectionChanged += (s, e) => Apply();
            Find<ComboBox>("PeriodFilter").SelectionChanged += (s, e) => { SyncPreset(); Apply(); };
            Find<DatePicker>("StartDate").SelectedDateChanged += (s, e) => TimeRangeEdited();
            Find<DatePicker>("EndDate").SelectedDateChanged += (s, e) => TimeRangeEdited();
            WireTimeCombo(Find<ComboBox>("StartTime"));
            WireTimeCombo(Find<ComboBox>("EndTime"));
            Find<TextBox>("SearchBox").TextChanged += (s, e) => {
                Find<TextBlock>("SearchHint").Visibility = Find<TextBox>("SearchBox").Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                searchTimer.Stop(); searchTimer.Start();
            };
            searchTimer.Tick += (s, e) => { searchTimer.Stop(); Apply(); };
            Find<Canvas>("TrendCanvas").SizeChanged += (s, e) => DrawChart();
            Window.PreviewKeyDown += async (s, e) => {
                if (e.Key == Key.F5) { await Refresh(); e.Handled = true; }
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { Find<TextBox>("SearchBox").Focus(); e.Handled = true; }
            };
            Navigate(false);
            ready = true;
        }

        T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        static Brush Brush(string color) { return (Brush)new BrushConverter().ConvertFromString(color); }
        public static string Compact(long value)
        {
            if (value >= 1000000000) return (value / 1000000000.0).ToString("0.00") + " B";
            if (value >= 1000000) return (value / 1000000.0).ToString("0.00") + " M";
            if (value >= 1000) return (value / 1000.0).ToString("0.0") + " K";
            return value.ToString("N0");
        }
        void Metric(string name, long value)
        { Text(name, Compact(value)); Find<TextBlock>(name).ToolTip = value.ToString("N0") + " Token"; }

        public async Task Refresh()
        {
            if (busy) return;
            busy = true;
            Find<Button>("RefreshButton").IsEnabled = false;
            Find<Button>("SourcesButton").IsEnabled = false;
            Text("StatusText", "正在读取本地日志，界面仍可操作…");
            var selectedPaths = new SourcePaths { Codex = paths.Codex, Claude = paths.Claude };
            try
            {
                var scan = await Task.Run(() => UsageReader.Scan(selectedPaths, count => Window.Dispatcher.BeginInvoke(new Action(() =>
                    Text("StatusText", String.Format("正在读取本地日志 · 已处理 {0:N0} 个文件…", count))))));
                SetResult(scan);
            }
            catch (Exception ex)
            {
                Text("StatusText", "读取未完成，已保留上次结果。请检查数据源目录后重试。");
                ShowText("读取日志失败", "错误类型：" + ex.GetType().Name + "\n\n" + ex.Message);
            }
            finally
            {
                busy = false;
                Find<Button>("RefreshButton").IsEnabled = true;
                Find<Button>("SourcesButton").IsEnabled = true;
            }
        }

        public void SetResult(ScanResult scan)
        {
            result = scan;
            SyncPreset();
            Apply();
            Text("StatusText", String.Format("更新于 {0:HH:mm:ss}  ·  {1:N0} 个日志文件  ·  {2:N0} 条用量记录", DateTime.Now, result.Files, result.Records.Count));
        }

        void Navigate(bool detail)
        {
            Text("PageTitle", detail ? "会话明细" : "用量概览");
            Text("PageSubtitle", detail ? "按项目、模型或会话检索，查看每次编程会话的用量。" : "集中查看你的 AI 编程用量，了解缓存带来的复用效果。");
            Find<StackPanel>("OverviewPanels").Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
            Find<DataGrid>("SessionsGrid").Height = detail ? 490 : 245;
            foreach (string name in new[] { "OverviewButton", "SessionsButton" })
            {
                bool selected = (name == "SessionsButton") == detail;
                Find<Button>(name).Background = Brush(selected ? "#243A33" : "Transparent");
                Find<Button>(name).Foreground = Brush(selected ? "#71E3BF" : "#B8C1CB");
            }
        }

        void SyncPreset()
        {
            int period = Find<ComboBox>("PeriodFilter").SelectedIndex;
            if (!ready || period == 4) return;
            DateTime today = DateTime.Today;
            DateTime start = period == 1 ? today.AddDays(-6) : period == 2 ? today.AddDays(-29)
                : period == 3 && result.Records.Count > 0 ? result.Records.Min(x => x.Time.LocalDateTime.Date) : today;
            syncingRange = true;
            try
            {
                Find<DatePicker>("StartDate").SelectedDate = start;
                Find<ComboBox>("StartTime").Text = "00:00:00";
                Find<DatePicker>("EndDate").SelectedDate = today;
                Find<ComboBox>("EndTime").Text = "23:59:59";
            }
            finally { syncingRange = false; }
        }

        void TimeRangeEdited()
        {
            if (!ready || syncingRange) return;
            if (Find<ComboBox>("PeriodFilter").SelectedIndex != 4)
                Find<ComboBox>("PeriodFilter").SelectedIndex = 4;
            else Apply();
        }

        // 时分秒同一下拉框：关闭态可输入 HH:mm:ss，打开态为时/分/秒三列，以“:”分隔。
        void WireTimeCombo(ComboBox combo)
        {
            // 可编辑 ComboBox 无 TextChanged 事件，且内部文本框在模板应用时才同步文本（会绕过 syncingRange 守卫）。
            // 改为监听 Text 依赖属性变化：程序化赋值与输入均同步触发，受 syncingRange/syncingLists 保护。
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox))
                .AddValueChanged(combo, (s, e) => TimeRangeEdited());
            combo.DropDownOpened += (s, e) => SyncTimeList(combo);
            bool wired = false;
            EventHandler layout = null;
            layout = (s, e) =>
            {
                if (wired || combo.Template == null) return;
                ListBox hour = combo.Template.FindName("PART_HourList", combo) as ListBox;
                ListBox minute = hour == null ? null : combo.Template.FindName("PART_MinuteList", combo) as ListBox;
                ListBox second = minute == null ? null : combo.Template.FindName("PART_SecondList", combo) as ListBox;
                if (second == null) return;
                wired = true;
                hour.ItemsSource = Enumerable.Range(0, 24).Select(i => i.ToString("00")).ToList();
                minute.ItemsSource = Enumerable.Range(0, 60).Select(i => i.ToString("00")).ToList();
                second.ItemsSource = Enumerable.Range(0, 60).Select(i => i.ToString("00")).ToList();
                hour.SelectionChanged += (s2, e2) => TimeFromList(combo, hour, minute, second);
                minute.SelectionChanged += (s2, e2) => TimeFromList(combo, hour, minute, second);
                second.SelectionChanged += (s2, e2) => TimeFromList(combo, hour, minute, second);
                combo.LayoutUpdated -= layout;
            };
            combo.LayoutUpdated += layout;
        }

        void SyncTimeList(ComboBox combo)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact(combo.Text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                || combo.Template == null) return;
            ListBox hour = combo.Template.FindName("PART_HourList", combo) as ListBox;
            ListBox minute = hour == null ? null : combo.Template.FindName("PART_MinuteList", combo) as ListBox;
            ListBox second = minute == null ? null : combo.Template.FindName("PART_SecondList", combo) as ListBox;
            if (second == null) return;
            syncingLists = true;
            try
            {
                hour.SelectedItem = parsed.ToString("HH");
                minute.SelectedItem = parsed.ToString("mm");
                second.SelectedItem = parsed.ToString("ss");
                hour.ScrollIntoView(hour.SelectedItem);
                minute.ScrollIntoView(minute.SelectedItem);
                second.ScrollIntoView(second.SelectedItem);
            }
            finally { syncingLists = false; }
        }

        void TimeFromList(ComboBox combo, ListBox hour, ListBox minute, ListBox second)
        {
            if (!ready || syncingLists) return;
            DateTime parsed;
            bool ok = DateTime.TryParseExact(combo.Text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
            string h = hour.SelectedItem as string, m = minute.SelectedItem as string, s = second.SelectedItem as string;
            if (h == null) h = ok ? parsed.ToString("HH") : null;
            if (m == null) m = ok ? parsed.ToString("mm") : null;
            if (s == null) s = ok ? parsed.ToString("ss") : null;
            if (h == null || m == null || s == null) return;
            combo.Text = h + ":" + m + ":" + s; // 赋值即触发上面的 Text 值变更事件
        }

        public void Apply()
        {
            if (!ready || syncingRange) return;
            string warning = String.Join("\n", result.Warnings);
            rangeStart = Find<DatePicker>("StartDate").SelectedDate;
            var end = Find<DatePicker>("EndDate").SelectedDate;
            DateTime startTime, endTime;
            bool validStart = DateTime.TryParseExact(Find<ComboBox>("StartTime").Text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out startTime);
            bool validEnd = DateTime.TryParseExact(Find<ComboBox>("EndTime").Text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out endTime);
            bool invalidDates = !rangeStart.HasValue || !end.HasValue || !validStart || !validEnd;
            if (invalidDates) warning = "请选择有效日期，并按 HH:mm:ss 输入时间（24 小时制），例如 09:30:00。";
            else
            {
                rangeStart = rangeStart.Value.Date.Add(startTime.TimeOfDay);
                rangeEnd = end.Value.Date.Add(endTime.TimeOfDay);
                invalidDates = rangeStart.Value > rangeEnd;
                if (invalidDates) warning = "开始时间不能晚于结束时间，请检查日期和时分秒。";
            }
            Find<Border>("Notice").Visibility = warning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Text("NoticeText", warning);
            selectedPlatform = (string)((ComboBoxItem)Find<ComboBox>("PlatformFilter").SelectedItem).Content;
            filtered = invalidDates ? new List<Usage>() : Analytics.Filter(result.Records, selectedPlatform, rangeStart, rangeEnd, Find<TextBox>("SearchBox").Text);
            var total = Totals.From(filtered);
            Metric("TotalValue", total.Total); Metric("InputValue", total.AllInput); Metric("OutputValue", total.Output);
            Text("SessionCount", String.Format("{0} · {1:N0} 个会话", selectedPlatform, total.Sessions));
            Text("HitValue", total.HitRate);
            Metric("NormalValue", total.Input); Metric("ReadValue", total.CacheRead); Metric("WriteValue", total.CacheWrite);
            PlatformSummary("Codex", "Codex"); PlatformSummary("Claude Code", "Claude");
            Find<System.Windows.Controls.Primitives.UniformGrid>("PlatformCards").Columns = selectedPlatform == "全部平台" ? 2 : 1;
            DrawComposition(total);
            sessions = Analytics.Sessions(filtered);
            Find<DataGrid>("SessionsGrid").ItemsSource = sessions;
            Text("TableCount", String.Format("{0} · 共 {1:N0} 个会话 · 点击列标题排序", selectedPlatform, sessions.Count));
            Find<TextBlock>("EmptyText").Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("ExportButton").IsEnabled = sessions.Count > 0;
            Find<Button>("DetailButton").IsEnabled = sessions.Count > 0;
            DrawChart();
        }

        void PlatformSummary(string platform, string prefix)
        {
            var visibility = selectedPlatform == "全部平台" || selectedPlatform == platform ? Visibility.Visible : Visibility.Collapsed;
            Find<Border>(prefix + "Card").Visibility = visibility;
            Find<TextBlock>(prefix + "Legend").Visibility = visibility;
            var total = Totals.From(filtered.Where(x => x.Platform == platform));
            Metric(prefix + "Total", total.Total);
            Text(prefix + "Detail", String.Format("{0:N0} 个会话 · 输出 {1}", total.Sessions, Compact(total.Output)));
            Text(prefix + "Hit", "缓存命中率  " + total.HitRate);
        }

        void DrawComposition(Totals total)
        {
            var bar = Find<Grid>("CompositionBar");
            bar.Children.Clear(); bar.ColumnDefinitions.Clear();
            long[] values = { total.Input, total.CacheRead, total.CacheWrite };
            string[] colors = { "#8193AB", "#71E3BF", "#EFB18A" };
            for (int i = 0; i < 3; i++)
            {
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(total.AllInput == 0 ? 1 : (double)values[i] / total.AllInput, GridUnitType.Star) });
                var segment = new Border { Background = Brush(total.AllInput == 0 ? "#303841" : colors[i]) };
                Grid.SetColumn(segment, i); bar.Children.Add(segment);
            }
        }

        void Details()
        {
            var row = Find<DataGrid>("SessionsGrid").SelectedItem as SessionRow;
            if (row == null) return;
            // 顶部单行显示恢复对话命令，直接复制到终端即可继续该会话。
            string resume = row.Platform == "Codex"
                ? "codex resume " + row.Session
                : "claude --resume " + row.Session;
            ShowText("会话详情", String.Format("{0}\n\n项目：{1}\n模型：{2}\n最近活动：{3:yyyy-MM-dd HH:mm:ss}\n用户请求：{4:N0} 次（你发出的消息）\n工具调用：{5:N0} 次（模型发起的调用）\n\n总 Token：{6:N0}\n普通输入：{7:N0}\n缓存读取：{8:N0}\n缓存写入：{9:N0}\n输出：{10:N0}\n缓存命中率：{11}\n\n以上数值仅包含当前筛选范围内的记录。",
                resume, row.ProjectPath, row.Model, row.Last, row.UserRequests, row.ToolCalls, row.Total, row.Input, row.CacheRead, row.CacheWrite, row.Output, row.HitRate));
        }

        void Export()
        {
            var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = "用量明细-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv", OverwritePrompt = true };
            if (dialog.ShowDialog(Window) != true) return;
            try
            {
                using (var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true))) CsvExport.Write(writer, sessions);
                Text("StatusText", String.Format("已导出 {0:N0} 个会话，范围与当前筛选一致。", sessions.Count));
            }
            catch (IOException ex) { ShowText("导出失败", ex.Message); }
            catch (UnauthorizedAccessException ex) { ShowText("导出失败", ex.Message); }
        }

        Window Dialog(string title, double width, double height)
        {
            var dialog = new Window { Title = title, Width = width, Height = height, MinWidth = width, MinHeight = height,
                Owner = Window, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#191D22"),
                Foreground = Brush("#F0F2F4"), FontFamily = Window.FontFamily, FontSize = 13, Resources = Window.Resources };
            DarkTitleBar(dialog);
            return dialog;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        static void DarkTitleBar(Window window)
        {
            window.SourceInitialized += (s, e) => {
                int enabled = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
            };
        }

        void ShowText(string title, string content)
        {
            var dialog = Dialog(title, 630, 570);
            var panel = new DockPanel { Margin = new Thickness(24) };
            var close = new Button { Content = "关闭", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            close.Click += (s, e) => dialog.Close();
            DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
            panel.Children.Add(new TextBox { Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = Brushes.Transparent });
            dialog.Content = panel; dialog.ShowDialog();
        }

        void Sources()
        {
            var dialog = Dialog("数据源设置", 710, 440);
            var panel = new StackPanel { Margin = new Thickness(26) };
            panel.Children.Add(new TextBlock { Text = "连接本地使用记录", FontSize = 22, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "选择配置根目录。只读取 JSONL 用量字段，不修改日志、不上传数据。", Foreground = Brush("#A1AAB4"), Margin = new Thickness(0, 10, 0, 20) });
            TextBox codex = SourceField(panel, "Codex 根目录（包含 sessions / archived_sessions）", paths.Codex);
            TextBox claude = SourceField(panel, "Claude Code 根目录（包含 projects）", paths.Claude);
            var error = new TextBlock { Foreground = Brush("#EFB18A"), Margin = new Thickness(0, 8, 0, 8) };
            panel.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var reset = new Button { Content = "恢复默认目录", Margin = new Thickness(0, 0, 10, 0) };
            reset.Click += (s, e) => { var defaults = SourcePaths.Defaults(); codex.Text = defaults.Codex; claude.Text = defaults.Claude; };
            var save = new Button { Content = "保存并读取", Background = Brush("#71E3BF"), Foreground = Brush("#132920"), IsDefault = true };
            save.Click += async (s, e) => {
                if (String.IsNullOrWhiteSpace(codex.Text) || String.IsNullOrWhiteSpace(claude.Text)) { error.Text = "请填写两个配置根目录。"; return; }
                var updated = new SourcePaths { Codex = codex.Text.Trim(), Claude = claude.Text.Trim() };
                try
                {
                    if (!Path.IsPathRooted(updated.Codex) || !Path.IsPathRooted(updated.Claude)) { error.Text = "请使用绝对路径，例如 C:\\Users\\用户名\\.codex。"; return; }
                    Directory.CreateDirectory(Path.GetDirectoryName(settingsFile));
                    File.WriteAllText(settingsFile, new JavaScriptSerializer().Serialize(updated), Encoding.UTF8);
                    paths = updated; dialog.Close(); await Refresh();
                }
                catch (IOException) { error.Text = "设置保存失败，请检查配置目录的写入权限。"; }
                catch (UnauthorizedAccessException) { error.Text = "无权保存设置，请检查配置目录的写入权限。"; }
                catch (ArgumentException) { error.Text = "目录格式无效，请重新选择。"; }
            };
            buttons.Children.Add(reset); buttons.Children.Add(save); panel.Children.Add(buttons);
            dialog.Content = panel; dialog.ShowDialog();
            RestorePageSelection();
        }

        void RestorePageSelection()
        {
            bool detail = Find<StackPanel>("OverviewPanels").Visibility == Visibility.Collapsed;
            Navigate(detail);
            var selected = Find<Button>(detail ? "SessionsButton" : "OverviewButton");
            FocusManager.SetFocusedElement(Window, selected);
            selected.Focus();
        }

        TextBox SourceField(Panel panel, string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBox { Text = value, Margin = new Thickness(0, 0, 10, 0) };
            System.Windows.Automation.AutomationProperties.SetName(text, label);
            var browse = new Button { Content = "浏览…" }; Grid.SetColumn(browse, 1);
            browse.Click += (s, e) => {
                using (var picker = new System.Windows.Forms.FolderBrowserDialog { Description = label, SelectedPath = text.Text, ShowNewFolderButton = false })
                    if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) text.Text = picker.SelectedPath;
            };
            grid.Children.Add(text); grid.Children.Add(browse); panel.Children.Add(grid); return text;
        }

        public const string Rules = "总 Token = 普通输入 + 缓存读取 + 缓存写入 + 输出。\n\n缓存命中率 = 缓存读取 Token ÷ 全部输入 Token。聚合时先累加 Token 再计算比例，不对各条百分比求平均。无输入时显示“—”。\n\nCodex\n读取 sessions 与 archived_sessions 中的 event_msg / token_count。对 total_token_usage 做差分，跳过重复累计快照。input_tokens 已包含缓存部分，因此缓存不再重复加到总量。reasoning_output_tokens 属于输出子项，不额外累加。累计回退时使用 last_token_usage；缺失时跳过并提示。\n\nClaude Code\n读取 projects（含子代理目录）中的 assistant.message.usage。普通输入、缓存读取、缓存写入分开相加。同一会话、消息 ID 与请求 ID 的流式快照合并，取各字段最大值。缓存写入按顶层 cache_creation_input_tokens 统计，不再叠加其时效子项。\n\n范围与限制\n日期按本机时区，以用量事件发生日归属。数据只覆盖当前机器现存、可解析的日志；缺失日志无法还原，订阅余额、实际账单和其他设备用量不在统计范围内。不同平台 Token 分词方式不同，合计用于观察用量。\n\nCodex 的首个累计快照会整体计入；若导入截断或分叉继承的日志，它可能包含此前的用量。日志格式变更也可能影响统计。刷新会重新扫描，便于纳入正在写入或后续补全的记录。\n\n隐私\n用量统计无网络请求、不需要 API Key。配置切换只读写本机文件，不发送配置或用量日志。仅在内存中解析日志，不持久化对话正文。数据源路径保存至本机应用配置；导出文件仅包含当前筛选的会话统计。\n\n快捷键\nF5：刷新数据\nCtrl+F：搜索\nEnter：查看所选会话详情";
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                var app = new Application();
                var desktop = new Desktop();
                if (args.Length >= 2 && args[0] == "--render")
                {
                    desktop.SetResult(UsageReader.Scan(SourcePaths.Defaults(), null));
                    var content = (FrameworkElement)desktop.Window.Content;
                    content.Measure(new Size(1360, 910)); content.Arrange(new Rect(0, 0, 1360, 910)); content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1360, 910, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(args[1])) encoder.Save(file);
                    return 0;
                }
                desktop.Window.Loaded += async (s, e) => await desktop.Refresh();
                return app.Run(desktop.Window);
            }
            catch (Exception ex)
            {
                if (args.Length > 0) Console.Error.WriteLine(ex.ToString());
                else MessageBox.Show("程序未能启动：\n" + ex.Message, "AI Assistant", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
