using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public partial class MainWindow
{
    private FrameworkElement fullSurface = null!;
    private Grid compactSurface = null!;
    private TextBox compactComposer = null!;
    private TextBlock compactContext = null!;
    private ScrollViewer compactScroll = null!;
    private RowDefinition compactContextRow = null!, compactSplitterRow = null!;
    private bool compactMode, compactContextHidden;
    private Rect fullBounds;
    private WindowState fullState;
    private WindowStyle fullStyle;
    private WindowChrome? fullChrome;
    private double fullMinWidth, fullMinHeight, contextHeight = 110;

    private void InitializeCompact()
    {
        fullSurface = (FrameworkElement)Content; Content = null;
        var root = new Grid(); root.Children.Add(fullSurface); Content = root;
        compactSurface = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.White, Margin = new Thickness(6) };
        compactSurface.RowDefinitions.Add(new() { Height = new GridLength(22) });
        compactContextRow = new() { Height = new GridLength(110), MinHeight = 40 };
        compactSurface.RowDefinitions.Add(compactContextRow);
        compactSplitterRow = new() { Height = new GridLength(5) };
        compactSurface.RowDefinitions.Add(compactSplitterRow);
        compactSurface.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 42 });
        root.Children.Add(compactSurface);
        var header = new DockPanel { Background = Brushes.Transparent, LastChildFill = true, Cursor = Cursors.SizeAll };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Arrow };
        DockPanel.SetDock(buttons, Dock.Right); header.Children.Add(buttons);
        Button Small(string text, string hint, RoutedEventHandler click)
        {
            var button = new Button { Content = text, ToolTip = hint, Width = 28, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0), FontSize = 12 };
            System.Windows.Automation.AutomationProperties.SetName(button, hint);
            button.Click += click; buttons.Children.Add(button); return button;
        }
        Small("⌃", "收起 / 展开上下文", (_, _) => ToggleCompactContext());
        Small("↗", "还原大窗口", (_, _) => RestoreCompact());
        var grip = new Border { Background = Brushes.Transparent, ToolTip = "按住拖动窗口" };
        grip.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); e.Handled = true; } };
        header.Children.Add(grip); compactSurface.Children.Add(header);
        compactContext = new TextBlock { FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Padding = new Thickness(4,0,4,0) };
        compactScroll = new ScrollViewer { Content = compactContext, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        Grid.SetRow(compactScroll, 1); compactSurface.Children.Add(compactScroll);
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(splitter, 2); compactSurface.Children.Add(splitter);
        splitter.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = compactScroll });
        var input = new Grid(); input.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); input.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        compactComposer = new TextBox { FontSize = 13, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(7), BorderThickness = new Thickness(0) };
        compactComposer.SetBinding(TextBox.TextProperty, new Binding(nameof(TextBox.Text)) { Source = Composer, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        compactComposer.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = Composer });
        compactComposer.PreviewKeyDown += Composer_KeyDown;
        input.Children.Add(compactComposer);
        var send = new Button { Content = "↑", Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Bottom, FontSize = 20 };
        System.Windows.Automation.AutomationProperties.SetName(send, "发送消息");
        send.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = SendButton });
        send.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = StatusBar });
        send.Click += Send_Click; Grid.SetColumn(send, 1); input.Children.Add(send);
        Grid.SetRow(input, 3); compactSurface.Children.Add(input);
    }
    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        if (compactMode) return;
        fullState = WindowState; fullBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        fullStyle = WindowStyle; fullChrome = WindowChrome.GetWindowChrome(this); fullMinWidth = MinWidth; fullMinHeight = MinHeight;
        WindowState = WindowState.Normal; MinWidth = 280; MinHeight = compactContextHidden ? 90 : 180;
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        Width = 420; Height = compactContextHidden ? 90 : 210;
        compactMode = true; fullSurface.Visibility = Visibility.Collapsed; compactSurface.Visibility = Visibility.Visible;
        RefreshCompact(); compactComposer.Focus();
    }
    private void ToggleCompactContext()
    {
        compactContextHidden = !compactContextHidden;
        if (compactContextHidden) { MinHeight = 90; contextHeight = Math.Max(40, compactContextRow.ActualHeight); compactContextRow.MinHeight = 0; compactContextRow.Height = new GridLength(0); compactSplitterRow.Height = new GridLength(0); compactScroll.Visibility = Visibility.Collapsed; Height = 90; }
        else { MinHeight = 180; compactContextRow.MinHeight = 40; compactContextRow.Height = new GridLength(contextHeight); compactSplitterRow.Height = new GridLength(5); compactScroll.Visibility = Visibility.Visible; Height += contextHeight + 5; }
    }
    private void RestoreCompact()
    {
        if (!compactMode) return;
        compactMode = false; compactSurface.Visibility = Visibility.Collapsed; fullSurface.Visibility = Visibility.Visible;
        MinWidth = fullMinWidth; MinHeight = fullMinHeight; WindowStyle = fullStyle; WindowChrome.SetWindowChrome(this, fullChrome);
        Left = fullBounds.Left; Top = fullBounds.Top; Width = fullBounds.Width; Height = fullBounds.Height; WindowState = fullState;
        Composer.Focus();
    }
    private void RefreshCompact()
    {
        if (!compactMode) return;
        var view = History.Latest ?? current;
        string text = view is null ? "" : string.Join("\n", view.Messages.Where(HistoryPaging.IsConversation).TakeLast(6).Select(m => (ChatPresentation.Kind(m) == "user" ? "你：" : "") + ChatPresentation.Display(m).Text));
        if (text.Length > 6000) text = text[^6000..];
        if (compactContext.Text != text) { compactContext.Text = text; compactScroll.ScrollToEnd(); }
    }
}
