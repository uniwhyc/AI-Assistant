using System;
using System.IO;
using System.Linq;
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

        // 软件每次启动（以及数据源目录变更）时调用：以“第一次看到”的生效文件为准留档原配置，之后永不被覆盖。
        // 留档失败不阻断启动；打开配置界面、写入前都还有兜底。
        internal void PreserveOriginals()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            TryPreserve(new ClaudeProviders(paths.Claude, Path.Combine(baseDirectory, "claude-configs")));
            TryPreserve(new CodexProviders(paths.Codex, Path.Combine(baseDirectory, "codex-configs")));
        }

        static void TryPreserve(ProviderConfigStore store)
        {
            try { store.PreserveOriginal(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public sealed class ClaudeProvidersPanel : DockPanel
    {
        ProviderConfigStore service;
        readonly ProviderConfigStore[] stores;
        readonly Button[] tabs;
        readonly ComboBox configs = new ComboBox { DisplayMemberPath = "Name", MinWidth = 160 };
        readonly TextBlock emptyHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Color("#A1AAB4"), VerticalAlignment = VerticalAlignment.Center };
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
        readonly Button apply = new Button { Content = "保存并启用", Background = Color("#EFB18A"), Foreground = Color("#2B1C13") };
        readonly Func<string, bool> confirmDelete;
        // 丢弃未保存修改、恢复原配置的用户回答；测试可注入替身以避免模态对话框。
        // 注意：丢弃保护统一调用 ConfirmDiscard() 方法（大写开头，内含“有改动才询问”的短路，且方法作用域不会命中被遮蔽的同名参数）；
        // 恢复用 this.confirmRestore() 引用字段——构造器参数会遮蔽同名字段，裸写小写名会命中被注入的参数（App 中为 null）。
        readonly Func<bool> confirmDiscard;
        readonly Func<bool> confirmRestore;
        bool loading;
        // 编辑器内容的载入基准：与编辑框现状不一致即视为有未保存修改（loadedName 为 null 表示新建状态）。
        string loadedName;
        string loadedEditor = "";
        // 没有任何已保存配置时，用这句提示直接取代下拉框（显隐由 Reload 切换）。
        const string EmptyHint = "暂无已保存的配置，可点右侧“新建配置”";

        public ClaudeProvidersPanel(ProviderConfigStore service, Func<string, bool> confirmDelete = null,
            Func<bool> confirmDiscard = null, Func<bool> confirmRestore = null)
            : this(service, null, confirmDelete, confirmDiscard, confirmRestore) { }

        public ClaudeProvidersPanel(ProviderConfigStore primary, ProviderConfigStore secondary, Func<string, bool> confirmDelete = null,
            Func<bool> confirmDiscard = null, Func<bool> confirmRestore = null)
        {
            stores = secondary == null ? new[] { primary } : new[] { primary, secondary };
            tabs = stores.Length > 1 ? new Button[stores.Length] : new Button[0];
            service = primary;
            this.confirmDelete = confirmDelete ?? ConfirmDelete;
            this.confirmDiscard = confirmDiscard ?? AskDiscard;
            this.confirmRestore = confirmRestore ?? ConfirmRestore;
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
                    tab.Click += (s, e) => { if (ConfirmDiscard()) SwitchPlatform(index); };
                    tabs[i] = tab; switcher.Children.Add(tab);
                }
                header.Children.Add(switcher);
            }
            header.Children.Add(target);
            header.Children.Add(current);
            DockPanel.SetDock(header, Dock.Top); Children.Add(header);
            var configuration = new StackPanel();
            var selection = new DockPanel();
            var add = new Button { Content = "新建配置", Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(delete, Dock.Right); selection.Children.Add(delete);
            DockPanel.SetDock(add, Dock.Right); selection.Children.Add(add);
            // 下拉框与空态提示叠在同一格：没有已保存配置时隐藏下拉、直接显示提示。
            var selector = new Grid();
            selector.Children.Add(configs); selector.Children.Add(emptyHint);
            selection.Children.Add(selector);
            AddField(configuration, "已保存的配置", selection);
            AutomationProperties.SetName(configs, "已保存的配置");
            configs.ToolTip = "选择已保存的配置即可载入编辑；未改动时点“启用此配置”切换生效，改动过则点“保存并启用”。";
            emptyHint.Text = EmptyHint;
            AutomationProperties.SetName(emptyHint, EmptyHint);
            AddField(configuration, nameLabel, name);
            configuration.Children.Add(formatHint);
            AutomationProperties.SetName(editor, service.Spec.EditorAutomationName);
            // 禁用态也要能显示悬停说明，用户才知道按钮为何不可点。
            ToolTipService.SetShowOnDisabled(restore, true);
            ToolTipService.SetShowOnDisabled(delete, true);
            DockPanel.SetDock(configuration, Dock.Top); Children.Add(configuration);
            Children.Add(editor);

            configs.SelectionChanged += (s, e) => { if (!loading) SelectConfig(true); };
            editor.TextChanged += (s, e) => UpdateApplyLabel();
            name.TextChanged += (s, e) => UpdateApplyLabel();
            // “新建配置”以编辑器当前展示的内容为初稿（含未保存的修改）：所见即所存，草稿不丢弃。
            add.Click += (s, e) => NewConfig(editor.Text);
            delete.Click += (s, e) => DeleteConfig();
            validate.Click += (s, e) => Run(() => {
                var warnings = service.Spec.Validator(editor.Text);
                status.Text = service.Spec.ValidatePassedMessage;
                foreach (string warning in warnings) status.Text += " " + warning;
            });
            save.Click += (s, e) => Save(false);
            apply.Click += (s, e) => Activate();
            restore.Click += (s, e) => Run(() => {
                if (!this.confirmRestore()) return;
                service.Restore(); UpdateCurrent();
                status.Text = "已恢复首次打开时的原配置。请重新打开 " + service.Spec.PlatformName + " 会话。";
            });
            close.Click += (s, e) => { var window = Window.GetWindow(this); if (window != null) window.Close(); };
            // 关闭入口（按钮、Esc、标题栏 ×）统一经过窗口 Closing：有未保存修改时先确认。
            Loaded += (s, e) => {
                var window = Window.GetWindow(this);
                if (window != null) window.Closing += (sender, args) => { if (!ConfirmDiscard()) args.Cancel = true; };
            };
            headerTitle.Text = service.Spec.PlatformName + " 配置文件";
            target.Text = "目标文件：" + service.SettingsPath;
            formatHint.Text = service.Spec.FormatHint;
            nameLabel.Text = service.Spec.NameFieldAutomationName;
            AutomationProperties.SetName(name, service.Spec.NameFieldAutomationName);
            MarkTab(0);
            // 软件启动时已留档；这里再兜底（例如启动后才出现配置文件），留档只创建一次。
            Run(() => { service.PreserveOriginal(); Reload(null); NewConfig(service.ReadCurrent()); });
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
            Run(() => { service.PreserveOriginal(); Reload(null); NewConfig(service.ReadCurrent()); });
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
            loadedName = null; loadedEditor = json;
            delete.IsEnabled = false;
            UpdateApplyLabel();
            status.Text = "请命名并编辑完整配置。仅保存不会改动 " + service.Spec.PlatformName + " 当前文件。";
            name.Focus();
        }

        // 未选中任何已保存配置（含新建状态）时返回 null。
        ProviderConfig SelectedConfig() { return configs.SelectedItem as ProviderConfig; }

        // confirmSwitch 仅在用户手动切换下拉时确认；程序内部刷新（保存/删除/切换平台后的重载）不重复询问。
        void SelectConfig(bool confirmSwitch)
        {
            var config = SelectedConfig();
            if (confirmSwitch && config != null && !ConfirmDiscard())
            {
                // 用户要保留未保存的修改：把下拉选中拨回编辑器内容对应的配置。
                loading = true;
                try { configs.SelectedItem = configs.Items.OfType<ProviderConfig>().FirstOrDefault(p => p.Name == loadedName); }
                finally { loading = false; }
                return;
            }
            delete.IsEnabled = config != null && service.CanDelete(config.Name);
            delete.ToolTip = delete.IsEnabled ? "删除程序新建的配置，保留当前生效配置和备份。" : "仅可删除程序新建的配置；原有配置受保护。";
            UpdateApplyLabel();
            if (config == null) return;
            name.Text = config.Name; editor.Text = config.Json;
            loadedName = config.Name; loadedEditor = config.Json;
            status.Text = "未做修改可直接启用；修改后请用“保存并启用”。启用会完整写入 " + service.Spec.PlatformName + " 当前文件。";
            if (!delete.IsEnabled) status.Text += " 此配置没有程序创建记录，禁止删除。";
        }

        // 选中的已保存配置与编辑器内容完全一致时，无需重复保存，可以直接启用。
        bool Unchanged()
        {
            var selected = SelectedConfig();
            return selected != null && name.Text == selected.Name && editor.Text == selected.Json;
        }

        void UpdateApplyLabel()
        {
            apply.Content = Unchanged() ? "启用此配置" : "保存并启用";
        }

        void Activate()
        {
            if (!Unchanged()) { Save(true); return; }
            var selected = SelectedConfig();
            Run(() => {
                service.Apply(selected);
                UpdateCurrent();
                status.Text = "已启用“" + selected.Name + "”。请重新打开 " + service.Spec.PlatformName + " 会话。";
            });
        }

        void UpdateCurrent()
        {
            // 是否可恢复取决于“当前生效文件是否偏离了首次打开时的留档”，而不是是否启用过。
            var hasOriginal = File.Exists(service.OriginalPath);
            restore.IsEnabled = service.CanRestore();
            restore.ToolTip = restore.IsEnabled
                ? "把 " + service.Spec.PlatformName + " 当前生效文件还原为首次打开时保存的原配置；还原前的内容（如有）会保留在备份文件中。"
                : hasOriginal
                    ? "当前生效文件已与首次打开时保存的原配置一致，无需恢复。"
                    : "首次打开时还没有 " + service.Spec.PlatformName + " 配置文件，暂无可恢复的原配置。";
            current.Text = "当前配置：" + service.CurrentName(service.Load());
        }

        void Reload(string selectedName)
        {
            var items = service.Load();
            loading = true;
            try { configs.ItemsSource = items; configs.SelectedItem = items.Find(p => p.Name == selectedName); }
            finally { loading = false; }
            // 没有已保存配置时以提示文字取代下拉框：没有可选项，展开也只会是空白细条。
            configs.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            emptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectConfig(false); UpdateCurrent();
        }

        void Save(bool activate)
        {
            Run(() => {
                // 保存时按平台重排格式：Claude JSON 美化为两空格缩进（只调空白，便于阅读），Codex TOML 原样保留。
                string text = service.Spec.Formatter == null ? editor.Text : service.Spec.Formatter(editor.Text);
                var config = new ProviderConfig { Name = name.Text.Trim(), Json = text };
                var selected = SelectedConfig();
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
            return Confirm(Window.GetWindow(this), "删除配置", "确定删除已保存的配置“" + configName
                + "”？\n\n编辑器中未保存的修改也将丢弃。" + service.Spec.PlatformName + " 当前生效配置和已有备份会保留。");
        }

        // 编辑器内容是否已偏离载入基准（loadedName 为 null 表示新建状态）。
        bool HasUnsavedChanges()
        {
            return editor.Text != loadedEditor || name.Text != (loadedName ?? "");
        }

        // 有未保存修改时先征求用户意见，避免选择切换、新增、关闭等动作静默丢弃编辑内容。
        bool ConfirmDiscard()
        {
            return !HasUnsavedChanges() || confirmDiscard();
        }

        // 默认确认：深色确认框询问是否丢弃未保存修改（测试注入替身以避免模态阻塞）。
        bool AskDiscard()
        {
            return Confirm(Window.GetWindow(this), "丢弃未保存的修改", "编辑器里还有未保存的修改，继续操作会丢弃这些内容。\n\n是否继续？");
        }

        bool ConfirmRestore()
        {
            return Confirm(Window.GetWindow(this), "恢复原配置", "确定恢复首次打开时保存的原配置？\n\n"
                + service.Spec.PlatformName + " 当前生效文件将被还原；还原前的内容（如有）仍会保留在备份文件中。");
        }

        // 与主体配色一致的深色确认框：原生 MessageBox 是浅色系统风格，与深色界面割裂。
        // 标题与“是/否”按钮文案保持原样；默认与 Esc 都落在“否”，避免误确认。
        static bool Confirm(Window owner, string caption, string message)
        {
            var dialog = new Window
            {
                Title = caption,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = owner,
                WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                Background = Color("#191D22"), Foreground = Color("#F0F2F4"), FontSize = 13
            };
            if (owner != null) { dialog.FontFamily = owner.FontFamily; dialog.Resources = owner.Resources; }
            Desktop.DarkTitleBar(dialog);

            var body = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 22, 24, 0) };
            body.Children.Add(new TextBlock { Text = "⚠", FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 22, Foreground = Color("#EFB18A"), Margin = new Thickness(0, 0, 12, 0) });
            body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400, VerticalAlignment = VerticalAlignment.Center });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(24, 18, 24, 20) };
            var yes = new Button { Content = "是", MinWidth = 78 };
            var no = new Button { Content = "否", MinWidth = 78, Margin = new Thickness(10, 0, 0, 0), IsDefault = true, IsCancel = true };
            bool confirmed = false;
            yes.Click += (s, e) => { confirmed = true; dialog.Close(); };
            no.Click += (s, e) => dialog.Close();
            buttons.Children.Add(yes); buttons.Children.Add(no);

            var root = new StackPanel();
            root.Children.Add(body); root.Children.Add(buttons);
            dialog.Content = root;
            dialog.Loaded += (s, e) => no.Focus();
            dialog.ShowDialog();
            return confirmed;
        }

        void DeleteConfig()
        {
            var config = SelectedConfig();
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
