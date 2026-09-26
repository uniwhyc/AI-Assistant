using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AI_Assistant
{
    public sealed partial class Desktop
    {
        void ClaudeSettings()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            // 跟随当前数据源目录（含数据源设置中的自定义目录），统计与配置指向同一位置。
            var claude = new ClaudeProviders(paths.Claude, Path.Combine(baseDirectory, "claude-configs"));
            var codex = new CodexProviders(paths.Codex, Path.Combine(baseDirectory, "codex-configs"));
            var dialog = Dialog("配置修改", 1040, 740);
            dialog.Content = new ClaudeProvidersPanel(claude, codex);
            dialog.ShowDialog();
            RestorePageSelection();
        }
    }

    public sealed class ClaudeProvidersPanel : DockPanel
    {
        ProviderConfigStore service;
        readonly ProviderConfigStore[] stores;
        readonly Button[] tabs;
        readonly ComboBox configs = new ComboBox { DisplayMemberPath = "Name", MinWidth = 160 };
        readonly TextBox name = new TextBox();
        readonly TextBox editor = new TextBox { AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, SpellCheck = { IsEnabled = false } };
        readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Color("#EFB18A"), Margin = new Thickness(0, 10, 0, 10) };
        readonly TextBlock current = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Color("#EFB18A"), Margin = new Thickness(0, 8, 0, 12) };
        readonly TextBlock headerTitle = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold };
        readonly TextBlock target = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Color("#A1AAB4"), Margin = new Thickness(0, 8, 0, 0) };
        readonly TextBlock formatHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Color("#A1AAB4"), Margin = new Thickness(0, 0, 0, 8) };
        readonly TextBlock nameLabel = new TextBlock { Margin = new Thickness(0, 0, 0, 6) };
        readonly Button restore = new Button { Content = "恢复原配置" };
        readonly Button delete = new Button { Content = "删除配置", Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
        readonly Func<string, bool> confirmDelete;
        bool loading;

        public ClaudeProvidersPanel(ProviderConfigStore service, Func<string, bool> confirmDelete = null)
            : this(service, null, confirmDelete) { }

        public ClaudeProvidersPanel(ProviderConfigStore primary, ProviderConfigStore secondary, Func<string, bool> confirmDelete = null)
        {
            stores = secondary == null ? new[] { primary } : new[] { primary, secondary };
            tabs = stores.Length > 1 ? new Button[stores.Length] : new Button[0];
            service = primary;
            this.confirmDelete = confirmDelete ?? ConfirmDelete;
            Margin = new Thickness(24); Background = Color("#191D22");
            System.Windows.Documents.TextElement.SetForeground(this, Color("#F0F2F4"));
            System.Windows.Documents.TextElement.SetFontSize(this, 13);

            var footer = new StackPanel();
            footer.Children.Add(status);
            var actions = new DockPanel();
            DockPanel.SetDock(restore, Dock.Left); actions.Children.Add(restore);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var validate = new Button { Content = "检查格式", Margin = new Thickness(0, 0, 8, 0) };
            var save = new Button { Content = "仅保存", Margin = new Thickness(0, 0, 8, 0) };
            var apply = new Button { Content = "保存并启用", Background = Color("#EFB18A"), Foreground = Color("#2B1C13") };
            var close = new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            right.Children.Add(validate); right.Children.Add(save); right.Children.Add(apply); right.Children.Add(close);
            actions.Children.Add(right); footer.Children.Add(actions);
            DockPanel.SetDock(footer, Dock.Bottom); Children.Add(footer);

            var header = new StackPanel();
            header.Children.Add(headerTitle);
            if (stores.Length > 1)
            {
                var switcher = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                for (int i = 0; i < stores.Length; i++)
                {
                    int index = i;
                    var tab = new Button { Content = stores[i].Spec.PlatformName, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 5, 14, 5) };
                    tab.Click += (s, e) => SwitchPlatform(index);
                    tabs[i] = tab; switcher.Children.Add(tab);
                }
                header.Children.Add(switcher);
            }
            header.Children.Add(target);
            header.Children.Add(current);
            DockPanel.SetDock(header, Dock.Top); Children.Add(header);
            var configuration = new StackPanel();
            var selection = new DockPanel();
            var add = new Button { Content = "新增配置", Margin = new Thickness(8, 0, 0, 0) };
            var copy = new Button { Content = "从当前配置新建", Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(delete, Dock.Right); selection.Children.Add(delete);
            DockPanel.SetDock(copy, Dock.Right); selection.Children.Add(copy);
            DockPanel.SetDock(add, Dock.Right); selection.Children.Add(add); selection.Children.Add(configs);
            AddField(configuration, "已保存的配置", selection);
            AutomationProperties.SetName(configs, "已保存的配置");
            AddField(configuration, nameLabel, name);
            configuration.Children.Add(formatHint);
            AutomationProperties.SetName(editor, service.Spec.EditorAutomationName);
            DockPanel.SetDock(configuration, Dock.Top); Children.Add(configuration);
            Children.Add(editor);

            configs.SelectionChanged += (s, e) => { if (!loading) SelectConfig(); };
            add.Click += (s, e) => NewConfig(service.Spec.Template);
            copy.Click += (s, e) => Run(() => NewConfig(service.ReadCurrent()));
            delete.Click += (s, e) => DeleteConfig();
            validate.Click += (s, e) => Run(() => {
                var warnings = service.Spec.Validator(editor.Text);
                status.Text = service.Spec.ValidatePassedMessage;
                foreach (string warning in warnings) status.Text += " " + warning;
            });
            save.Click += (s, e) => Save(false);
            apply.Click += (s, e) => Save(true);
            restore.Click += (s, e) => Run(() => {
                service.Restore(); UpdateCurrent();
                status.Text = "已恢复首次切换前的原配置。请重新打开 " + service.Spec.PlatformName + " 会话。";
            });
            close.Click += (s, e) => { var window = Window.GetWindow(this); if (window != null) window.Close(); };
            headerTitle.Text = service.Spec.PlatformName + " 配置文件";
            target.Text = "目标文件：" + service.SettingsPath;
            formatHint.Text = service.Spec.FormatHint;
            nameLabel.Text = service.Spec.NameFieldAutomationName;
            AutomationProperties.SetName(name, service.Spec.NameFieldAutomationName);
            MarkTab(0);
            Run(() => { Reload(null); NewConfig(service.ReadCurrent()); });
        }

        static Brush Color(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }
        static void AddField(Panel panel, string label, FrameworkElement control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            control.Margin = new Thickness(0, 0, 0, 10);
            AutomationProperties.SetName(control, label); panel.Children.Add(control);
        }
        static void AddField(Panel panel, TextBlock label, FrameworkElement control)
        {
            panel.Children.Add(label);
            control.Margin = new Thickness(0, 0, 0, 10);
            AutomationProperties.SetName(control, label.Text); panel.Children.Add(control);
        }

        void SwitchPlatform(int index)
        {
            if (service == stores[index]) return;
            service = stores[index];
            MarkTab(index);
            headerTitle.Text = service.Spec.PlatformName + " 配置文件";
            target.Text = "目标文件：" + service.SettingsPath;
            formatHint.Text = service.Spec.FormatHint;
            nameLabel.Text = service.Spec.NameFieldAutomationName;
            AutomationProperties.SetName(name, service.Spec.NameFieldAutomationName);
            AutomationProperties.SetName(editor, service.Spec.EditorAutomationName);
            Run(() => { Reload(null); NewConfig(service.ReadCurrent()); });
            status.Text = "已切换到 " + service.Spec.PlatformName + " 配置，未保存的编辑已丢弃。";
        }

        void MarkTab(int index)
        {
            for (int i = 0; i < tabs.Length; i++)
            {
                tabs[i].Background = i == index ? Color("#EFB18A") : null;
                tabs[i].Foreground = i == index ? Color("#2B1C13") : Color("#F0F2F4");
            }
        }

        void NewConfig(string json)
        {
            loading = true;
            try { configs.SelectedIndex = -1; }
            finally { loading = false; }
            name.Clear(); editor.Text = json;
            delete.IsEnabled = false;
            status.Text = "请命名并编辑完整配置。仅保存不会改动 " + service.Spec.PlatformName + " 当前文件。";
            name.Focus();
        }

        void SelectConfig()
        {
            var config = configs.SelectedItem as ProviderConfig;
            delete.IsEnabled = config != null && service.CanDelete(config.Name);
            delete.ToolTip = delete.IsEnabled ? "删除程序新建的配置，保留当前生效配置和备份。" : "仅可删除程序新建的配置；原有配置受保护。";
            if (config == null) return;
            name.Text = config.Name; editor.Text = config.Json;
            status.Text = "可修改名称和完整配置，保存后生效；启用后才会写入 " + service.Spec.PlatformName + " 当前文件。";
            if (!delete.IsEnabled) status.Text += " 此配置没有程序创建记录，禁止删除。";
        }

        void UpdateCurrent()
        {
            restore.IsEnabled = File.Exists(service.OriginalPath);
            current.Text = "当前配置：" + service.CurrentName(service.Load());
        }

        void Reload(string selectedName)
        {
            var items = service.Load();
            loading = true;
            try { configs.ItemsSource = items; configs.SelectedItem = items.Find(p => p.Name == selectedName); }
            finally { loading = false; }
            SelectConfig(); UpdateCurrent();
        }

        void Save(bool activate)
        {
            Run(() => {
                var config = new ProviderConfig { Name = name.Text.Trim(), Json = editor.Text };
                var selected = configs.SelectedItem as ProviderConfig;
                if (selected != null) service.RenameAndSave(selected.Name, config);
                else service.Save(config);
                Reload(config.Name);
                if (activate)
                {
                    try { service.Apply(config); }
                    catch (Exception ex) { status.Text = "配置已保存，启用失败：" + Error(ex); return; }
                    UpdateCurrent();
                }
                status.Text = activate ? "已完整写入“" + config.Name + "”。请重新打开 " + service.Spec.PlatformName + " 会话。" : "配置文件已保存，" + service.Spec.PlatformName + " 当前文件未改动。";
            });
        }

        bool ConfirmDelete(string configName)
        {
            return MessageBox.Show(Window.GetWindow(this), "确定删除已保存的配置“" + configName
                + "”？\n\n编辑器中未保存的修改也将丢弃。" + service.Spec.PlatformName + " 当前生效配置和已有备份会保留。",
                "删除配置", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        void DeleteConfig()
        {
            var config = configs.SelectedItem as ProviderConfig;
            if (config == null) return;
            Run(() => {
                if (!service.CanDelete(config.Name)) throw new InvalidOperationException("此配置没有程序创建记录，禁止删除。");
                if (!confirmDelete(config.Name)) return;
                service.Delete(config.Name);
                Reload(null); NewConfig(service.ReadCurrent());
                status.Text = "已删除“" + config.Name + "”。" + service.Spec.PlatformName + " 当前生效配置和已有备份未改动。";
            });
        }

        void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { status.Text = Error(ex); }
        }

        static string Error(Exception ex)
        {
            if (ex is InvalidOperationException) return ex.Message;
            if (ex is IOException || ex is UnauthorizedAccessException) return "文件读写失败，请检查目录权限或文件是否被其他程序占用。";
            if (ex is ArgumentException || ex is NotSupportedException) return "配置路径或内容无效，请检查后重试。";
            throw ex;
        }
    }
}
