using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AI_Assistant;

public static class DesktopTests
{
    static int passed;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("界面检查失败：" + name);
        passed++; Console.WriteLine("通过：" + name);
    }
    static T Find<T>(Desktop desktop, string name) where T : FrameworkElement
    { return (T)desktop.Window.FindName(name); }
    static void Layout(Desktop desktop, int width, int height)
    {
        var root = (FrameworkElement)desktop.Window.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
    }
    static void Render(Desktop desktop, string path, int width, int height)
    {
        Layout(desktop, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((FrameworkElement)desktop.Window.Content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) png.Save(stream);
    }
    static void RenderControl(FrameworkElement control, string path, int width, int height)
    {
        control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height)); control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) png.Save(stream);
    }
    [STAThread]
    public static int Main()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var app = new Application();
            var desktop = new Desktop();
            var result = new ScanResult { Files = 2, Records = new List<Usage> {
                new Usage { Key = "c1", Platform = "Codex", Project = "E:/测试项目", Session = "s1", Model = "测试模型",
                    Time = new DateTimeOffset(DateTime.Today.AddHours(10)), Input = 20, CacheRead = 70, CacheWrite = 10, Output = 30 },
                new Usage { Key = "a1", Platform = "Claude Code", Project = "E:/另一项目", Session = "s2", Model = "测试模型",
                    Time = new DateTimeOffset(DateTime.Today.AddDays(-3).AddHours(10)), Input = 100, CacheRead = 0, CacheWrite = 0, Output = 10 }
            } };
            desktop.SetResult(result); Layout(desktop, 1360, 910);
            Check(Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex == 0, "默认选择今天");
            Check(Find<DatePicker>(desktop, "StartDate").SelectedDate == DateTime.Today && Find<DatePicker>(desktop, "EndDate").SelectedDate == DateTime.Today,
                "默认起止日期均为今天");
            Check(Find<ComboBox>(desktop, "StartTime").Text == "00:00:00" && Find<ComboBox>(desktop, "EndTime").Text == "23:59:59",
                "默认起止时间覆盖今天整天");
            Check(Find<TextBlock>(desktop, "TotalValue").Text == "130", "首次展示仅包含今天的用量");
            Check(Find<DatePicker>(desktop, "StartDate").Visibility == Visibility.Visible && Find<DatePicker>(desktop, "StartDate").ActualWidth > 0
                && Find<DatePicker>(desktop, "EndDate").Visibility == Visibility.Visible && Find<DatePicker>(desktop, "EndDate").ActualWidth > 0,
                "预设范围下起止时间始终显示");
            Render(desktop, "artifacts/filters-today.png", 1360, 910);
            Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex = 1;
            Check(Find<DatePicker>(desktop, "StartDate").SelectedDate == DateTime.Today.AddDays(-6)
                && Find<DatePicker>(desktop, "EndDate").SelectedDate == DateTime.Today, "最近七天自动更新起止日期");
            Check(Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex == 1, "预设填充不会误切换到自定义时间");
            Render(desktop, "artifacts/filters-seven-days.png", 1360, 910);
            Check(Find<TextBlock>(desktop, "TotalValue").Text == "240", "概览展示两个平台的合计");
            Check(Find<TextBlock>(desktop, "HitValue").Text == (0.35).ToString("P1"), "命中率与筛选合计一致");
            var bar = Find<Grid>(desktop, "CompositionBar");
            Check(Math.Abs(bar.ColumnDefinitions[0].ActualWidth / bar.ActualWidth - 0.6) < 0.01, "输入构成条按实际比例绘制");
            Find<ComboBox>(desktop, "PlatformFilter").SelectedIndex = 1;
            Check(Find<TextBlock>(desktop, "TotalValue").Text == "130", "平台筛选更新用量");
            Check(Find<DataGrid>(desktop, "SessionsGrid").Items.Count == 1, "平台筛选同步会话表");
            Layout(desktop, 1360, 910);
            Check(Find<Canvas>(desktop, "TrendCanvas").Children.OfType<System.Windows.Shapes.Polyline>().Count() == 1,
                "选择单个平台时趋势图仅显示该平台曲线");
            Check(Find<TextBlock>(desktop, "InputValue").Text == "100" && Find<TextBlock>(desktop, "OutputValue").Text == "30"
                && Find<TextBlock>(desktop, "HitValue").Text == (0.7).ToString("P1"), "Codex 输入、输出及命中率同步切换");
            Check(Find<TextBlock>(desktop, "NormalValue").Text == "20" && Find<TextBlock>(desktop, "ReadValue").Text == "70"
                && Find<TextBlock>(desktop, "WriteValue").Text == "10", "Codex 输入构成同步切换");
            Check(Find<Border>(desktop, "CodexCard").Visibility == Visibility.Visible && Find<Border>(desktop, "ClaudeCard").Visibility == Visibility.Collapsed,
                "选择 Codex 只显示 Codex 平台卡片");
            Check(Find<TextBlock>(desktop, "CodexLegend").Visibility == Visibility.Visible && Find<TextBlock>(desktop, "ClaudeLegend").Visibility == Visibility.Collapsed,
                "选择 Codex 只显示 Codex 图例");
            Check(Find<Canvas>(desktop, "TrendCanvas").Children.OfType<Border>().All(x => !x.ToolTip.ToString().Contains("Claude Code")),
                "趋势提示不显示未选择的平台");
            Render(desktop, "artifacts/platform-codex.png", 1360, 910);
            Find<ComboBox>(desktop, "PlatformFilter").SelectedIndex = 2;
            Check(Find<TextBlock>(desktop, "TotalValue").Text == "110" && Find<TextBlock>(desktop, "InputValue").Text == "100"
                && Find<TextBlock>(desktop, "OutputValue").Text == "10" && Find<TextBlock>(desktop, "HitValue").Text == (0.0).ToString("P1"),
                "切换 Claude Code 后所有汇总卡更新");
            Check(Find<TextBlock>(desktop, "NormalValue").Text == "100" && Find<TextBlock>(desktop, "ReadValue").Text == "0"
                && Find<TextBlock>(desktop, "WriteValue").Text == "0", "切换 Claude Code 清除此前缓存构成");
            Check(Find<DataGrid>(desktop, "SessionsGrid").Items.Cast<SessionRow>().All(x => x.Platform == "Claude Code"),
                "切换 Claude Code 后会话和导出数据仅含该平台");
            Check(Find<Border>(desktop, "CodexCard").Visibility == Visibility.Collapsed && Find<Border>(desktop, "ClaudeCard").Visibility == Visibility.Visible
                && Find<TextBlock>(desktop, "CodexLegend").Visibility == Visibility.Collapsed && Find<TextBlock>(desktop, "ClaudeLegend").Visibility == Visibility.Visible,
                "Claude Code 卡片和图例同步显示");
            Check(Find<DatePicker>(desktop, "StartDate").SelectedDate == DateTime.Today.AddDays(-6)
                && Find<ComboBox>(desktop, "EndTime").Text == "23:59:59", "切换平台保留时间范围");
            Render(desktop, "artifacts/platform-claude.png", 1360, 910);
            Find<TextBox>(desktop, "SearchBox").Text = "测试项目"; desktop.Apply();
            Check(Find<TextBlock>(desktop, "TotalValue").Text == "0" && Find<TextBlock>(desktop, "HitValue").Text == "—"
                && !Find<Button>(desktop, "ExportButton").IsEnabled, "所选平台无匹配记录时清空用量并禁止导出");
            Find<ComboBox>(desktop, "PlatformFilter").SelectedIndex = 1;
            Check(Find<TextBox>(desktop, "SearchBox").Text == "测试项目" && Find<TextBlock>(desktop, "TotalValue").Text == "130",
                "切换平台保留搜索条件并重新统计");
            Find<TextBox>(desktop, "SearchBox").Clear(); desktop.Apply();
            Find<ComboBox>(desktop, "PlatformFilter").SelectedIndex = 0;
            Layout(desktop, 1360, 910);
            Check(Find<TextBlock>(desktop, "TotalValue").Text == "240" && Find<DataGrid>(desktop, "SessionsGrid").Items.Count == 2,
                "切回全部平台恢复总量与会话合计");
            Check(Find<Canvas>(desktop, "TrendCanvas").Children.OfType<System.Windows.Shapes.Polyline>().Count() == 2
                && Find<Border>(desktop, "CodexCard").Visibility == Visibility.Visible && Find<Border>(desktop, "ClaudeCard").Visibility == Visibility.Visible,
                "切回全部平台恢复双平台曲线与卡片");
            Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex = 2;
            Check(Find<DatePicker>(desktop, "StartDate").SelectedDate == DateTime.Today.AddDays(-29), "最近三十天自动更新开始日期");
            Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex = 3;
            Check(Find<DatePicker>(desktop, "StartDate").SelectedDate == DateTime.Today.AddDays(-3), "全部时间从最早记录日期开始");
            Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex = 0;
            Check(Find<DatePicker>(desktop, "StartDate").SelectedDate == DateTime.Today, "切回今天恢复当天起止日期");
            Check(Find<TextBlock>(desktop, "TotalValue").Text == "130", "今天筛选排除此前用量");
            Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex = 4;
            Find<DatePicker>(desktop, "StartDate").SelectedDate = DateTime.Today.AddDays(1);
            Find<DatePicker>(desktop, "EndDate").SelectedDate = DateTime.Today;
            Check(Find<Border>(desktop, "Notice").Visibility == Visibility.Visible, "无效日期显示解释");
            Check(!Find<Button>(desktop, "ExportButton").IsEnabled, "无效日期禁止导出陈旧数据");
            Find<DatePicker>(desktop, "StartDate").SelectedDate = DateTime.Today;
            Find<ComboBox>(desktop, "StartTime").Text = "09:59:59";
            Find<ComboBox>(desktop, "EndTime").Text = "09:59:59";
            Check(Find<TextBlock>(desktop, "TotalValue").Text == "0", "结束时间精确到秒并排除后一秒用量");
            Find<ComboBox>(desktop, "EndTime").Text = "10:00:00";
            Check(Find<TextBlock>(desktop, "TotalValue").Text == "130", "结束秒的用量纳入概览和明细");
            Find<ComboBox>(desktop, "StartTime").Text = "10:00:01";
            Check(Find<TextBlock>(desktop, "NoticeText").Text.Contains("开始时间不能晚于"), "同一天的时分秒倒置显示提示");
            Find<ComboBox>(desktop, "EndTime").Text = "10:00:01";
            Check(Find<TextBlock>(desktop, "TotalValue").Text == "0", "开始时间精确到秒并排除前一秒用量");
            foreach (string invalid in new[] { "24:00:00", "10:60:00", "10:00:60", "", "abc" })
            {
                Find<ComboBox>(desktop, "StartTime").Text = invalid;
                Check(Find<Border>(desktop, "Notice").Visibility == Visibility.Visible && !Find<Button>(desktop, "ExportButton").IsEnabled,
                    "无效时分秒阻止筛选和导出：" + (invalid == "" ? "空值" : invalid));
            }
            Find<ComboBox>(desktop, "StartTime").Text = "09:59:59";
            Find<ComboBox>(desktop, "EndTime").Text = "10:00:00";
            Check(Find<Border>(desktop, "Notice").Visibility == Visibility.Collapsed, "修正时间后恢复筛选");
            desktop.SetResult(result);
            Check(Find<ComboBox>(desktop, "StartTime").Text == "09:59:59" && Find<ComboBox>(desktop, "EndTime").Text == "10:00:00",
                "刷新数据保留手动设置的秒级范围");
            Render(desktop, "artifacts/custom-time.png", 1360, 910);
            Render(desktop, "artifacts/custom-time-compact.png", 1100, 710);
            var timeBox = Find<ComboBox>(desktop, "EndTime");
            var editBox = (TextBox)timeBox.Template.FindName("PART_EditableTextBox", timeBox);
            var timeHost = (ScrollViewer)editBox.Template.FindName("PART_ContentHost", editBox);
            Check(timeHost.ExtentWidth <= timeHost.ViewportWidth, "时间输入框完整显示时分秒");
            var hourList = (ListBox)timeBox.Template.FindName("PART_HourList", timeBox);
            var minuteList = (ListBox)timeBox.Template.FindName("PART_MinuteList", timeBox);
            var secondList = (ListBox)timeBox.Template.FindName("PART_SecondList", timeBox);
            Check(hourList.Items.Count == 24 && minuteList.Items.Count == 60 && secondList.Items.Count == 60,
                "时分秒下拉三列分别包含 24/60/60 个选项");
            Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex = 0;
            hourList.SelectedItem = "09"; minuteList.SelectedItem = "30"; secondList.SelectedItem = "15";
            Check(Find<ComboBox>(desktop, "EndTime").Text == "09:30:15", "下拉选择时分秒合并为完整时间");
            Check(Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex == 4, "下拉选择自动切换自定义范围");
            timeBox.Text = "11:22:33";
            typeof(Desktop).GetMethod("SyncTimeList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(desktop, new object[] { timeBox });
            Check((string)hourList.SelectedItem == "11" && (string)minuteList.SelectedItem == "22" && (string)secondList.SelectedItem == "33",
                "打开下拉按当前文本同步三列选中项");
            var datePicker = Find<DatePicker>(desktop, "StartDate");
            var popup = (Popup)datePicker.Template.FindName("PART_Popup", datePicker);
            var calendar = (Calendar)popup.Child;
            RenderControl(calendar, "artifacts/calendar.png", 320, 320);
            var calendarItem = (CalendarItem)calendar.Template.FindName("PART_CalendarItem", calendar);
            var monthView = (Grid)calendarItem.Template.FindName("PART_MonthView", calendarItem);
            Check(monthView.Children.Count == 49, "深色日历保留星期标题及 42 个日期按钮");
            DateTime month = calendar.DisplayDate;
            ((Button)calendarItem.Template.FindName("PART_PreviousButton", calendarItem)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(calendar.DisplayDate.Month == month.AddMonths(-1).Month, "深色日历支持月份导航");
            ((Button)calendarItem.Template.FindName("PART_HeaderButton", calendarItem)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(calendar.DisplayMode == CalendarMode.Year, "深色日历支持切换月份选择");
            RenderControl(calendar, "artifacts/calendar-year.png", 320, 320);
            Check(monthView.Visibility == Visibility.Collapsed, "月份选择视图隐藏日期网格");
            var yearView = (Grid)calendarItem.Template.FindName("PART_YearView", calendarItem);
            Check(yearView.Visibility == Visibility.Visible && yearView.Children.Count == 12, "月份选择视图显示 12 个月份");
            var tip = new ToolTip { Content = "缓存命中率：70.0%\n缓存读取 / 全部输入", Resources = desktop.Window.Resources };
            RenderControl(tip, "artifacts/tooltip.png", 260, 80);
            var menu = (ContextMenu)desktop.Window.Resources["TextEditMenu"];
            menu.Resources = desktop.Window.Resources;
            menu.PlacementTarget = timeBox;
            RenderControl(menu, "artifacts/context-menu.png", 220, 160);
            Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex = 1;
            Check(Find<ComboBox>(desktop, "StartTime").Text == "00:00:00" && Find<ComboBox>(desktop, "EndTime").Text == "23:59:59",
                "从自定义切回最近七天重置起止秒数");
            Find<ComboBox>(desktop, "StartTime").Text = "08:00:00";
            Check(Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex == 4, "直接编辑时间自动切换自定义范围");
            Check(Find<ComboBox>(desktop, "StartTime").Text == "08:00:00", "切换自定义时保留用户输入");
            Find<ComboBox>(desktop, "PeriodFilter").SelectedIndex = 1;
            Find<TextBox>(desktop, "SearchBox").Text = "不存在的项目"; desktop.Apply();
            Check(Find<TextBlock>(desktop, "EmptyText").Visibility == Visibility.Visible, "搜索空结果显示提示");
            Find<TextBox>(desktop, "SearchBox").Clear(); desktop.Apply();
            Find<Button>(desktop, "SessionsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Find<StackPanel>(desktop, "OverviewPanels").Visibility == Visibility.Collapsed, "会话导航切换视图");
            Find<Button>(desktop, "OverviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Find<StackPanel>(desktop, "OverviewPanels").Visibility == Visibility.Visible, "概览导航恢复视图");
            foreach (string page in new[] { "OverviewButton", "SessionsButton" })
            {
                var selected = Find<Button>(desktop, page);
                selected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                string title = Find<TextBlock>(desktop, "PageTitle").Text;
                var sourceButton = Find<Button>(desktop, "SourcesButton");
                FocusManager.SetFocusedElement(desktop.Window, sourceButton);
                Check(FocusManager.GetFocusedElement(desktop.Window) == sourceButton, "重现数据源操作留下焦点：" + title);
                typeof(Desktop).GetMethod("RestorePageSelection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(desktop, null);
                Check(FocusManager.GetFocusedElement(desktop.Window) == selected, "关闭后焦点恢复到当前页面：" + title);
                Check(Find<TextBlock>(desktop, "PageTitle").Text == title, "恢复标记不改变页面内容：" + title);
                Check(((SolidColorBrush)selected.Background).Color == (Color)ColorConverter.ConvertFromString("#243A33")
                    && ((SolidColorBrush)sourceButton.Background).Color == Colors.Transparent, "仅当前页面保留选中标记：" + title);
            }
            Find<Button>(desktop, "OverviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(desktop, "artifacts/compact-fixture.png", 1100, 710);
            Check(Find<Canvas>(desktop, "TrendCanvas").ActualWidth > 200, "最小窗口保留趋势绘图空间");
            var settingsContent = (StackPanel)Find<Button>(desktop, "ClaudeSettingsButton").Content;
            Check(Find<TextBlock>(desktop, "ClaudeSettingsLabel").Text == "配置修改"
                && settingsContent.Children.OfType<TextBlock>().Any(icon => icon.Text == "⚙" && icon.Width == 26),
                "侧栏配置入口更名为配置修改并带统一尺寸图标");
            Console.WriteLine("界面检查：" + passed + " 项通过。");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }
}
