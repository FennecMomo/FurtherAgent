using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public sealed class ChatMessageView : Border
{
    static ChatMessageView() => VisualTheme.IsCodexProperty.OverrideMetadata(typeof(ChatMessageView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((ChatMessageView)d).Render()));
    public ChatMessageView() { DataContextChanged += (_, _) => Render(); Loaded += (_, _) => Render(); }
    private void Render()
    {
        if (DataContext is not ChatEntry raw) return;
        var entry = ChatPresentation.Display(raw);
        string kind = ChatPresentation.Kind(entry);
        bool codex = VisualTheme.GetIsCodex(this);
        Margin = new Thickness(kind == "user" ? 60 : 0, 0, kind == "user" ? 10 : 30, 12);
        Padding = new Thickness(12, 9, 12, 9);
        CornerRadius = new CornerRadius(9);
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kind switch
        { "user" => "#E2EFE8", "assistant" => "#FFFFFF", "reasoning" => "#F0EDF7", _ => "#F0F2F4" }));
        HorizontalAlignment = kind == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        MaxWidth = codex && kind == "user" ? 560 : double.PositiveInfinity;
        if (codex)
        {
            Margin = kind == "user" ? new Thickness(70, 12, 12, 34) : new Thickness(10, 8, 10, 26);
            Padding = kind == "user" ? new Thickness(18, 13, 18, 13) : new Thickness(0);
            CornerRadius = new CornerRadius(22);
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kind == "user" ? "#E7F3FF" : "#FFFFFF"));
        }
        if (kind is "tool" or "reasoning" or "system")
        {
            string label = kind == "tool" ? "工具调用" : kind == "reasoning" ? "思考记录（非正式回复）" : "运行记录";
            string summary = entry.Text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (summary.Length > 72) summary = summary[..72] + "…";
            if (kind == "tool" && (entry.Role is "tool" or "assistant")) summary = entry.Role == "tool" ? "返回结果" : "调用参数";
            var details = new TextBox { Text = entry.Text + (entry.Detail is null ? "" : "\n\n原始字段\n" + entry.Detail),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"),
                FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
            object content = details;
            if (kind == "reasoning")
            {
                var body = new StackPanel();
                body.Children.Add(new MarkdownView { Markdown = entry.Text });
                if (entry.Detail is not null) body.Children.Add(new Expander { Header = "原始字段", Content = details });
                content = new ScrollViewer { Content = body, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            }
            Child = new Expander { Header = label + " · " + summary, IsExpanded = false, Content = content };
            return;
        }
        var panel = new StackPanel();
        if (!codex) panel.Children.Add(new TextBlock { Text = kind == "user" ? "你" : "AI", FontSize = 11,
            Foreground = Brushes.SeaGreen, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new MarkdownView { AttachmentFactory = reference => (Window.GetWindow(this) as MainWindow)?.CreateFileVisual(reference), Markdown = entry.Text });
        if (entry.Detail is not null) panel.Children.Add(new Expander { Header = "调用详情", Content = new TextBox {
            Text = entry.Detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        Child = panel;
    }
}
