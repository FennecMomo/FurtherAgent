using System.IO;
using System.Text.Json;
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
    private Border compactBall = null!;
    private Size expandedCompactSize = new(420, 210);
    private ResizeMode expandedResizeMode;
    private System.Windows.Point ballPress;
    private bool ballDragged;
    private Rect fullBounds;
    private WindowState fullState;
    private WindowStyle fullStyle;
    private WindowChrome? fullChrome;
    private string compactSettingsPath = Path.Combine(Path.GetDirectoryName(SettingsFile)!, "compact-window.json");
    private record CompactPosition(double Left, double Top, double Width = 420, double Height = 210);
    private CompactPosition? compactPosition;
    private double fullMinWidth, fullMinHeight;

    private void InitializeCompact()
    {
        fullSurface = (FrameworkElement)Content; Content = null;
        var root = new Grid(); root.Children.Add(fullSurface); Content = root;
        compactSurface = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.White, Margin = new Thickness(6) };
        compactSurface.RowDefinitions.Add(new() { Height = new GridLength(22) });
        compactContextRow = new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 40 };
        compactSurface.RowDefinitions.Add(compactContextRow);
        compactSplitterRow = new() { Height = new GridLength(5) };
        compactSurface.RowDefinitions.Add(compactSplitterRow);
        compactSurface.RowDefinitions.Add(new() { Height = new GridLength(64), MinHeight = 56 });
        root.Children.Add(compactSurface);
        compactBall = new Border { Visibility = Visibility.Collapsed, Background = Brushes.LightGray,
            CornerRadius = new CornerRadius(22), Width = 44, Height = 44, Cursor = Cursors.Hand,
            Focusable = true, ToolTip = "点击展开，按住拖动", Child = new TextBlock { Text = "•", FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray } };
        System.Windows.Automation.AutomationProperties.SetName(compactBall, "展开迷你窗口");
        root.Children.Add(compactBall);
        compactBall.MouseLeftButtonDown += (_, e) => { ballPress = e.GetPosition(compactBall); ballDragged = false; compactBall.CaptureMouse(); e.Handled = true; };
        compactBall.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || !compactBall.IsMouseCaptured) return;
            var point = e.GetPosition(compactBall);
            if (Math.Abs(point.X - ballPress.X) + Math.Abs(point.Y - ballPress.Y) < 5) return;
            ballDragged = true; compactBall.ReleaseMouseCapture(); DragMove(); SaveCompactPosition();
        };
        compactBall.MouseLeftButtonUp += (_, e) => { compactBall.ReleaseMouseCapture(); if (!ballDragged) ToggleCompactContext(); e.Handled = true; };
        compactBall.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { ToggleCompactContext(); e.Handled = true; } };
        compactSurface.SizeChanged += (_, _) =>
        {
            if (compactMode && !compactContextHidden)
                compactContextRow.MaxHeight = Math.Max(40, compactSurface.ActualHeight - 96);
        };
        var positionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        positionTimer.Tick += (_, _) => { positionTimer.Stop(); SaveCompactPosition(); };
        LocationChanged += (_, _) => { if (compactMode) { positionTimer.Stop(); positionTimer.Start(); } };
        Closing += (_, _) => SaveCompactPosition();
        var header = new DockPanel { Background = Brushes.Transparent, LastChildFill = true, Cursor = Cursors.SizeAll };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Arrow };
        DockPanel.SetDock(buttons, Dock.Right); header.Children.Add(buttons);
        Button Small(string text, string hint, RoutedEventHandler click)
        {
            var button = new Button { Content = text, ToolTip = hint, Width = 28, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0), FontSize = 12 };
            System.Windows.Automation.AutomationProperties.SetName(button, hint);
            button.Click += click; buttons.Children.Add(button); return button;
        }
        Small("⌄", "向右下角收成小球", (_, _) => ToggleCompactContext());
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
        compactComposer = new TextBox { FontSize = 13, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(7), Margin = new Thickness(0), MinHeight = 48, Background = Brushes.White, Foreground = Brushes.Black, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Top };
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
        compactContextHidden = false;
        WindowState = WindowState.Normal; MinWidth = 280; MinHeight = 180;
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        Width = 420; Height = 210;
        compactMode = true; fullSurface.Visibility = Visibility.Collapsed; compactSurface.Visibility = Visibility.Visible;
        try { compactPosition ??= JsonSerializer.Deserialize<CompactPosition>(File.ReadAllText(compactSettingsPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        if (compactPosition is { } saved && double.IsFinite(saved.Left) && double.IsFinite(saved.Top))
        {
            if (double.IsFinite(saved.Width) && double.IsFinite(saved.Height))
            { Width = Math.Clamp(saved.Width, 280, Math.Max(280, SystemParameters.VirtualScreenWidth)); Height = Math.Clamp(saved.Height, 180, Math.Max(180, SystemParameters.VirtualScreenHeight)); }
            Left = Math.Clamp(saved.Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Clamp(saved.Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
        }
        RefreshCompact(); compactComposer.Focus();
    }
    private void ToggleCompactContext()
    {
        if (!compactMode) return;
        double right = Left + Width, bottom = Top + Height;
        if (!compactContextHidden)
        {
            expandedCompactSize = new(Width, Height); expandedResizeMode = ResizeMode;
            compactContextHidden = true;
            compactSurface.Visibility = Visibility.Collapsed; compactBall.Visibility = Visibility.Visible;
            MinWidth = MinHeight = 44; ResizeMode = ResizeMode.NoResize;
            Width = Height = 44; Left = right - 44; Top = bottom - 44;
            SetCompactBallRegion(true); compactBall.Focus();
        }
        else
        {
            compactContextHidden = false; SetCompactBallRegion(false);
            compactBall.Visibility = Visibility.Collapsed; compactSurface.Visibility = Visibility.Visible;
            ResizeMode = expandedResizeMode; MinWidth = 280; MinHeight = 180;
            Width = expandedCompactSize.Width; Height = expandedCompactSize.Height;
            Left = right - Width; Top = bottom - Height;
            compactComposer.Focus();
        }
        SaveCompactPosition();
    }
    private void SetCompactBallRegion(bool enabled)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (!enabled) { SetWindowRgn(handle, IntPtr.Zero, true); return; }
        var dpi = VisualTreeHelper.GetDpi(this);
        var region = CreateEllipticRgn(0, 0, (int)Math.Round(44 * dpi.DpiScaleX), (int)Math.Round(44 * dpi.DpiScaleY));
        if (SetWindowRgn(handle, region, true) == 0) DeleteObject(region);
    }
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);

    private void RestoreCompact()
    {
        if (!compactMode) return;
        SaveCompactPosition();
        if (compactContextHidden) { SetCompactBallRegion(false); ResizeMode = expandedResizeMode; compactContextHidden = false; }
        compactBall.Visibility = Visibility.Collapsed;
        compactMode = false; compactSurface.Visibility = Visibility.Collapsed; fullSurface.Visibility = Visibility.Visible;
        MinWidth = fullMinWidth; MinHeight = fullMinHeight; WindowStyle = fullStyle; WindowChrome.SetWindowChrome(this, fullChrome);
        Left = fullBounds.Left; Top = fullBounds.Top; Width = fullBounds.Width; Height = fullBounds.Height; WindowState = fullState;
        Composer.Focus();
    }
    private void SaveCompactPosition()
    {
        if (!compactMode || WindowState != WindowState.Normal || !double.IsFinite(Left) || !double.IsFinite(Top)) return;
        compactPosition = compactContextHidden
            ? new(Left + Width - expandedCompactSize.Width, Top + Height - expandedCompactSize.Height, expandedCompactSize.Width, expandedCompactSize.Height)
            : new(Left, Top, Width, Height);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(compactSettingsPath)!);
            string temporary = compactSettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(compactPosition));
            File.Move(temporary, compactSettingsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
