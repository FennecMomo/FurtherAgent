using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using FurtherAgent.Core;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace FurtherAgent.Client;

internal sealed class MiniWindow : Window
{
    private readonly string server, code, sessionId, positionPath;
    private readonly Func<string> readDraft;
    private readonly Action<string> writeDraft;
    private readonly Action showMain;
    private readonly CancellationTokenSource lifetime = new();
    private RegistryClient? registry;
    private RemoteDevice? remote;
    private AgentInfo? agent;
    private SessionView? view;
    private readonly Grid surface = new() { Background = Brushes.White, Margin = new Thickness(6) };
    private readonly TextBlock context = new() { FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Padding = new Thickness(4, 0, 4, 0) };
    private readonly ScrollViewer scroll;
    private readonly TextBox composer = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(7), MinHeight = 48, Background = Brushes.White, Foreground = Brushes.Black, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Top };
    private readonly Button send = new() { Content = "↑", Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Bottom, FontSize = 20 };
    private readonly Border ball;
    private readonly TextBlock title = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse statusDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock statusLabel = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border statusBadge = new() { Background = Brushes.WhiteSmoke, CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RowDefinition contextRow = new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 40 };
    private readonly System.Windows.Threading.DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool closed, collapsed, sending, settingDraft, dragging, ballDragged, attached, sessionConnecting = true, followLatest = true;
    private int scrollRevision;
    private Point ballPress;
    private double dragY, dragOffset;
    private Size expandedSize = new(420, 210);
    private ResizeMode expandedResizeMode;
    private record CompactPosition(double Left, double Top, double Width, double Height);

    public bool IsSending => sending;
    public bool HasUnsentDraft => !string.IsNullOrWhiteSpace(composer.Text);
    private void SetStatus(string message) { title.ToolTip = message; send.ToolTip = message; }

    public MiniWindow(string server, string code, string sessionId, string sessionTitle, SessionView initialView,
        string positionPath, Func<string> readDraft, Action<string> writeDraft, Action showMain, int cascade = 0)
    {
        this.server = server; this.code = code; this.sessionId = sessionId; this.positionPath = positionPath;
        if (string.IsNullOrWhiteSpace(sessionTitle)) sessionTitle = sessionId;
        this.readDraft = readDraft; this.writeDraft = writeDraft; this.showMain = showMain;
        view = initialView;
        Title = "迷你 · " + sessionTitle;
        Width = 420; Height = 210; MinWidth = 280; MinHeight = 180;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; Topmost = true;
        Background = Brushes.White;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        Left = SystemParameters.WorkArea.Right - Width - 30 - cascade;
        Top = SystemParameters.WorkArea.Bottom - Height - 30 - cascade;
        LoadPosition(cascade == 0);

        var root = new Grid(); Content = root;
        surface.RowDefinitions.Add(new() { Height = new GridLength(24) });
        surface.RowDefinitions.Add(contextRow);
        surface.RowDefinitions.Add(new() { Height = new GridLength(5) });
        surface.RowDefinitions.Add(new() { Height = new GridLength(64), MinHeight = 56 });
        root.Children.Add(surface);

        var header = new DockPanel { Background = Brushes.Transparent, LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right); header.Children.Add(buttons);
        Button Small(string text, string hint, RoutedEventHandler action)
        {
            var button = new Button { Content = text, ToolTip = hint, Width = 28, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0), FontSize = 12 };
            AutomationProperties.SetName(button, hint); button.Click += action; buttons.Children.Add(button); return button;
        }
        Small("⌄", "收成小球", (_, _) => ToggleContext());
        Small("↗", "显示主窗口", (_, _) => showMain());
        Small("×", "关闭此迷你窗口", (_, _) => Close());
        var statusContent = new StackPanel { Orientation = Orientation.Horizontal };
        statusContent.Children.Add(statusDot); statusContent.Children.Add(statusLabel);
        statusBadge.Child = statusContent;
        DockPanel.SetDock(statusBadge, Dock.Right); header.Children.Add(statusBadge);
        title.Text = sessionTitle; title.ToolTip = sessionTitle;
        title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); e.Handled = true; } };
        header.Children.Add(title); surface.Children.Add(header);

        scroll = new ScrollViewer { Content = context, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Background = Brushes.Transparent, Cursor = Cursors.Hand, CanContentScroll = false };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            double target = Math.Clamp(scroll.VerticalOffset - e.Delta, 0, scroll.ScrollableHeight);
            followLatest = target >= scroll.ScrollableHeight - 2;
            scroll.ScrollToVerticalOffset(target);
            if (followLatest) RenderLatestText();
        };
        scroll.PreviewMouseLeftButtonDown += (_, e) => { dragging = true; dragY = e.GetPosition(scroll).Y; dragOffset = scroll.VerticalOffset; scroll.CaptureMouse(); e.Handled = true; };
        scroll.PreviewMouseMove += (_, e) =>
        {
            if (!dragging) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
            double target = Math.Clamp(dragOffset + dragY - e.GetPosition(scroll).Y, 0, scroll.ScrollableHeight);
            if (Math.Abs(target - scroll.VerticalOffset) > 0.5) followLatest = target >= scroll.ScrollableHeight - 2;
            scroll.ScrollToVerticalOffset(target); e.Handled = true;
        };
        scroll.PreviewMouseLeftButtonUp += (_, e) => { if (dragging) { EndDrag(); e.Handled = true; } };
        scroll.LostMouseCapture += (_, _) => dragging = false;
        Grid.SetRow(scroll, 1); surface.Children.Add(scroll);
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(splitter, 2); surface.Children.Add(splitter);
        var input = new Grid(); input.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); input.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        composer.Text = readDraft(); composer.TextChanged += (_, _) => { if (!settingDraft) writeDraft(composer.Text); };
        composer.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && send.IsEnabled) { e.Handled = true; _ = SendAsync(); } };
        input.Children.Add(composer);
        AutomationProperties.SetName(send, "发送消息"); send.Click += (_, _) => _ = SendAsync();
        Grid.SetColumn(send, 1); input.Children.Add(send);
        Grid.SetRow(input, 3); surface.Children.Add(input);

        ball = new Border { Visibility = Visibility.Collapsed, Background = Brushes.LightGray, CornerRadius = new CornerRadius(22), Width = 44, Height = 44, Cursor = Cursors.Hand,
            Focusable = true, ToolTip = sessionTitle + " · 点击展开，按住拖动", Child = new TextBlock { Text = "•", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray } };
        AutomationProperties.SetName(ball, "展开迷你窗口：" + sessionTitle);
        root.Children.Add(ball);
        ball.MouseLeftButtonDown += (_, e) => { ballPress = e.GetPosition(ball); ballDragged = false; ball.CaptureMouse(); e.Handled = true; };
        ball.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || !ball.IsMouseCaptured) return;
            var point = e.GetPosition(ball);
            if (Math.Abs(point.X - ballPress.X) + Math.Abs(point.Y - ballPress.Y) < 5) return;
            ballDragged = true; ball.ReleaseMouseCapture(); DragMove(); SavePosition();
        };
        ball.MouseLeftButtonUp += (_, e) => { ball.ReleaseMouseCapture(); if (!ballDragged) ToggleContext(); e.Handled = true; };
        ball.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { ToggleContext(); e.Handled = true; } };
        surface.SizeChanged += (_, _) => { if (!collapsed) contextRow.MaxHeight = Math.Max(40, surface.ActualHeight - 96); };
        positionTimer.Tick += (_, _) => { positionTimer.Stop(); SavePosition(); };
        LocationChanged += (_, _) => { if (!closed) { positionTimer.Stop(); positionTimer.Start(); } };
        SizeChanged += (_, _) => { if (!closed && !collapsed) { positionTimer.Stop(); positionTimer.Start(); } };
        Closed += async (_, _) =>
        {
            closed = true; positionTimer.Stop(); SavePosition(); lifetime.Cancel();
            if (remote is not null) await remote.DisposeAsync();
            registry?.Dispose(); lifetime.Dispose();
        };
        Refresh(initialView);
        send.IsEnabled = false;
        composer.IsEnabled = false;
    }

    public void SetDraft(string text)
    {
        if (composer.Text == text) return;
        settingDraft = true;
        try { composer.Text = text; } finally { settingDraft = false; }
    }

    public void RestoreAndActivate()
    {
        if (collapsed) ToggleContext();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate(); composer.Focus();
    }

    public void ToggleContext()
    {
        EndDrag();
        double right = Left + Width, bottom = Top + Height;
        if (!collapsed)
        {
            expandedSize = new(Width, Height); expandedResizeMode = ResizeMode; collapsed = true;
            surface.Visibility = Visibility.Collapsed; ball.Visibility = Visibility.Visible;
            MinWidth = MinHeight = 44; ResizeMode = ResizeMode.NoResize;
            Width = Height = 44; Left = right - 44; Top = bottom - 44;
            SetBallRegion(true); Opacity = 0.3; ball.Focus();
        }
        else
        {
            collapsed = false; Opacity = 1; SetBallRegion(false); ball.Visibility = Visibility.Collapsed; surface.Visibility = Visibility.Visible;
            ResizeMode = expandedResizeMode; MinWidth = 280; MinHeight = 180;
            Width = expandedSize.Width; Height = expandedSize.Height;
            Left = right - Width; Top = bottom - Height; if (followLatest) ScrollToLatestAfterLayout(); composer.Focus();
        }
        SavePosition();
    }

    public async Task StartAsync()
    {
        try
        {
            registry = new RegistryClient(server); remote = new RemoteDevice();
            var connection = remote;
            connection.Notification += (method, node) =>
            {
                if (method != "session" || node is null) return;
                var update = Json.Read<SessionView>(node);
                Dispatcher.BeginInvoke(() => { if (!closed && update.Id == sessionId) Refresh(update); });
            };
            connection.ConnectionChanged += (online, message) => Dispatcher.BeginInvoke(() =>
            {
                if (closed) return;
                if (!online) attached = false;
                sessionConnecting = !online || !attached;
                send.IsEnabled = online && attached && !sending && view?.CanSend == true;
                composer.IsEnabled = send.IsEnabled;
                UpdateRunStatus();
                SetStatus(online ? view?.Title ?? sessionId : message);
            });
            connection.Reconnected += () => Dispatcher.BeginInvoke(async () =>
            {
                if (closed) return;
                try
                {
                    var restored = Json.Read<SessionView>(await connection.Peer.CallAsync("attach", new { id = sessionId }, lifetime.Token));
                    attached = true; sessionConnecting = false; Refresh(restored);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { if (!closed) SetStatus("重连后读取会话失败：" + ex.Message); }
            });
            await connection.ConnectAsync(registry, code, lifetime.Token);
            if (closed) return;
            try { await OperationGroups.NegotiateAsync(connection.Peer); } catch (IOException) { }
            agent = Json.Read<AgentInfo[]>(await connection.Peer.CallAsync("agents", null, lifetime.Token)).FirstOrDefault(a => a.Available);
            var loaded = Json.Read<SessionView>(await connection.Peer.CallAsync("attach", new { id = sessionId }, lifetime.Token));
            attached = true; sessionConnecting = false; Refresh(loaded);
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            if (!closed) { sessionConnecting = false; UpdateRunStatus(); SetStatus("迷你窗口连接失败：" + ex.Message); context.Text = "连接失败：" + ex.Message + "\n请关闭后从主窗口重新打开。"; send.IsEnabled = false; composer.IsEnabled = false; }
        }
    }

    private void Refresh(SessionView update)
    {
        if (update.Id != sessionId || closed) return;
        view = update;
        if (!string.IsNullOrWhiteSpace(update.Title)) { Title = "迷你 · " + update.Title; title.Text = update.Title; SetStatus(update.Title); ball.ToolTip = update.Title + " · 点击展开，按住拖动"; }
        UpdateRunStatus();
        send.IsEnabled = attached && remote?.IsConnected == true && !sending && update.CanSend;
        composer.IsEnabled = send.IsEnabled;
        RenderLatestText();
    }

    private void RenderLatestText()
    {
        if (dragging || !followLatest || view is null) return;
        string text = string.Join("\n", view.Messages.Where(HistoryPaging.IsConversation)
            .Select(m => (ChatPresentation.Kind(m) == "user" ? "你：" : "") + ChatPresentation.Display(m).Text));
        if (context.Text != text) context.Text = text;
        ScrollToLatestAfterLayout();
    }

    private void ScrollToLatestAfterLayout()
    {
        int revision = ++scrollRevision;
        scroll.ScrollToEnd();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (closed || collapsed || dragging || !followLatest || revision != scrollRevision) return;
            scroll.UpdateLayout();
            scroll.ScrollToEnd();
        }));
    }

    private async Task SendAsync()
    {
        if (sending || !attached || remote?.IsConnected != true || view?.CanSend != true) return;
        string text = composer.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        sending = true; send.IsEnabled = false; composer.IsEnabled = false;
        UpdateRunStatus();
        try
        {
            var peer = remote.Peer;
            Task submission = agent?.CanFiles == true
                ? new FileTransferClient(peer).SendAsync(sessionId, text, [], responseTimeout: TimeSpan.FromMinutes(3))
                : peer.CallAsync("send", new { text, delivery = "auto" }, responseTimeout: TimeSpan.FromMinutes(3));
            if (await Task.WhenAny(submission, Task.Delay(TimeSpan.FromSeconds(12))) != submission)
                SetStatus("远端确认较慢，仍在等待原请求；请勿重复发送。");
            await submission;
            if (composer.Text == text) { SetDraft(""); writeDraft(""); }
            SetStatus("消息已由远端接收，结果以会话状态为准。");
        }
        catch (TimeoutException) { SetStatus("确认超时，发送结果未知。请先查看会话结果，不要重复提交。"); }
        catch (Exception ex) { SetStatus("发送失败：" + ex.Message); }
        finally { sending = false; send.IsEnabled = attached && remote?.IsConnected == true && view?.CanSend == true; composer.IsEnabled = send.IsEnabled; UpdateRunStatus(); }
    }

    private void UpdateRunStatus()
    {
        var indicator = sending ? new AgentIndicator("提交中", "#D97706")
            : AgentIndicator.From(attached && remote?.IsConnected == true, sessionConnecting, view);
        statusLabel.Text = indicator.Label;
        var color = (Brush)new BrushConverter().ConvertFromString(indicator.Color)!;
        statusDot.Fill = color;
        ((TextBlock)ball.Child).Foreground = color;
        string detail = view?.StatusDetail is { Length: > 0 } description ? " · " + description : "";
        string hint = indicator.Label + detail;
        statusBadge.ToolTip = hint;
        ball.ToolTip = (view?.Title ?? sessionId) + " · " + hint + " · 点击展开，按住拖动";
        AutomationProperties.SetName(statusBadge, "会话状态：" + indicator.Label);
        AutomationProperties.SetName(ball, "展开迷你窗口：" + (view?.Title ?? sessionId) + "，" + indicator.Label);
    }

    private void EndDrag()
    {
        dragging = false;
        if (scroll.IsMouseCaptured) scroll.ReleaseMouseCapture();
        if (followLatest) RenderLatestText();
    }

    private void LoadPosition(bool allowLegacy)
    {
        try
        {
            string source = positionPath;
            if (!File.Exists(source) && allowLegacy)
                source = Path.Combine(Path.GetDirectoryName(positionPath)!, "compact-window.json");
            var saved = JsonSerializer.Deserialize<CompactPosition>(File.ReadAllText(source));
            if (saved is null || !double.IsFinite(saved.Left) || !double.IsFinite(saved.Top) || !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height)) return;
            Width = Math.Clamp(saved.Width, 280, Math.Max(280, SystemParameters.VirtualScreenWidth));
            Height = Math.Clamp(saved.Height, 180, Math.Max(180, SystemParameters.VirtualScreenHeight));
            Left = Math.Clamp(saved.Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Clamp(saved.Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SavePosition()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || WindowState != WindowState.Normal) return;
        var position = collapsed
            ? new CompactPosition(Left + Width - expandedSize.Width, Top + Height - expandedSize.Height, expandedSize.Width, expandedSize.Height)
            : new CompactPosition(Left, Top, Width, Height);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(positionPath)!);
            string temporary = positionPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(position));
            File.Move(temporary, positionPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void SetBallRegion(bool enabled)
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
}
