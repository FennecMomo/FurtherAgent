using ConnectionProfile = FurtherAgent.Mobile.ConnectionProfile;
using System.Text.Json;
using FurtherAgent.Core;
using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

// Reuses the chat transcript and speech input, but all sends go only to the durable mailbox.
internal sealed class MailboxPage : ContentPage
{
    private readonly ConnectionProfile profile;
    private readonly WebView transcript = new();
    private readonly Editor composer = new() { Placeholder = "写几条留言，等对方方便时再看…", AutoSize = EditorAutoSizeOption.TextChanges,
        MinimumHeightRequest = 54, MaximumHeightRequest = 150, MaxLength = MailboxClient.MaxTextLength };
    private readonly Label status = Ui.Text("正在读取留言…", 12);
    private readonly Button send, retry, voice;
    private readonly IDispatcherTimer timer;
    private MailboxRuntime.Entry? entry;
    private List<MailboxMessage> rendered = [];
    private List<string> copiedTexts = [];
    private bool active, webReady, rendering, readingOlder, busy, loading, voiceHeld, voiceDiscard, recognizingSpeech;
    private CancellationTokenSource? speechCancel, speechStop;
    private long? historyEnd;

    internal MailboxPage(ConnectionProfile profile)
    {
        this.profile = profile;
        Title = "留言 · " + profile.Name;
        transcript.Source = new HtmlWebViewSource { Html = Transcript.Shell };
        transcript.Navigated += async (_, _) => { webReady = true; await RenderAsync(); };
        transcript.Navigating += Navigate;
        composer.Text = Preferences.Get(MailboxRuntime.DraftKey(profile.Id), "");
        composer.TextChanged += (_, _) => Preferences.Set(MailboxRuntime.DraftKey(profile.Id), composer.Text ?? "");
        send = Ui.Button("发送 ↑", SendAsync, true); send.AutomationId = "mailbox-send";
        retry = Ui.Button("刷新", RefreshAsync); retry.AutomationId = "mailbox-refresh";
        voice = Ui.Button("语音", () => Task.CompletedTask);
        new HoldToTalk(voice, () =>
        {
            if (busy || recognizingSpeech) return;
            voiceHeld = true; voiceDiscard = false; _ = VoiceAsync();
        }, discard =>
        {
            voiceDiscard = discard; status.Text = discard ? "松开放弃本次语音" : "松手停止，文字保留 · 下拉放弃";
        }, discard =>
        {
            voiceHeld = false; voiceDiscard = discard;
            if (discard) speechCancel?.Cancel(); else speechStop?.Cancel();
        }).Attach();
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 8 };
        actions.Add(retry); actions.Add(Ui.Text("留言不会立即唤醒对方", 11), 1); actions.Add(voice, 2); actions.Add(send, 3);
        var footer = new VerticalStackLayout { Padding = 12, Spacing = 6, Children = { status, composer, actions } };
        var layout = new Grid { RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        layout.Add(transcript); layout.Add(footer, 0, 1); Content = layout;
        ToolbarItems.Add(new ToolbarItem("语音设置", null, async () => await Navigation.PushAsync(new SpeechSettingsPage())));
        ToolbarItems.Add(new ToolbarItem("重新绑定", null, async () => await RebindAsync()));
        timer = Dispatcher.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(15);
        timer.Tick += async (_, _) => { if (active) await RefreshAsync(); };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing(); active = true; MailboxRuntime.ViewingProfile = profile.Id;
        MailboxRuntime.Changed += OnChanged; timer.Start();
        await CompletionNotifications.RequestPermissionOnceAsync();
        await RefreshAsync();
    }
    protected override void OnDisappearing()
    {
        active = false; timer.Stop(); MailboxRuntime.Changed -= OnChanged;
        if (MailboxRuntime.ViewingProfile == profile.Id) MailboxRuntime.ViewingProfile = null;
        speechCancel?.Cancel(); base.OnDisappearing();
    }
    internal async Task ResumeForegroundAsync()
    {
        active = true; MailboxRuntime.ViewingProfile = profile.Id; timer.Start();
        await RefreshAsync();
    }
    internal void SuspendForeground()
    {
        active = false; timer.Stop(); voiceHeld = false; speechCancel?.Cancel();
    }
    private async void OnChanged(string id) { if (active && id == profile.Id) await RenderAsync(); }
    private async Task RefreshAsync()
    {
        if (busy || loading) return;
        loading = true;
        try
        {
            entry ??= await MailboxRuntime.OpenAsync(profile, false);
            if (entry is null)
            {
                status.Text = "首次绑定需家里接入端在线；之后家里离线也可留言。";
                entry = await MailboxRuntime.OpenAsync(profile, true);
            }
            if (entry is not null) await MailboxRuntime.RefreshAsync(entry);
            await RenderAsync();
        }
        catch (Exception ex) { status.Text = Message(ex); }
        finally { loading = false; }
    }
    private async Task RebindAsync()
    {
        try { entry = await MailboxRuntime.RebindAsync(profile); await RefreshAsync(); }
        catch (Exception ex) { status.Text = Message(ex); }
    }
    private async Task SendAsync()
    {
        if (busy || string.IsNullOrWhiteSpace(composer.Text)) return;
        if (recognizingSpeech) { status.Text = "请先松手结束语音，再发送留言。"; return; }
        busy = true; send.IsEnabled = false;
        try
        {
            entry ??= await MailboxRuntime.OpenAsync(profile, true);
            if (entry is null) return;
            string text = composer.Text ?? "";
            await MailboxRuntime.QueueAsync(entry, text);
            if (composer.Text == text) composer.Text = "";
            readingOlder = false; historyEnd = null;
            await RenderAsync();
        }
        catch (Exception ex) { status.Text = Message(ex); }
        finally { busy = false; send.IsEnabled = true; }
    }
    private async Task RenderAsync()
    {
        if (!active || !webReady || entry is null || rendering) return;
        rendering = true;
        try
        {
            rendered = entry.Messages.Where(m => historyEnd is null || m.Sequence <= historyEnd).TakeLast(200).ToList();
            bool older = rendered.Count > 0 && (entry.HasOlder || entry.Messages[0].Sequence < rendered[0].Sequence);
            var messages = rendered.Select(m => new ChatEntry(m.Sender == "controller" ? "user" : "assistant",
                m.Text + "\n\n" + m.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm") + (m.Sender == "controller" ?
                    m.Sequence <= entry.AgentReadThrough ? " · 对方已读" : " · 已保存到留言箱" : ""))).ToList();
            messages.AddRange(entry.Outbox.Select(p => new ChatEntry("user", p.Text + "\n\n待发送 · 网络恢复后使用原消息 ID 重试")));
            copiedTexts = rendered.Select(m => m.Text).Concat(entry.Outbox.Select(p => p.Text)).ToList();
            var view = new SessionView("mailbox", profile.Name, "", "idle", null, messages, []);
            string html = Transcript.Render(view, older, readingOlder, conversationLimit: 240);
            await transcript.EvaluateJavaScriptAsync($"renderTranscript({JsonSerializer.Serialize(html)}, false, false);");
            status.Text = entry.Error ?? (entry.Outbox.Count > 0 ? $"{entry.Outbox.Count} 条留言待发送，可点刷新重试。" : "留言已同步；收到留言不会触发对方的 Agent 任务。");
            if (CompletionNotifications.Foreground && MailboxRuntime.ViewingProfile == profile.Id && !readingOlder)
                await MailboxRuntime.MarkReadAsync(entry, rendered.LastOrDefault()?.Sequence ?? 0);
        }
        catch (Exception ex) { status.Text = Message(ex); }
        finally { rendering = false; }
    }
    private async void Navigate(object? sender, WebNavigatingEventArgs args)
    {
        if (args.Url is "about:blank" || args.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        args.Cancel = true;
        try
        {
            if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme == "fa-history" && entry is not null)
            {
                if (uri.Host == "latest") { readingOlder = false; historyEnd = null; await RenderAsync(); }
                else if (uri.Host == "older" && !busy)
                {
                    long first = rendered.FirstOrDefault()?.Sequence ?? 0;
                    if (entry.Messages.FirstOrDefault()?.Sequence == first && entry.HasOlder) await MailboxRuntime.LoadOlderAsync(entry);
                    if (entry.Messages.Count > 200 && first > 0) historyEnd = first - 1;
                    readingOlder = true; await RenderAsync();
                    await transcript.EvaluateJavaScriptAsync("historyLoadDone()");
                }
            }
            else if (uri.Scheme == "fa-copy" && int.TryParse(uri.Host, out int index))
            {
                if (index >= 0 && index < copiedTexts.Count) { await Clipboard.SetTextAsync(copiedTexts[index]); status.Text = "已复制留言"; }
            }
            else if (uri.Scheme is "http" or "https") await Browser.OpenAsync(uri, BrowserLaunchMode.External);
        }
        catch (Exception ex) { status.Text = Message(ex); }
    }
    private string Message(Exception ex)
    {
        string text = ex is NotSupportedException ? "中继尚未启用留言，请先升级服务器。" : ex.Message;
        text = text.Replace(profile.Code, "[授权码]", StringComparison.Ordinal);
        if (entry?.Client.Binding.Token is { Length: > 0 } token) text = text.Replace(token, "[授权信息]", StringComparison.Ordinal);
        return text;
    }

    private async Task VoiceAsync()
    {
        string original = composer.Text ?? "";
        string key = MailboxRuntime.DraftKey(profile.Id);
        try
        {
            string apiKey = await SecureStorage.GetAsync(SpeechSettingsPage.KeyName) ?? "";
            if (string.IsNullOrWhiteSpace(apiKey)) { await Navigation.PushAsync(new SpeechSettingsPage()); return; }
            if (await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
                throw new InvalidOperationException("需要麦克风权限才能语音输入，原草稿已保留。");
            if (!active || !voiceHeld) return;
            using var cancel = new CancellationTokenSource(); using var stopCapture = new CancellationTokenSource();
            speechCancel = cancel; speechStop = stopCapture; recognizingSpeech = true;
            send.IsEnabled = false; status.Text = "正在连接语音服务…";
            var endpoint = FunAsrClient.Endpoint(Preferences.Get(SpeechSettingsPage.WorkspaceName, ""));
            await Task.Run(() => new FunAsrClient().RunAsync(apiKey, endpoint,
                ct => MicrophoneAudio.Capture(() => MainThread.BeginInvokeOnMainThread(() =>
                {
                    status.Text = "正在听 · 松手停止，文字保留 · 下拉放弃";
                }), stopCapture.Token, ct),
                text => MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (!active || voiceDiscard) return;
                    string merged = SpeechTranscript.Append(original, text);
                    Preferences.Set(key, merged); composer.Text = merged;
                }).GetAwaiter().GetResult(), cancel.Token));
            status.Text = "语音已转为草稿，请确认后发送留言。";
        }
        catch (OperationCanceledException) { status.Text = "语音输入已停止，已识别的文字保留。"; }
        catch (Exception ex) { status.Text = "语音识别未完成，草稿已保留：" + ex.Message; }
        finally
        {
            if (voiceDiscard) composer.Text = original;
            recognizingSpeech = false; speechCancel = speechStop = null; send.IsEnabled = !busy;
        }
    }
}
