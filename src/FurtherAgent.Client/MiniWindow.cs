using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using FurtherAgent.Core;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace FurtherAgent.Client;

internal sealed partial class MiniWindow : Window
{
    private readonly string server, code, sessionId, positionPath;
    private readonly Func<string> readDraft;
    private readonly Action<string> writeDraft;
    private readonly Action showMain;
    private readonly SubmissionRecovery submissionRecovery;
    private readonly (string Device, string Session) submissionKey;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim recoveryWake = new(0, 1);
    private readonly RegistryClient? sharedRegistry;
    private readonly RemoteDevice? sharedRemote;
    private RegistryClient? registry;
    private RemoteDevice? ownedRemote;
    private RemoteSessionConnection? remote;
    private ConnectionHeartbeat? heartbeat;
    private Task? recoveryTask;
    private RpcPeer? attachedPeer;
    private string? recoveryError;
    private AgentInfo? agent;
    private SessionView? view;
    private readonly Grid surface = new() { Background = Brushes.White, Margin = new Thickness(6) };
    private readonly StackPanel context = new() { Margin = new Thickness(4, 0, 4, 0) };
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
    private bool closed, collapsed, sending, settingDraft, dragging, attached, sessionConnecting = true, followLatest = true;
    private int scrollRevision;
    private Point ballPress;
    private double dragY, dragOffset;
    private Size expandedSize = new(420, 210);
    private ResizeMode expandedResizeMode;
    private record CompactPosition(double Left, double Top, double Width, double Height);

    private sealed class TranscriptRow : DockPanel
    {
        public bool IsUser { get; }
        public MarkdownView Body { get; }

        public TranscriptRow(bool isUser, Func<string, FrameworkElement?> attachmentFactory)
        {
            IsUser = isUser;
            Body = new MarkdownView { LimitLength = false, IsTabStop = false, AttachmentFactory = attachmentFactory };
            if (isUser)
            {
                var label = new TextBlock
                {
                    Text = "你：", FontSize = 13, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 0)
                };
                SetDock(label, Dock.Left);
                Children.Add(label);
            }
            Children.Add(Body);
        }
    }

    public bool IsSending => sending;
    public bool HasUnsentDraft => !string.IsNullOrWhiteSpace(composer.Text);
    public int ShortcutSlot { get; }
    private bool SessionReady => attached && remote?.IsConnected == true && ReferenceEquals(attachedPeer, remote.Peer);
    private static bool BallModifiersHeld => Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt);
    private void SetStatus(string message) { title.ToolTip = message; send.ToolTip = message; }

    private void SetSessionTitle(string sessionTitle)
    {
        title.Text = $"{ShortcutSlot % 10} · {sessionTitle}";
        Title = "迷你 · " + title.Text;
    }

    public MiniWindow(string server, string code, string sessionId, string sessionTitle, SessionView initialView,
        string positionPath, Func<string> readDraft, Action<string> writeDraft, Action showMain, int shortcutSlot, int cascade = 0)
        : this(server, code, sessionId, sessionTitle, initialView, positionPath, readDraft, writeDraft, showMain,
            shortcutSlot, cascade, null, null) { }

    public MiniWindow(string server, string code, string sessionId, string sessionTitle, SessionView initialView,
        string positionPath, Func<string> readDraft, Action<string> writeDraft, Action showMain, int shortcutSlot, int cascade,
        RemoteDevice? sharedRemote, RegistryClient? sharedRegistry)
        : this(server, code, sessionId, sessionTitle, initialView, positionPath, readDraft, writeDraft, showMain,
            shortcutSlot, cascade, sharedRemote, sharedRegistry, new SubmissionRecovery(), server) { }

    public MiniWindow(string server, string code, string sessionId, string sessionTitle, SessionView initialView,
        string positionPath, Func<string> readDraft, Action<string> writeDraft, Action showMain, int shortcutSlot, int cascade,
        RemoteDevice? sharedRemote, RegistryClient? sharedRegistry, SubmissionRecovery submissionRecovery, string deviceIdentity)
        : this(server, code, sessionId, sessionTitle, initialView, positionPath, readDraft, writeDraft, showMain,
            shortcutSlot, cascade, sharedRemote, sharedRegistry, submissionRecovery, deviceIdentity, null, null) { }

    internal MiniWindow(string server, string code, string sessionId, string sessionTitle, SessionView initialView,
        string positionPath, Func<string> readDraft, Action<string> writeDraft, Action showMain, int shortcutSlot, int cascade,
        RemoteDevice? sharedRemote, RegistryClient? sharedRegistry, SubmissionRecovery submissionRecovery, string deviceIdentity,
        DownloadedAttachments? chatImages, string? legacyImageScope)
    {
        this.server = server; this.code = code; this.sessionId = sessionId; this.positionPath = positionPath;
        this.sharedRemote = sharedRemote; this.sharedRegistry = sharedRegistry;
        if ((sharedRemote is null) != (sharedRegistry is null)) throw new ArgumentException("共享连接和注册客户端必须同时提供。");
        if (string.IsNullOrWhiteSpace(sessionTitle)) sessionTitle = sessionId;
        this.readDraft = readDraft; this.writeDraft = writeDraft; this.showMain = showMain;
        this.submissionRecovery = submissionRecovery;
        submissionKey = (deviceIdentity, sessionId);
        this.chatImages = chatImages;
        this.legacyImageScope = legacyImageScope;
        view = initialView;
        ShortcutSlot = shortcutSlot;
        SetSessionTitle(sessionTitle);
        Width = 420; Height = 210; MinWidth = 280; MinHeight = 180;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; Topmost = true;
        AllowsTransparency = true;
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
        title.ToolTip = sessionTitle;
        title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); e.Handled = true; } };
        header.Children.Add(title); surface.Children.Add(header);

        scroll = new ScrollViewer { Content = context, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Background = Brushes.Transparent, Cursor = Cursors.Hand, CanContentScroll = false };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            double target = Math.Clamp(scroll.VerticalOffset - e.Delta, 0, scroll.ScrollableHeight);
            followLatest = target >= scroll.ScrollableHeight - 2;
            scroll.ScrollToVerticalOffset(target);
            if (followLatest) RenderLatestMessages();
        };
        scroll.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsHyperlinkSource(e.OriginalSource)) return;
            dragging = true;
            dragY = e.GetPosition(scroll).Y;
            dragOffset = scroll.VerticalOffset;
            scroll.CaptureMouse();
            e.Handled = true;
        };
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
        send.Click += (_, _) => _ = SendAsync();
        Grid.SetColumn(send, 1); input.Children.Add(send);
        Grid.SetRow(input, 3); surface.Children.Add(input);

        ball = new Border { Visibility = Visibility.Collapsed, Background = Brushes.LightGray, CornerRadius = new CornerRadius(22), Width = 44, Height = 44,
            Child = new TextBlock { Text = "•", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray } };
        AutomationProperties.SetName(ball, "迷你窗口小圆点");
        root.Children.Add(ball);
        ball.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (!BallModifiersHeld) return;
            ballPress = e.GetPosition(ball);
            ball.CaptureMouse();
        };
        ball.MouseMove += (_, e) =>
        {
            if (!ball.IsMouseCaptured) return;
            if (e.LeftButton != MouseButtonState.Pressed || !BallModifiersHeld)
            {
                ball.ReleaseMouseCapture();
                return;
            }
            var point = e.GetPosition(ball);
            if (Math.Abs(point.X - ballPress.X) + Math.Abs(point.Y - ballPress.Y) < 5) return;
            ball.ReleaseMouseCapture(); DragMove(); SavePosition();
            e.Handled = true;
        };
        ball.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!ball.IsMouseCaptured) return;
            bool expand = BallModifiersHeld && ball.InputHitTest(e.GetPosition(ball)) is not null;
            ball.ReleaseMouseCapture();
            if (expand) ToggleContext();
        };
        surface.SizeChanged += (_, _) => { if (!collapsed) contextRow.MaxHeight = Math.Max(40, surface.ActualHeight - 96); };
        positionTimer.Tick += (_, _) => { positionTimer.Stop(); SavePosition(); };
        LocationChanged += (_, _) => { if (!closed) { positionTimer.Stop(); positionTimer.Start(); } };
        SizeChanged += (_, _) => { if (!closed && !collapsed) { positionTimer.Stop(); positionTimer.Start(); } };
        Closed += async (_, _) =>
        {
            submissionRecovery.Changed -= OnSubmissionStateChanged;
            closed = true; positionTimer.Stop(); SavePosition(); lifetime.Cancel();
            try { if (recoveryTask is not null) await recoveryTask; }
            finally
            {
                try { if (heartbeat is not null) await heartbeat.DisposeAsync(); }
                finally
                {
                    try { if (remote is not null) await remote.DisposeAsync(); }
                    finally
                    {
                        try { if (ownedRemote is not null) await ownedRemote.DisposeAsync(); }
                        finally { if (sharedRegistry is null) registry?.Dispose(); recoveryWake.Dispose(); lifetime.Dispose(); }
                    }
                }
            }
        };
        submissionRecovery.Changed += OnSubmissionStateChanged;
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
        if (ball.IsMouseCaptured) ball.ReleaseMouseCapture();
        double right = Left + Width, bottom = Top + Height;
        if (!collapsed)
        {
            expandedSize = new(Width, Height); expandedResizeMode = ResizeMode; collapsed = true;
            surface.Visibility = Visibility.Collapsed; ball.Visibility = Visibility.Visible;
            MinWidth = MinHeight = 44; ResizeMode = ResizeMode.NoResize;
            Width = Height = 44; Left = right - 44; Top = bottom - 44;
            Background = Brushes.Transparent; Opacity = 0.18;
        }
        else
        {
            collapsed = false; Background = Brushes.White; Opacity = 1; ball.Visibility = Visibility.Collapsed; surface.Visibility = Visibility.Visible;
            ResizeMode = expandedResizeMode; MinWidth = 280; MinHeight = 180;
            Width = expandedSize.Width; Height = expandedSize.Height;
            Left = right - Width; Top = bottom - Height;
            RenderLatestMessages();
            composer.Focus();
        }
        SavePosition();
    }

    public Task StartAsync()
    {
        if (closed) return Task.CompletedTask;
        return recoveryTask ??= RecoverAsync(lifetime.Token);
    }

    private void WakeRecovery()
    {
        if (closed) return;
        try { recoveryWake.Release(); } catch (SemaphoreFullException) { }
    }

    private void Observe(RemoteSessionConnection connection)
    {
        connection.Notification += (method, node) =>
        {
            if (method != "session" || node is null) return;
            var notificationPeer = connection.Peer;
            var update = Json.Read<SessionView>(node);
            Dispatcher.BeginInvoke(() =>
            {
                if (!closed && remote == connection && connection.Peer == notificationPeer && update.Id == sessionId) Refresh(update);
            });
        };
        connection.ConnectionChanged += (online, message) => Dispatcher.BeginInvoke(() =>
        {
            if (closed || remote != connection) return;
            attached = false; attachedPeer = null;
            sessionConnecting = !connection.IsNetworkConnected;
            recoveryError = online ? null : message;
            send.IsEnabled = composer.IsEnabled = false;
            UpdateRunStatus();
            WakeRecovery();
        });
        connection.Reconnected += () => Dispatcher.BeginInvoke(() =>
        {
            if (closed || remote != connection) return;
            WakeRecovery();
        });
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            registry = sharedRegistry ?? new RegistryClient(server);
            var owner = sharedRemote;
            if (owner is null)
            {
                int attempt = 0;
                while (!ct.IsCancellationRequested)
                {
                    owner = new RemoteDevice();
                    ownedRemote = owner;
                    sessionConnecting = true;
                    UpdateRunStatus();
                    try { await owner.ConnectAsync(registry, code, ct); break; }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        await owner.DisposeAsync(); ownedRemote = null; owner = null;
                        recoveryError = ex.Message;
                        UpdateRunStatus();
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 1 << Math.Min(attempt++, 4))), ct);
                    }
                }
                ct.ThrowIfCancellationRequested();
                heartbeat = new ConnectionHeartbeat(registry, owner!);
                heartbeat.Start();
            }
            var connection = new RemoteSessionConnection(owner!, registry, code);
            remote = connection;
            Observe(connection);
            sessionConnecting = !connection.IsNetworkConnected;
            UpdateRunStatus();
            await connection.ConnectAsync(ct);
            ct.ThrowIfCancellationRequested();
            sessionConnecting = false;
            await RestoreSessionAsync(connection, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!closed)
            {
                sessionConnecting = false; recoveryError = ex.Message;
                send.IsEnabled = composer.IsEnabled = false;
                UpdateRunStatus();
            }
        }
    }

    private async Task RestoreSessionAsync(RemoteSessionConnection connection, CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!connection.IsConnected)
            {
                attached = false; attachedPeer = null; sessionConnecting = !connection.IsNetworkConnected;
                send.IsEnabled = composer.IsEnabled = false;
                UpdateRunStatus();
                await recoveryWake.WaitAsync(TimeSpan.FromSeconds(1), ct);
                continue;
            }
            if (SessionReady)
            {
                await recoveryWake.WaitAsync(ct);
                continue;
            }

            var peer = connection.Peer;
            attached = false; attachedPeer = null; sessionConnecting = false; recoveryError = null;
            send.IsEnabled = composer.IsEnabled = false;
            UpdateRunStatus();
            try
            {
                try { await OperationGroups.NegotiateAsync(peer, ct); } catch (IOException) { }
                var available = Json.Read<AgentInfo[]>(await peer.CallAsync("agents", null, ct)).FirstOrDefault(a => a.Available)
                    ?? throw new IOException("远端尚无可用 Agent，等待接入端恢复。");
                var restored = Json.Read<SessionView>(await SessionHistory.ReadAsync(peer, "attach", new { id = sessionId }, ct));
                ct.ThrowIfCancellationRequested();
                if (!connection.IsConnected || connection.Peer != peer) continue;
                agent = available; attachedPeer = peer; attached = true; attempt = 0;
                Refresh(restored);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                recoveryError = ex.Message;
                UpdateRunStatus();
                await recoveryWake.WaitAsync(TimeSpan.FromSeconds(Math.Min(15, 1 << Math.Min(attempt++, 4))), ct);
            }
        }
    }

    private void Refresh(SessionView update)
    {
        if (update.Id != sessionId || closed) return;
        view = update;
        if (!string.IsNullOrWhiteSpace(update.Title)) { SetSessionTitle(update.Title); SetStatus(update.Title); }
        UpdateRunStatus();
        RefreshSendAction();
        RenderLatestMessages();
    }

    private void OnSubmissionStateChanged((string Device, string Session) key)
    {
        if (closed || key != submissionKey) return;
        RefreshSendAction();
        UpdateRunStatus();
    }

    private void RefreshSendAction()
    {
        bool uncertain = submissionRecovery.IsUncertain(submissionKey);
        bool queued = view?.CanSteer == false;
        bool ready = SessionReady && !sending;
        composer.IsEnabled = ready && view?.CanSend == true;
        send.IsEnabled = ready && (uncertain || view?.CanSend == true);
        send.Content = uncertain ? "核对" : queued ? "排队" : "↑";
        send.FontSize = uncertain || queued ? 11 : 20;
        AutomationProperties.SetName(send, uncertain ? "核对上次发送" : queued ? "排队发送消息" : "发送消息");
    }

    private void RenderLatestMessages()
    {
        if (collapsed || dragging || !followLatest || view is null) return;
        bool imagesChanged = UpdateChatImageContext(view);
        int index = 0;
        foreach (var message in view.Messages.Where(HistoryPaging.IsConversation))
        {
            bool isUser = ChatPresentation.Kind(message) == "user";
            if (index == context.Children.Count)
                context.Children.Add(new TranscriptRow(isUser, CreateChatImageVisual));
            else if (((TranscriptRow)context.Children[index]).IsUser != isUser)
            {
                context.Children.RemoveAt(index);
                context.Children.Insert(index, new TranscriptRow(isUser, CreateChatImageVisual));
            }
            var row = (TranscriptRow)context.Children[index++];
            string markdown = ChatPresentation.Display(message).Text;
            if (row.Body.Markdown != markdown) row.Body.Markdown = markdown;
            else if (imagesChanged && MarkdownView.AttachmentReferences(markdown).Any()) row.Body.RefreshAttachmentVisuals();
        }
        if (index < context.Children.Count) context.Children.RemoveRange(index, context.Children.Count - index);
        ScrollToLatestAfterLayout();
    }

    private static bool IsHyperlinkSource(object source)
    {
        for (var item = source as FrameworkContentElement; item is not null; item = item.Parent as FrameworkContentElement)
            if (item is Hyperlink) return true;
        return false;
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
        var connection = remote;
        if (sending || connection is null || !SessionReady) return;
        if (submissionRecovery.PromptIfUncertain(this, submissionKey) || view?.CanSend != true) return;
        string text = composer.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        sending = true; send.IsEnabled = false; composer.IsEnabled = false;
        UpdateRunStatus();
        try
        {
            var peer = connection.Peer;
            Task submission = agent?.CanFiles == true
                ? new FileTransferClient(peer).SendAsync(sessionId, text, [], responseTimeout: TimeSpan.FromMinutes(3))
                : peer.CallAsync("send", new { text, delivery = "auto" }, responseTimeout: TimeSpan.FromMinutes(3));
            if (await Task.WhenAny(submission, Task.Delay(TimeSpan.FromSeconds(12))) != submission)
                SetStatus("远端确认较慢，仍在等待原请求；请勿重复发送。");
            await submission;
            if (composer.Text == text) { SetDraft(""); writeDraft(""); }
            SetStatus(view?.CanSteer == false ? "消息已进入远端队列。" : "消息已由远端接收，结果以会话状态为准。");
        }
        catch (SubmissionUncertainException ex)
        {
            submissionRecovery.MarkUncertain(submissionKey);
            SetStatus(ex.Message);
        }
        catch (Exception ex) { SetStatus("发送失败：" + ex.Message); }
        finally { sending = false; RefreshSendAction(); UpdateRunStatus(); }
    }

    private void UpdateRunStatus()
    {
        var indicator = sending ? new AgentIndicator("提交中", "#D97706")
            : !(remote?.IsNetworkConnected ?? sharedRemote?.IsConnected ?? ownedRemote?.IsConnected ?? false) ? new AgentIndicator(sessionConnecting ? "网络连接中" : "网络已断开", "#D97706")
            : !SessionReady ? new AgentIndicator(recoveryError is null ? "会话加载中" : "会话待重试", "#D97706")
            : submissionRecovery.IsUncertain(submissionKey) ? new AgentIndicator("发送待核对", "#D97706")
            : AgentIndicator.From(true, false, view);
        statusLabel.Text = indicator.Label;
        var color = (Brush)new BrushConverter().ConvertFromString(indicator.Color)!;
        statusDot.Fill = color;
        ((TextBlock)ball.Child).Foreground = color;
        string detail = recoveryError is { Length: > 0 } error ? " · " + error
            : view?.StatusDetail is { Length: > 0 } description ? " · " + description : "";
        string hint = indicator.Label + detail;
        statusBadge.ToolTip = hint;
        if (recoveryError is not null) SetStatus(hint);
        AutomationProperties.SetName(statusBadge, "会话状态：" + indicator.Label);
    }

    private void EndDrag()
    {
        dragging = false;
        if (scroll.IsMouseCaptured) scroll.ReleaseMouseCapture();
        if (followLatest) RenderLatestMessages();
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
}
