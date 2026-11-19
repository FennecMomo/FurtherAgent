using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FurtherAgent.Core;
using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

public sealed partial class MainPage : ContentPage
{
    private DeviceConnectionPool<MobileSession> connections => ConnectionRuntime.Connections;
    private readonly Dictionary<string, string> connectionNames = [];
    private readonly Dictionary<string, string> legacyDraftScopes = [];
    private MobileSession session = NewSession();
    public MobileSession CurrentSession => session;
    private static MobileSession NewSession() => ConnectionRuntime.NewSession();
    private string? activeConnection;
    private bool connectingDevice;
    private readonly WebView transcript = new();
    private readonly Label title = Ui.Text("FurtherAgent", 17, true);
    private readonly Label status = Ui.Text("", 12);
    private readonly Label attachments = Ui.Text("", 11);
    private readonly Label connectionLabel = Ui.Text("未连接", 11, true);
    private readonly Microsoft.Maui.Controls.Shapes.Ellipse connectionDot = new() { WidthRequest = 7, HeightRequest = 7, Fill = new SolidColorBrush(Color.FromArgb("#DC2626")), VerticalOptions = LayoutOptions.Center };
    private readonly Grid emptyChat = new() { ZIndex = 1 };
    private readonly Label emptyTitle = Ui.Text("", 21, true);
    private readonly Label emptyHint = Ui.Text("", 14);
    private readonly Button emptyAction;
    private string? backgroundServiceError;
    private readonly Editor composer = new() { Placeholder = "连接设备后，在这里发送消息…", AutoSize = EditorAutoSizeOption.TextChanges,
        MinimumHeightRequest = 54, MaximumHeightRequest = 140, FontSize = 15, BackgroundColor = Colors.Transparent };
    private readonly Button send, stop, files, models, prompts, conversations, voice;
    private readonly Dictionary<string, List<RemoteFile>> uploads = [];
    private string scope = "", draftKey = "", lastSession = "", lastHtml = "";
    private bool loadingDraft, webReady, pendingRender, preview, interactionBusy, recognizingSpeech, closed;
    private readonly IDispatcherTimer timer;
    private CancellationTokenSource? speechCancel, speechStop;
    private bool recordingSpeech, suspended;
    private bool rendering, prependHistory, loadingOlder;
    private CancellationTokenSource? downloadCancel;
    private string? downloadStatus;
    private bool voiceHeld, voiceDiscard, restoringConnections;
    private DateTime nextRestore = DateTime.MinValue;

    public MainPage()
    {
        ConnectionRuntime.Changed += OnChanged;
        Title = "FurtherAgent"; Ui.Theme(this);
        Ui.TextTheme(title); Ui.TextTheme(status); Ui.TextTheme(attachments);
        Ui.TextTheme(emptyTitle); Ui.TextTheme(emptyHint);
        composer.SetAppThemeColor(Editor.TextColorProperty, Ui.Green, Colors.White);
        composer.SetAppThemeColor(Editor.PlaceholderColorProperty, Color.FromArgb("#66736D"), Color.FromArgb("#ACBCB3"));
        conversations = Ui.Button("会话", OpenSessionsAsync);
        NavigationPage.SetHasNavigationBar(this, false);
        title.MaxLines = 1; title.LineBreakMode = LineBreakMode.TailTruncation;
        Ui.TextTheme(connectionLabel);
        connectionLabel.VerticalTextAlignment = TextAlignment.Center;
        connectionLabel.MaxLines = 1;
        connectionLabel.LineBreakMode = LineBreakMode.TailTruncation;
        deviceButton = TextAction("连接设备 ▾", SwitchDeviceAsync);
        deviceButton.FontSize = 12; deviceButton.Padding = new Thickness(0);
        deviceButton.HorizontalOptions = LayoutOptions.Fill;
        var connectionState = new HorizontalStackLayout { Spacing = 4, Children = { connectionDot, connectionLabel } };
        connectionState.MaximumWidthRequest = 86;
        var connection = new Grid { ColumnSpacing = 6, ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        connection.Add(deviceButton); connection.Add(connectionState, 1);
        var heading = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, Children = { title, connection } };
        var header = new Grid { Padding = new Thickness(10, 6, 10, 2), ColumnSpacing = 7,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        conversations.BackgroundColor = Colors.Transparent;
        conversations.SetAppTheme(Button.ImageSourceProperty, ImageSource.FromFile("ic_menu.png"), ImageSource.FromFile("ic_menu_white.png"));
        var newChat = Ui.Button("新建会话", NewConversationAsync); newChat.BackgroundColor = Colors.Transparent;
        newChat.SetAppTheme(Button.ImageSourceProperty, ImageSource.FromFile("ic_square_pen.png"), ImageSource.FromFile("ic_square_pen_white.png"));
        header.Add(conversations); header.Add(heading, 1); header.Add(newChat, 2);
        AlignStart(deviceButton);
        transcript.Source = new HtmlWebViewSource { Html = Transcript.Shell };
        transcript.Navigated += (_, _) => { webReady = true; QueueRender(); };
        transcript.Navigating += OnNavigating;
        send = Ui.Button("发送 ↑", SendAsync, true);
        stop = Ui.Button("停止", () => Act(session.StopAsync));
        files = Ui.Button("附件", FileMenuAsync);
        models = Ui.Button("模型", SettingsAsync);
        prompts = Ui.Button("待确认", RespondAsync);
        voice = Ui.Button("语音", () => Task.CompletedTask);
        new HoldToTalk(voice, () =>
        {
            if (recognizingSpeech || interactionBusy || !session.Connected) return;
            voiceHeld = true; voiceDiscard = false;
            _ = VoiceInputAsync();
        }, discard =>
        {
            voiceDiscard = discard;
            status.Text = discard ? "松开放弃本次语音" : "松手停止，文字保留 · 下拉放弃";
        }, discard =>
        {
            voiceHeld = false; voiceDiscard = discard;
            if (discard) speechCancel?.Cancel(); else speechStop?.Cancel();
        }).Attach();
        voice.AutomationId = "voice-input";
        SemanticProperties.SetDescription(voice, "按住说话，松手停止，文字保留，下拉放弃");
        StyleModelButton(models);
        prompts.BackgroundColor = Color.FromArgb("#FFF3E6"); prompts.WidthRequest = -1;
        prompts.Padding = new Thickness(12, 0); prompts.FontSize = 12;
        files.BackgroundColor = voice.BackgroundColor = Colors.Transparent;
        send.CornerRadius = stop.CornerRadius = 22;
        stop.BackgroundColor = Ui.Green; Ui.SetAction(stop, "停止", true);
        var primaryAction = new Grid(); primaryAction.Add(stop); primaryAction.Add(send);
        var controls = new Grid { ColumnSpacing = 4, ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)] };
        controls.Add(files); controls.Add(models, 1); controls.Add(voice, 2); controls.Add(primaryAction, 3);
        var input = new VerticalStackLayout { Spacing = 2, Children = { composer, controls } };
        var panel = new Border { Padding = new Thickness(10, 6), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 }, Content = input };
        panel.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F5F6F7"), Color.FromArgb("#182820"));
        prompts.HorizontalOptions = LayoutOptions.End;
        var footer = new VerticalStackLayout { Padding = new Thickness(12, 4, 12, 8), Spacing = 4,
            ZIndex = 2,
            Children = { status, attachments, prompts, panel } };
        // Cover the entire keyboard-following input region, including the rounded
        // panel corners and status rows; WebView can retain an old Android surface.
        footer.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#101C17"));
        composer.SetAppThemeColor(Editor.BackgroundColorProperty, Color.FromArgb("#F5F6F7"), Color.FromArgb("#182820"));
        var transcriptHost = new Grid { IsClippedToBounds = true, ZIndex = 0 };
        transcriptHost.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#101C17"));
        transcriptHost.Add(transcript);
        emptyAction = TextAction("", () => session.Connected ? OpenSessionsAsync() : OpenConnectionAsync());
        emptyAction.Padding = new Thickness(18, 0);
        emptyAction.BackgroundColor = Ui.Green; emptyAction.TextColor = Colors.White;
        emptyTitle.HorizontalTextAlignment = emptyHint.HorizontalTextAlignment = TextAlignment.Center;
        emptyHint.MaximumWidthRequest = 310;
        emptyChat.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#101C17"));
        emptyChat.Add(new VerticalStackLayout { Padding = 24, Spacing = 12, HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center, Children = { emptyTitle, emptyHint, emptyAction } });
        transcriptHost.Add(emptyChat);
        status.IsVisible = false;
        status.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(Label.Text)) UpdateStatusVisibility(); };
        var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        layout.Add(header); layout.Add(transcriptHost, 0, 1); layout.Add(footer, 0, 2);
        Content = CreateDrawerHost(layout);
        composer.TextChanged += (_, _) => { SaveDraft(); RefreshButtons(); };
        timer = Dispatcher.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.Tick += async (_, _) =>
        {
            if (!suspended && pendingRender && webReady && !rendering) { pendingRender = false; await RenderAsync(); }
            if (!suspended && DateTime.UtcNow >= nextRestore) await RestoreSavedConnectionsAsync();
            if (!suspended) { UpdateMailboxBadge(); await OpenPendingMailboxAsync(); await OpenPendingCompletionAsync(); }
        };
        Loaded += async (_, _) => await RestoreSavedConnectionsAsync();
        timer.Start(); RefreshButtons(); UpdateEmptyChat();
    }

    private async void OnNavigating(object? sender, WebNavigatingEventArgs args)
    {
        if (args.Url is "about:blank" || args.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        args.Cancel = true;
        try
        {
            if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme == "fa-history")
            {
                if (uri.Host == "older" && !loadingOlder)
                {
                    var owner = session;
                    loadingOlder = true;
                    try
                    {
                        if (owner.Busy || interactionBusy) return;
                        await Act(owner.LoadOlderAsync);
                        if (session != owner) return;
                        prependHistory = owner.ReadingOlder;
                        if (!prependHistory) lastSession = "";
                        QueueRender();
                    }
                    finally
                    {
                        loadingOlder = false;
                        if (webReady) await transcript.EvaluateJavaScriptAsync("historyLoadDone()");
                    }
                }
                else if (uri.Host == "latest" && !loadingOlder) { session.ShowLatest(); lastSession = ""; QueueRender(); }
            }
            else if (uri.Scheme == "fa-copy" && int.TryParse(uri.Host, out int index))
            {
                var view = preview ? Transcript.Preview : session.View;
                if (view is not null && index >= 0 && index < view.Messages.Count)
                { await Clipboard.SetTextAsync(ChatPresentation.Display(view.Messages[index]).Text); status.Text = "已复制消息"; }
            }
            else if (uri.Scheme == "attachment") await Act(async () =>
            {
                string name = Uri.UnescapeDataString(args.Url["attachment:".Length..]);
                var output = await session.ListOutputFilesAsync();
                var file = RemoteFileReferences.Resolve(output, name)
                    ?? throw new IOException("附件尚未生成或已移除，请稍后重试。");
                await DownloadOutputAsync(file);
            });
            else if (uri.Scheme is "https" or "http") await Browser.OpenAsync(uri, BrowserLaunchMode.External);
        }
        catch (Exception ex) { await ErrorAsync(ex); }
    }

    private void OnChanged()
    {
        if (closed) return;
        var indicator = session.Indicator;
        connectionLabel.Text = session.View?.Prompts.Any(p => p.Kind is "item/tool/requestUserInput" or CodexQuestions.AsyncKind) == true ? "等待回答" : indicator.Label;
        connectionDot.Fill = new SolidColorBrush(Color.FromArgb(indicator.Color));
        if (session.Connected) preview = false;
        string nextKey = session.View is { } view ? $"draft.{scope}.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(view.Id)))}" : "";
        if (nextKey != draftKey)
        {
            SaveDraft(); loadingDraft = true; draftKey = nextKey;
            if (nextKey.Length > 0 && !Preferences.ContainsKey(nextKey) && legacyDraftScopes.TryGetValue(scope, out var legacy))
            {
                string oldKey = nextKey.Replace("draft." + scope + ".", "draft." + legacy + ".", StringComparison.Ordinal);
                if (oldKey != nextKey && Preferences.ContainsKey(oldKey))
                { Preferences.Set(nextKey, Preferences.Get(oldKey, "")); Preferences.Remove(oldKey); }
            }
            composer.Text = nextKey.Length > 0 ? Preferences.Get(nextKey, "") : ""; loadingDraft = false;
        }
        title.Text = session.View?.Title ?? (preview ? "离线预览" : session.Connected ? "选择会话" : "FurtherAgent");
        if (!recognizingSpeech)
            status.Text = downloadStatus ?? (session.Status + (backgroundServiceError is null ? "" : " · " + backgroundServiceError));
        deviceButton.Text = session.HasConnection ? $"{connectionNames.GetValueOrDefault(activeConnection ?? "", session.DeviceName)} ▾" : "连接设备 ▾";
        if (drawerLayer?.IsVisible == true) RenderDrawer();
        RefreshButtons(); UpdateEmptyChat(); QueueRender();
    }
    private List<RemoteFile> PendingFiles => uploads.GetValueOrDefault(draftKey) ?? [];
    private void RefreshButtons()
    {
        bool ready = session.Connected && !session.Busy && !interactionBusy && session.View is not null && !preview;
        composer.IsEnabled = !session.Busy && !interactionBusy && session.View is not null && !preview;
        voice.IsEnabled = recognizingSpeech || composer.IsEnabled;
        Ui.SetAction(voice, recognizingSpeech ? (recordingSpeech ? "结束" : "取消") : "语音");
        SemanticProperties.SetDescription(voice, "按住说话，松手停止，文字保留，下拉放弃");
        composer.Placeholder = session.View is not null && !preview ? "继续这个会话…" : "连接设备后，在这里发送消息…";
        send.IsEnabled = ready && session.View!.CanSend && (!string.IsNullOrWhiteSpace(composer.Text) || PendingFiles.Count > 0);
        Ui.SetAction(send, session.SendUncertain ? "核对" : session.Busy ? "等待…" : "发送 ↑", true);
        stop.IsEnabled = ready && session.View!.CanStop == true;
        files.IsEnabled = downloadCancel is not null || ready && session.Agent?.CanFiles == true;
        string fileAction = downloadCancel is null ? "附件" : "取消下载";
        Ui.SetAction(files, fileAction);
        files.SetAppTheme(Button.ImageSourceProperty, Ui.Icon(fileAction), Ui.Icon(fileAction, true));
        voice.SetAppTheme(Button.ImageSourceProperty, Ui.Icon(recognizingSpeech ? "停止" : "语音"), Ui.Icon(recognizingSpeech ? "停止" : "语音", true));
        models.IsEnabled = ready && session.Agent?.CanConfigure == true;
        prompts.IsEnabled = ready && session.View!.Prompts.Count > 0;
        bool hasQuestion = session.View?.Prompts.Any(p => p.Kind is "item/tool/requestUserInput" or CodexQuestions.AsyncKind) == true;
        Ui.SetAction(prompts, session.View?.Prompts.Count > 0 ? $"{(hasQuestion ? "回答问题" : "确认")} {session.View.Prompts.Count}" : "待确认", hasQuestion);
        conversations.IsEnabled = !session.Busy && !interactionBusy;
        bool hasDraft = !string.IsNullOrWhiteSpace(composer.Text) || PendingFiles.Count > 0;
        stop.IsVisible = session.View?.CanStop == true && !session.SendUncertain && !(hasDraft && session.View.CanSend);
        send.IsVisible = !stop.IsVisible;
        prompts.IsVisible = session.View?.Prompts.Count > 0;
        prompts.ImageSource = null;
        prompts.Text = $"{(hasQuestion ? "待回答" : "待确认")} {session.View?.Prompts.Count}  ›";
        models.ImageSource = null;
        string model = session.Settings?.Model ?? session.View?.Model ?? "模型";
        string? effort = session.Settings?.CanChangeReasoning == true ? session.Settings.ReasoningEffort : null;
        models.Text = string.IsNullOrWhiteSpace(effort) ? $"{model} ▾" : $"{model} · {effort} ▾";
        SemanticProperties.SetDescription(models, $"模型与思考强度，{models.Text}");
        deviceButton.IsEnabled = !interactionBusy && !session.Busy && !connectingDevice;
        UpdateStatusVisibility();
        attachments.Text = PendingFiles.Count == 0 ? "" : "待发送：" + string.Join("、", PendingFiles.Select(f => f.Name));
        attachments.IsVisible = PendingFiles.Count > 0;
    }
    private void UpdateEmptyChat()
    {
        emptyChat.IsVisible = !preview && session.View is null;
        emptyTitle.Text = session.Connected ? "选择一个会话" : "连接远端设备";
        emptyHint.Text = session.Connected ? "从会话列表继续之前的工作，或创建一个新会话。" : "连接已保存的设备，或输入接入端显示的授权码。";
        emptyAction.Text = session.Connected ? "查看会话" : "连接设备";
    }
    private void QueueRender() => pendingRender = true;
    private void StartConnectionService()
    {
        try { ConnectionService.Start(); backgroundServiceError = null; }
        catch { backgroundServiceError = "后台连接服务未启动，返回应用后会检查并恢复连接"; }
    }
    private async Task RenderAsync()
    {
        rendering = true;
        try
        {
            var renderingSession = session; bool renderingPreview = preview;
            var view = preview ? Transcript.Preview : session.View ?? new SessionView("empty", "", "", "idle", null, [], []);
            bool hasOlder = session.HasOlder, older = session.ReadingOlder;
            string html = await Task.Run(() => Transcript.Render(view, hasOlder, older));
            if (!ReferenceEquals(renderingSession, session) || renderingPreview != preview || !preview && session.View is not null && !ReferenceEquals(view, session.View)) { pendingRender = true; return; }
            if (lastHtml == html && lastSession == view.Id) { prependHistory = false; return; }
            bool reset = lastSession != view.Id;
            bool prepend = prependHistory; prependHistory = false;
            await transcript.EvaluateJavaScriptAsync($"renderTranscript({JsonSerializer.Serialize(html)}, {(reset ? "true" : "false")}, {(prepend ? "true" : "false")});");
            lastHtml = html; lastSession = view.Id;
            if (!preview) _ = LoadImagePreviewsAsync(renderingSession, view);
        }
        catch (Exception ex) { status.Text = "记录显示失败，可重新选择会话：" + ex.Message; }
        finally { rendering = false; }
    }
    private readonly Dictionary<string, string> imagePreviews = [];
    private readonly HashSet<string> loadingImagePreviews = [];
    private readonly SemaphoreSlim imagePreviewSlots = new(2);
    private async Task LoadImagePreviewsAsync(MobileSession owner, SessionView view)
    {
        if (!ImageThumbnail.CanRequest(owner.Agent)) return;
        string connection = activeConnection ?? "";
        foreach (var file in (view.OutputFiles ?? []).Where(f => ImageThumbnail.Supported(f.Name)))
        {
            string key = DownloadedAttachments.Key(connection, view.Id, file);
            if (!loadingImagePreviews.Add(key)) continue;
            await imagePreviewSlots.WaitAsync();
            try
            {
                if (session != owner || session.View?.Id != view.Id) return;
                if (!imagePreviews.TryGetValue(key, out string? data))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var thumbnail = await owner.ThumbnailAsync(view.Id, file, timeout.Token);
                    data = "data:image/jpeg;base64," + thumbnail.Data;
                    if (imagePreviews.Count >= 64) imagePreviews.Remove(imagePreviews.Keys.First());
                    imagePreviews[key] = data;
                }
                if (session == owner && session.View?.Id == view.Id && webReady)
                    await transcript.EvaluateJavaScriptAsync($"setAttachmentImage({JsonSerializer.Serialize(file.Id)},{JsonSerializer.Serialize(file.Version)},{JsonSerializer.Serialize(data)});");
            }
            catch { /* Leave the attachment clickable if the thumbnail is unavailable. */ }
            finally { imagePreviewSlots.Release(); loadingImagePreviews.Remove(key); }
        }
    }

    public void SaveDraft()
    {
        if (!loadingDraft && draftKey.Length > 0) Preferences.Set(draftKey, composer.Text ?? "");
        if (activeConnection is { } id && session.View is { } view) Preferences.Set("last-session." + id, view.Id);
    }
    public void Detach()
    {
        Suspend();
        closed = true;
        ConnectionRuntime.Changed -= OnChanged;
        downloadCancel?.Cancel();
        SaveDraft(); timer.Stop();
        webReady = false; lastHtml = lastSession = "";
    }
    private async Task ErrorAsync(Exception ex)
    {
        string message = ex is TimeoutException ? "等待确认超时。请先检查远端历史，不要重复发送。" : ex.Message;
        // Never echo a pairing code that a transport exception might contain.
        try { foreach (var p in (await SavedConnections.LoadAsync()).Connections) message = message.Replace(p.Code, "[授权码]", StringComparison.Ordinal); } catch { }
        await DisplayAlertAsync("操作未完成", message, "知道了");
    }
    private async Task Act(Func<Task> action)
    {
        if (interactionBusy) return;
        interactionBusy = true; RefreshButtons();
        try { await action(); } catch (Exception ex) { await ErrorAsync(ex); }
        finally { interactionBusy = false; RefreshButtons(); }
    }
    private Task OpenConnectionAsync() => interactionBusy || session.Busy || connectingDevice ? Task.CompletedTask : Navigation.PushAsync(new ConnectionPage(this));
    private Task OpenSessionsAsync() { OpenDrawer(); return Task.CompletedTask; }
    public async Task ConnectAsync(string code, string? name = null, string? replaceId = null, bool resumeOnLaunch = true)
    {
        if (connectingDevice || interactionBusy || session.Busy) throw new IOException("请等待当前操作结束再切换设备。");
        CompletionNotifications.CancelPending();
        SaveDraft(); connectingDevice = true; StartConnectionService();
        try
        {
            var (profile, target) = await ConnectionRuntime.ConnectAsync(code, name, replaceId, resumeOnLaunch);
            if (closed) throw new OperationCanceledException("应用正在退出。");
            connectionNames[profile.Id] = profile.Name;
            legacyDraftScopes[profile.Id] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Server.TrimEnd('/') + "\n" + profile.Code.Trim())));
            ActivateConnection(profile.Id, target);
            await CompletionNotifications.RequestPermissionOnceAsync();
        }
        finally { connectingDevice = false; OnChanged(); }
    }
    private void ActivateConnection(string id, MobileSession target)
    {
        if (activeConnection == id && ReferenceEquals(session, target))
        {
            OnChanged();
            return;
        }
        SaveDraft(); session = target; activeConnection = id; scope = id;
        preview = false; lastHtml = lastSession = ""; prependHistory = false; OnChanged();
    }
    public async Task DisconnectCurrentAsync()
    {
        if (session.Busy || interactionBusy || connectingDevice) throw new IOException("请等待当前操作结束再断开。");
        CompletionNotifications.CancelPending();
        SaveDraft();
        if (activeConnection is { } id)
        {
            await ConnectionRuntime.DisconnectAsync(id);
        }
        var next = connections.Entries.LastOrDefault();
        if (next.Value is not null) ActivateConnection(next.Key, next.Value);
        else { session = NewSession(); activeConnection = null; scope = ""; OnChanged(); }
    }
    public async Task SelectAsync(string id)
    {
        if (!openingCompletion) CompletionNotifications.CancelPending();
        SaveDraft(); await session.SelectAsync(id); OnChanged();
    }
    public Task PreviewAsync()
    {
        CompletionNotifications.CancelPending();
        SaveDraft(); session = NewSession(); activeConnection = null; scope = "";
        preview = true; OnChanged();
        return Task.CompletedTask;
    }
    public async Task ResumeAsync()
    {
        if (closed) { closed = false; ConnectionRuntime.Changed += OnChanged; timer.Start(); }
        suspended = false;
        SaveDraft();
        if (connections.Entries.Count > 0) StartConnectionService();
        await ConnectionRuntime.CheckConnectionsAsync();
        await RestoreSavedConnectionsAsync();
        if (connections.Entries.Count > 0) await CompletionNotifications.RequestPermissionOnceAsync();
        await OpenPendingCompletionAsync();
    }
    private async Task RestoreSavedConnectionsAsync()
    {
        if (restoringConnections || closed || suspended || interactionBusy || connectingDevice || session.Busy) return;
        restoringConnections = true; nextRestore = DateTime.UtcNow.AddSeconds(20);
        var selectionBeforeRestore = session;
        try
        {
            await StartConnectionServiceIfSavedAsync();
            await ConnectionRuntime.RestoreAsync();
            var saved = await SavedConnections.LoadAsync();
            if (closed || suspended || interactionBusy || connectingDevice || session.Busy || !ReferenceEquals(session, selectionBeforeRestore)) return;
            var entries = connections.Entries;
            (string Id, MobileSession Session)? preferred = null;
            foreach (var profile in saved.Connections.OrderBy(p => p.Id == saved.SelectedId ? 1 : 0))
            {
                string id = DeviceConnectionPool<MobileSession>.Identity(profile.Server, profile.Code);
                if (!entries.TryGetValue(id, out var target)) continue;
                connectionNames[id] = profile.Name;
                legacyDraftScopes[id] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Server.TrimEnd('/') + "\n" + profile.Code.Trim())));
                preferred = (id, target);
            }
            if (activeConnection is null && !preview && preferred is { } fallback) ActivateConnection(fallback.Id, fallback.Session);
            else OnChanged();
            if (entries.Count > 0) await CompletionNotifications.RequestPermissionOnceAsync();
        }
        catch { status.Text = "无法读取保存的连接，请在连接页检查"; }
        finally { restoringConnections = false; nextRestore = DateTime.UtcNow.AddSeconds(20); }
    }
    private async Task StartConnectionServiceIfSavedAsync()
    {
        try
        {
            var saved = await SavedConnections.LoadAsync();
            if (!closed && !suspended && saved.Connections.Any(p => Preferences.Get("resume-connection." +
                    DeviceConnectionPool<MobileSession>.Identity(p.Server, p.Code), true))) StartConnectionService();
        }
        catch { /* RestoreAsync reports a storage failure in the page status. */ }
    }
    private async Task SendAsync()
    {
        if (session.SendUncertain)
        {
            if (await DisplayAlertAsync("先核对远端会话", "上次提交可能已经执行。请先刷新或在电脑核对。确认后仅恢复编辑，不会自动重发。", "我已核对", "返回"))
                session.AcknowledgeUncertain();
            return;
        }
        string text = composer.Text ?? ""; string key = draftKey;
        var origin = session; var ids = PendingFiles.Select(f => f.Id).ToArray();
        await Act(async () =>
        {
            await origin.SendAsync(text, ids);
            if (draftKey == key && ReferenceEquals(origin, session) && composer.Text == text) composer.Text = "";
            if (Preferences.Get(key, "") == text) Preferences.Remove(key);
            if (uploads.TryGetValue(key, out var files)) { files.RemoveAll(f => ids.Contains(f.Id)); if (files.Count == 0) uploads.Remove(key); }
        });
    }
    public void Suspend()
    {
        suspended = true;
        SaveDraft();
        // Do not keep recording after Home, screen lock, or switching applications.
        voiceHeld = false; voiceDiscard = true;
        speechCancel?.Cancel();
    }
    private async Task VoiceInputAsync()
    {
        if (recognizingSpeech)
        {
            if (recordingSpeech) { speechStop?.Cancel(); status.Text = "正在收尾，保留已识别文字…"; }
            else speechCancel?.Cancel();
            return;
        }
        if (interactionBusy || session.Busy || preview || draftKey.Length == 0 || closed) return;
        await Act(async () =>
        {
            string key = draftKey, original = composer.Text ?? "";
            SaveDraft();
            string apiKey = await SecureStorage.GetAsync(SpeechSettingsPage.KeyName) ?? "";
            if (string.IsNullOrWhiteSpace(apiKey))
            { await Navigation.PushAsync(new SpeechSettingsPage()); return; }
            if (await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
                throw new InvalidOperationException("需要麦克风权限才能语音输入。可在系统应用设置中允许；原草稿已保留。");
            if (closed || suspended || !voiceHeld) return;
            using var cancel = new CancellationTokenSource();
            using var stopCapture = new CancellationTokenSource();
            speechCancel = cancel; speechStop = stopCapture; recognizingSpeech = true;
            RefreshButtons(); status.Text = "正在连接语音服务…";
            int generation = 0;
            bool hasText = false;
            try
            {
                var endpoint = FunAsrClient.Endpoint(Preferences.Get(SpeechSettingsPage.WorkspaceName, ""));
                await Task.Run(() => new FunAsrClient().RunAsync(apiKey, endpoint,
                    ct => MicrophoneAudio.Capture(() => MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (!recognizingSpeech || closed) return;
                        recordingSpeech = true; RefreshButtons(); status.Text = voiceDiscard ? "松开放弃本次语音" : "正在听 · 松手停止，文字保留 · 下拉放弃";
                    }), stopCapture.Token, ct),
                    text => MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        if (closed || voiceDiscard || generation != 0) return;
                        hasText = !string.IsNullOrWhiteSpace(text);
                        string merged = SpeechTranscript.Append(original, text);
                        Preferences.Set(key, merged);
                        if (draftKey == key) composer.Text = merged;
                    }).GetAwaiter().GetResult(), cancel.Token));
                status.Text = hasText ? "语音已停止，文字保留为草稿，请手动发送" : "没有识别到文字，原草稿已保留";
            }
            catch (OperationCanceledException) { status.Text = "语音输入已停止，已识别的文字已保留"; }
            catch (TimeoutException)
            {
                status.Text = "语音识别超时，草稿已保留";
                throw new InvalidOperationException("语音服务响应超时，已识别的文字已保留，请检查网络后重试。");
            }
            catch { status.Text = "语音识别未完成，草稿已保留"; throw; }
            finally
            {
                generation++;
                if (voiceDiscard)
                {
                    Preferences.Set(key, original);
                    if (draftKey == key) composer.Text = original;
                    status.Text = "已放弃本次语音，原草稿保留";
                }
                speechCancel = null; speechStop = null; recognizingSpeech = recordingSpeech = false;
            }
        });
    }
    private async Task FileMenuAsync()
    {
        if (downloadCancel is not null) { downloadCancel.Cancel(); return; }
        string choice = await DisplayActionSheetAsync("会话附件", "取消", null, "选择并上传文件", "查看远端输出", "清除待发送附件");
        if (choice == "清除待发送附件") { uploads.Remove(draftKey); RefreshButtons(); return; }
        if (choice == "选择并上传文件") await Act(async () =>
        {
            var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "选择资料" });
            if (picked is null) return;
            string dir = Path.Combine(FileSystem.CacheDirectory, "upload-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string name = Path.GetFileName(picked.FileName.Replace('\\', '/'));
            string local = Path.Combine(dir, string.IsNullOrWhiteSpace(name) ? "attachment" : name);
            try
            {
                await using (var source = await picked.OpenReadAsync())
                await using (var target = File.Create(local))
                {
                    byte[] buffer = new byte[128 * 1024]; long total = 0; int count;
                    while ((count = await source.ReadAsync(buffer)) > 0)
                    { total += count; if (total > FileTransferStore.MaxFileBytes) throw new IOException("文件超过 100 MiB 上限。"); await target.WriteAsync(buffer.AsMemory(0, count)); }
                }
                var uploaded = await session.UploadAsync(local, Progress("上传"));
                if (!uploads.TryGetValue(draftKey, out var list)) uploads[draftKey] = list = [];
                list.Add(uploaded); status.Text = "文件已上传，发送消息后交给 Agent";
            }
            finally { if (File.Exists(local)) File.Delete(local); Directory.Delete(dir); }
        });
        if (choice == "查看远端输出") await Act(async () =>
        {
            var output = await session.ListOutputFilesAsync();
            if (output.Count == 0) { await DisplayAlertAsync("会话产物", "目前没有输出文件，可让 Agent 把结果放入会话输出目录。", "知道了"); return; }
            string[] options = output.Select((f, i) => $"{i + 1}. {f.Name} · {f.Size / 1024d:N0} KB").ToArray();
            string selected = await DisplayActionSheetAsync("下载后可保存或分享", "取消", null, options);
            int index = Array.IndexOf(options, selected); if (index < 0) return;
            await DownloadOutputAsync(output[index]);
        });
    }
    private readonly DownloadedAttachments downloadedAttachments = new(Path.Combine(FileSystem.AppDataDirectory, "downloads"));
    private async Task DownloadOutputAsync(RemoteFile file)
    {
        string key = DownloadedAttachments.Key(activeConnection ?? "", session.View?.Id ?? "", file);
        if (downloadedAttachments.Find(key) is { } existing)
        {
            await OpenDownloadedFileAsync(existing);
            return;
        }
        string path = downloadedAttachments.NewPath(file.Name);
        string name = Path.GetFileName(path);
        using var cancel = new CancellationTokenSource();
        downloadCancel = cancel;
        downloadStatus = status.Text = "正在读取文件信息 · 点击附件按钮可取消下载";
        RefreshButtons();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await session.DownloadAsync(file, path, new Progress<FileProgress>(p =>
            {
                if (downloadCancel != cancel) return;
                double percent = p.Total == 0 ? 100 : p.Completed * 100d / p.Total;
                downloadStatus = status.Text = $"下载 {percent:N1}% · {p.Completed / 1024d:N0}/{p.Total / 1024d:N0} KB · {p.Completed / 1024d / Math.Max(1, elapsed.Elapsed.TotalSeconds):N1} KB/s";
            }), cancel.Token);
            downloadedAttachments.Remember(key, path);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        { status.Text = "下载已取消，未完成的文件已清理。"; return; }
        finally { downloadCancel = null; downloadStatus = null; RefreshButtons(); }
        status.Text = "下载完成并已校验：" + name;
        string choice = await DisplayActionSheetAsync("下载完成：" + name, "稍后", null, "打开文件", "保存或分享");
        if (choice == "打开文件") await OpenDownloadedFileAsync(path);
        else if (choice == "保存或分享") await Share.Default.RequestAsync(new ShareFileRequest { Title = "保存或分享文件", File = new ShareFile(path) });
    }
    private async Task OpenDownloadedFileAsync(string path)
    {
        if (!await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(path) }))
        {
            if (await DisplayAlertAsync("无法打开文件", "没有可打开此类型文件的应用，可以保存或分享到其他应用。", "保存或分享", "取消"))
                await Share.Default.RequestAsync(new ShareFileRequest { Title = "保存或分享文件", File = new ShareFile(path) });
        }
    }
    private IProgress<FileProgress> Progress(string action) => new Progress<FileProgress>(p => status.Text = $"{action} {p.Completed / 1024d:N0} / {p.Total / 1024d:N0} KB");
    private async Task SettingsAsync() => await Act(async () =>
    {
        var settings = await session.GetSettingsAsync();
        await Navigation.PushAsync(new ModelPage(session, settings));
    });
    private async Task RespondAsync() => await Act(async () =>
    {
        var owner = session;
        if (owner.View is not { Prompts.Count: > 0 } view) return;
        var prompt = view.Prompts[0];
        if (view.Prompts.Count > 1)
        {
            var choices = view.Prompts.Select((item, index) =>
            {
                string title = item.Kind switch
                {
                    "item/tool/requestUserInput" or CodexQuestions.AsyncKind => "回答问题",
                    "item/commandExecution/requestApproval" => "确认命令",
                    "item/fileChange/requestApproval" => "确认文件修改",
                    CodexPermissionRequests.Kind => "确认访问权限",
                    _ => item.Title.ReplaceLineEndings(" ")
                };
                return $"{index + 1}. {title}";
            }).ToArray();
            var choice = await DisplayActionSheetAsync("选择待处理事项", "取消", null, choices);
            int index = Array.IndexOf(choices, choice);
            if (index < 0) return;
            var selected = view.Prompts[index];
            if (owner != session || owner.View is not { } latestView || latestView.Id != view.Id ||
                latestView.Prompts.FirstOrDefault(item => item.Id == selected.Id && item.Kind == selected.Kind) is not { } current)
            {
                await DisplayAlertAsync("事项已更新", "请重新打开待处理列表。", "知道了");
                return;
            }
            prompt = current;
        }
        if (prompt.Kind == CodexRecovery.Kind)
        {
            if (await DisplayAlertAsync(prompt.Title, prompt.Detail, "继续任务（新一轮）", "稍后处理"))
                await session.RespondAsync(prompt.Id, "continue");
        }
        else if (prompt.Kind is "item/tool/requestUserInput" or CodexQuestions.AsyncKind && prompt.Payload?["questions"] is JsonArray questions)
        {
            var answers = new JsonObject();
            foreach (var q in questions)
            {
                string question = q?["question"]?.ToString() ?? "Agent 需要你的回答";
                string? header = q?["header"]?.ToString();
                if (!string.IsNullOrWhiteSpace(header)) question = header + "\n" + question;
                string? detail = q?["detail"]?.ToString();
                if (!string.IsNullOrWhiteSpace(detail) && !await ReviewQuestionDetailAsync(question, detail)) return;
                string options = q?["options"] is JsonArray opts ? "\n" + string.Join("\n", opts.Select(o => $"• {o?["label"]}：{o?["description"]}")) : "";
                string? answer;
                var labels = (q?["options"] as JsonArray)?.Select(o => o?["label"]?.ToString() ?? "").Where(s => s.Length > 0).ToArray() ?? [];
                if (q?["multiSelect"]?.GetValue<bool>() == true || q?["multiple"]?.GetValue<bool>() == true)
                {
                    var selected = await SelectMultipleAnswersAsync(question, labels);
                    if (selected is null) return;
                    answers[q!["id"]!.ToString()] = new JsonObject { ["answers"] = new JsonArray(selected.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
                    continue;
                }
                if (labels.Length > 0)
                {
                    answer = await DisplayActionSheetAsync(question, "稍后回答", null, [.. labels, "自行填写…"]);
                    if (answer == "稍后回答" || answer is null) return;
                    if (answer == "自行填写…") answer = await DisplayPromptAsync("填写回答", question + options, "继续", "取消", maxLength: 4000);
                }
                else answer = await DisplayPromptAsync("Agent 需要你的回答", question, "继续", "取消", maxLength: 4000);
                if (string.IsNullOrWhiteSpace(answer)) return;
                answers[q!["id"]!.ToString()] = new JsonObject { ["answers"] = new JsonArray(JsonValue.Create(answer)) };
            }
            await session.RespondAsync(prompt.Id, "", new JsonObject { ["answers"] = answers });
        }
        else if (prompt.Kind is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or CodexPermissionRequests.Kind or "hermes/approval" or "dsh/approval")
        {
            bool accessRequest = prompt.Kind == CodexPermissionRequests.Kind;
            string detail = accessRequest ? prompt.Detail
                : prompt.Payload?["command"]?.ToString() ?? prompt.Payload?["reason"]?.ToString() ?? prompt.Detail;
            string heading = prompt.Kind == "dsh/approval" ? "DSH 工具权限" : prompt.Title;
            string[] choices = accessRequest ? ["允许本次", "本会话始终允许"] : ["允许本次"];
            string decision = await DisplayActionSheetAsync(heading + "\n" + detail, "稍后处理", "拒绝", choices);
            string? response = decision switch
            {
                "允许本次" => "accept",
                "本会话始终允许" when accessRequest => "acceptForSession",
                "拒绝" => "decline",
                _ => null
            };
            if (response is not null) await session.RespondAsync(prompt.Id, response);
        }
        else await DisplayAlertAsync("请在远端处理", prompt.Title + "\n" + prompt.Detail, "知道了");
    });
    private async Task<bool> ReviewQuestionDetailAsync(string question, string detail)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = new ContentPage { Title = "查看待回答内容" };
        Ui.Theme(page);
        bool finishing = false;
        async Task Finish(bool reviewed)
        {
            if (finishing) return;
            finishing = true;
            try { await Navigation.PopModalAsync(); completion.TrySetResult(reviewed); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        page.Disappearing += (_, _) => { if (!finishing) completion.TrySetResult(false); };
        var heading = Ui.Text(question, 18, true); Ui.TextTheme(heading);
        var content = Ui.Text(detail, 14); Ui.TextTheme(content);
        var note = Ui.Text("请阅读完整内容，再继续选择回答。", 12); Ui.TextTheme(note);
        var cancelLabel = Ui.Text("暂不回答", 13); Ui.TextTheme(cancelLabel);
        var continueLabel = Ui.Text("已阅读，继续回答", 13); Ui.TextTheme(continueLabel);
        var actions = new Grid { ColumnSpacing = 16, ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        actions.Add(new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center,
            Children = { Ui.Button("取消", () => Finish(false)), cancelLabel } });
        actions.Add(new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center,
            Children = { continueLabel, Ui.Button("继续回答", () => Finish(true), true) } }, 1);
        var layout = new Grid { Padding = 20, RowSpacing = 14, RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        layout.Add(heading); layout.Add(note, 0, 1);
        layout.Add(new ScrollView { Content = content }, 0, 2); layout.Add(actions, 0, 3);
        page.Content = layout;
        await Navigation.PushModalAsync(page);
        return await completion.Task;
    }
    private async Task<string[]?> SelectMultipleAnswersAsync(string question, string[] labels)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        string? custom = null;
        while (true)
        {
            string[] values = custom is null ? labels : [.. labels, custom];
            string[] choices = values.Select((label, index) => (index >= labels.Length || selected.Contains(label) ? "☑ " : "☐ ") + label).ToArray();
            string? choice = await DisplayActionSheetAsync(question + "\n可多选，也可填写一个补充回答；选完后点确认。", "稍后回答", null, [.. choices, "填写其他回答…", "确认选择"]);
            if (choice is null or "稍后回答") return null;
            if (choice == "确认选择")
            {
                var answers = labels.Where(selected.Contains).Concat(custom is null ? [] : new[] { custom }).Distinct(StringComparer.Ordinal).ToArray();
                if (answers.Length > 0) return answers;
                await DisplayAlertAsync("尚未回答", "请选择至少一个选项，或填写补充回答。", "继续选择");
            }
            else if (choice == "填写其他回答…")
            {
                string? value = await DisplayPromptAsync("补充回答", question, "保留", "取消", maxLength: 4000, initialValue: custom);
                if (value is null) continue;
                value = value.Trim();
                if (labels.Contains(value, StringComparer.Ordinal)) { selected.Add(value); custom = null; }
                else custom = value.Length == 0 ? null : value;
            }
            else
            {
                int index = Array.IndexOf(choices, choice);
                if (index >= labels.Length) custom = null;
                else if (index >= 0 && !selected.Add(labels[index])) selected.Remove(labels[index]);
            }
        }
    }
}
