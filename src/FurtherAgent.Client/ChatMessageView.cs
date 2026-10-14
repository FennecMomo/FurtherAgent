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
            Margin = kind == "user" ? new Thickness(70, 12, 12, 34) : new Thickness(10, 8, 10, 18);
            Padding = kind == "user" ? new Thickness(18, 13, 18, 13) : new Thickness(0);
            CornerRadius = new CornerRadius(22);
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kind == "user" ? "#E7F3FF" : "#FFFFFF"));
        }
        if (entry.Operations is { } operations)
        {
            var items = new StackPanel();
            var owner = Window.GetWindow(this) as MainWindow;
            string? key = owner?.OperationGroupKey(raw);
            void Fill()
            {
                if (items.Children.Count > 0) return;
                foreach (var operation in operations) items.Children.Add(new ChatMessageView { DataContext = operation });
            }
            var group = new Expander { Header = entry.Text, IsExpanded = owner?.OperationGroupExpanded(key) == true, Content = items };
            if (group.IsExpanded) Fill();
            group.Expanded += (_, e) => { if (e.OriginalSource == group) { Fill(); owner?.SetOperationGroupExpanded(key, true); } };
            group.Collapsed += (_, e) => { if (e.OriginalSource == group) owner?.SetOperationGroupExpanded(key, false); };
            Child = group; return;
        }
        if (kind is "tool" or "reasoning" or "system")
        {
            Child = new TextBlock { Text = OperationSummary.Display(entry).Text,
                TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brushes.Gray };
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
