using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AI_Assistant
{
    public sealed partial class Desktop
    {
        void DrawChart()
        {
            var canvas = Find<Canvas>("TrendCanvas");
            canvas.Children.Clear();
            double width = canvas.ActualWidth, height = canvas.ActualHeight;
            if (width < 100 || height < 50) return;
            double left = 48, top = 12, bottom = height - 26, right = width - 12;
            if (filtered.Count == 0)
            {
                Label(canvas, "此范围暂无用量记录", left + 30, height / 2 - 12, "#A1AAB4");
                Text("ChartCaption", "每日 Token · 按本地时区");
                return;
            }
            DateTime start = rangeStart.HasValue ? rangeStart.Value.Date : filtered.Min(x => x.Time.LocalDateTime.Date);
            int days = Math.Max(1, (rangeEnd.Date - start).Days + 1);
            int bucketSize = Math.Max(1, (int)Math.Ceiling(days / 40.0));
            int count = (int)Math.Ceiling((double)days / bucketSize);
            long[][] values = { new long[count], new long[count] };
            foreach (var item in filtered)
            {
                int index = (item.Time.LocalDateTime.Date - start).Days / bucketSize;
                if (index >= 0 && index < count) values[item.Platform == "Codex" ? 0 : 1][index] += item.Total;
            }
            double maximum = Math.Max(1, Math.Max(values[0].Max(), values[1].Max()) * 1.15);
            for (int tick = 0; tick <= 3; tick++)
            {
                double y = top + (bottom - top) * tick / 3;
                canvas.Children.Add(new Line { X1 = left, X2 = right, Y1 = y, Y2 = y, Stroke = Brush("#303741"), StrokeDashArray = new DoubleCollection { 3, 4 } });
                Label(canvas, Compact((long)(maximum * (3 - tick) / 3)), 0, y - 8, "#A1AAB4", 10);
            }
            double step = (right - left) / Math.Max(1, count - 1);
            string[] colors = { "#71E3BF", "#EFB18A" };
            string[] platforms = { "Codex", "Claude Code" };
            for (int series = 0; series < 2; series++)
            {
                if (selectedPlatform != "全部平台" && selectedPlatform != platforms[series]) continue;
                var points = new PointCollection();
                for (int i = 0; i < count; i++)
                    points.Add(new Point(count == 1 ? (left + right) / 2 : left + i * step, bottom - values[series][i] / maximum * (bottom - top)));
                var fill = new PointCollection(points);
                fill.Insert(0, new Point(points[0].X, bottom)); fill.Add(new Point(points[points.Count - 1].X, bottom));
                canvas.Children.Add(new Polygon { Points = fill, Fill = Brush(colors[series]), Opacity = 0.055 });
                canvas.Children.Add(new Polyline { Points = points, Stroke = Brush(colors[series]), StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false });
                if (count <= 10)
                    foreach (var point in points)
                    {
                        var dot = new Ellipse { Width = 6, Height = 6, Fill = Brush(colors[series]), IsHitTestVisible = false };
                        Canvas.SetLeft(dot, point.X - 3); Canvas.SetTop(dot, point.Y - 3); canvas.Children.Add(dot);
                    }
            }
            int labelEvery = Math.Max(1, (int)Math.Ceiling(count / 6.0));
            for (int i = 0; i < count; i++)
            {
                double x = count == 1 ? (left + right) / 2 : left + i * step;
                DateTime date = start.AddDays(i * bucketSize);
                if (i % labelEvery == 0 || i == count - 1 && count % labelEvery > 1)
                    Label(canvas, date.ToString(days > 365 ? "yy/MM" : "MM/dd"), Math.Min(right - 32, x - 13), bottom + 9, "#A1AAB4", 10);
                string dateLabel = date.ToString("yyyy-MM-dd");
                if (bucketSize > 1) dateLabel += " 至 " + start.AddDays(Math.Min(days - 1, (i + 1) * bucketSize - 1)).ToString("yyyy-MM-dd");
                string tip = dateLabel;
                for (int series = 0; series < 2; series++)
                    if (selectedPlatform == "全部平台" || selectedPlatform == platforms[series])
                        tip += String.Format("\n{0}：{1:N0} Token", platforms[series], values[series][i]);
                var hit = new Border { Width = Math.Max(8, count == 1 ? right - left : step), Height = bottom - top,
                    Background = Brushes.Transparent, ToolTip = tip };
                System.Windows.Automation.AutomationProperties.SetName(hit, tip);
                Canvas.SetLeft(hit, count == 1 ? left : x - step / 2); Canvas.SetTop(hit, top); canvas.Children.Add(hit);
            }
            Text("ChartCaption", bucketSize == 1 ? "每日 Token · 按本地时区 · 悬停查看详情" : String.Format("每 {0} 天合计 · 按本地时区 · 悬停查看详情", bucketSize));
        }

        void Label(Canvas canvas, string text, double x, double y, string color, double size = 11)
        {
            var label = new TextBlock { Text = text, Foreground = Brush(color), FontSize = size, FontFamily = new FontFamily("Segoe UI"), IsHitTestVisible = false };
            Canvas.SetLeft(label, x); Canvas.SetTop(label, y); canvas.Children.Add(label);
        }
    }
}
