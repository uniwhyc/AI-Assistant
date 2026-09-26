using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AI_Assistant
{
    public sealed class ClaudeModelsPanel : DockPanel
    {
        readonly TextBox url = new TextBox { Text = "https://api.anthropic.com" };
        readonly ComboBox provider = new ComboBox { ItemsSource = ClaudeModelPreset.All, SelectedIndex = 1, MinHeight = 40 };
        readonly PasswordBox key = new PasswordBox { MinHeight = 40, Padding = new Thickness(12, 8, 12, 8),
            Background = Color("#20262D"), Foreground = Color("#F0F2F4"), BorderBrush = Color("#414A56") };
        readonly Button fetch = new Button { Content = "获取模型列表", HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly TextBlock message = new TextBlock { Text = "查询后可选择并复制模型 ID，再手动填入左侧 JSON。", TextWrapping = TextWrapping.Wrap,
            Foreground = Color("#A1AAB4"), Margin = new Thickness(0, 10, 0, 10) };
        readonly TextBox models = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas") };
        readonly Func<string, string, CancellationToken, Task<List<string>>> query;
        CancellationTokenSource pending;
        bool selectingPreset;

        public ClaudeModelsPanel() : this(ClaudeModels.FetchAsync) { }

        public ClaudeModelsPanel(Func<string, string, CancellationToken, Task<List<string>>> query)
        {
            this.query = query;
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = "查询可用模型", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            Field(header, "服务商／Coding Plan", provider);
            Field(header, "请求 URL（基础地址或模型接口）", url);
            Field(header, "API Key（仅用于查询）", key);
            header.Children.Add(fetch); header.Children.Add(message);
            DockPanel.SetDock(header, Dock.Top); Children.Add(header);
            AutomationProperties.SetName(models, "可用模型 ID 列表"); Children.Add(models);
            fetch.Click += async (s, e) => {
                if (pending != null) { pending.Cancel(); return; }
                await Fetch();
            };
            provider.SelectionChanged += (s, e) => SelectPreset();
            url.TextChanged += (s, e) => {
                if (!selectingPreset && provider.SelectedIndex > 0) provider.SelectedIndex = 0;
                ClearResults();
            };
            key.PasswordChanged += (s, e) => ClearResults();
            Unloaded += (s, e) => { if (pending != null) pending.Cancel(); key.Clear(); };
            SelectPreset();
        }

        void SelectPreset()
        {
            var preset = provider.SelectedItem as ClaudeModelPreset;
            if (preset == null) return;
            selectingPreset = true;
            try
            {
                key.Clear(); models.Clear();
                if (preset.Address.Length > 0) url.Text = preset.Address;
                key.IsEnabled = !preset.IsReference;
                fetch.Content = preset.IsReference ? "查看套餐参考模型" : "获取模型列表";
                message.Text = preset.IsReference
                    ? "此地址用于套餐配置。未确认公开列表接口，可查看参考模型；不查询账户、不需要 Key。"
                    : "填入该服务的 Key 查询模型。模型 ID 需自行填入 JSON，列表不代表 Claude 协议兼容性。";
                message.ToolTip = preset.Source;
            }
            finally { selectingPreset = false; }
        }

        static Brush Color(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }
        static void Field(Panel panel, string label, FrameworkElement control)
        {
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            control.Margin = new Thickness(0, 0, 0, 10); AutomationProperties.SetName(control, label); panel.Children.Add(control);
        }

        void ClearResults()
        {
            models.Clear();
            if (pending == null && !selectingPreset) message.Text = "查询信息已更新，请重新获取模型列表。";
        }

        async Task Fetch()
        {
            var preset = provider.SelectedItem as ClaudeModelPreset;
            if (preset != null && preset.IsReference)
            {
                models.Text = String.Join(Environment.NewLine, preset.ReferenceModels);
                message.Text = "套餐参考模型（2026-09-14 核对），非实时账户查询；具体可用性以套餐权限为准。";
                return;
            }
            using (var cancellation = new CancellationTokenSource())
            {
                pending = cancellation; url.IsEnabled = false; key.IsEnabled = false; provider.IsEnabled = false;
                fetch.Content = "取消查询"; models.Clear(); message.Text = "正在获取模型列表…";
                try
                {
                    var result = await query(url.Text, key.Password, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    models.Text = String.Join(Environment.NewLine, result);
                    message.Text = result.Count == 0 ? "接口返回了空模型列表。" : "已获取 " + result.Count + " 个模型，可复制 ID 到左侧 JSON。";
                }
                catch (OperationCanceledException) { message.Text = cancellation.IsCancellationRequested ? "查询已取消。" : "查询超时，请检查服务后重试。"; }
                catch (HttpRequestException) { message.Text = "无法连接模型接口，请检查网络、URL 和服务状态。"; }
                catch (InvalidOperationException ex) { message.Text = ex.Message; }
                catch (ArgumentException) { message.Text = "URL 或 API Key 格式无效，请检查后重试。"; }
                finally { pending = null; fetch.Content = "获取模型列表"; url.IsEnabled = true; key.IsEnabled = true; provider.IsEnabled = true; }
            }
        }
    }
}
