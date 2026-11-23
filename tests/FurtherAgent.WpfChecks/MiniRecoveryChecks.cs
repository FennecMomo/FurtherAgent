using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class MiniRecoveryChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var source = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src/FurtherAgent.Client/App.xaml"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            source.Root!.Elements().Single().Nodes());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = RunAsync(root);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); }
        finally { app.Shutdown(); }
    }

    private static async Task RunAsync(string root)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string url = $"http://127.0.0.1:{port}";
        var windowType = typeof(MainWindow);
        MainWindow CreateMain() => (MainWindow)Activator.CreateInstance(windowType, Private, null, [null, url], null)!;
        string fixture = Path.Combine(root, "artifacts", "checks", "mini-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string identity = Path.Combine(fixture, "fixture.identity");
        string secret = new('A', 64);
        File.WriteAllText(identity, secret);
        string code = DeviceIdentity.Code(secret);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(url);
        start.Environment["FURTHERAGENT_MAILBOX_ROOT"] = Path.Combine(fixture, "server-mailboxes");
        using var server = Process.Start(start)!;
        int initialPairFailures = 0;
        server.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line && line.Contains("/pair") && line.Contains("404"))
                Interlocked.Increment(ref initialPairFailures);
        };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        using var registry = new RegistryClient(url);
        DeviceHost? host = null;
        var minis = new List<Window>();
        var recoveries = new List<Task>();
        var extraConnections = new List<RemoteDevice>();
        var state = new RecoveryState();
        int physicalControls = 0;
        var miniType = typeof(MainWindow).Assembly.GetType("FurtherAgent.Client.MiniWindow")!;
        T Field<T>(Window mini, string name) => (T)miniType.GetField(name, Private)!.GetValue(mini)!;
        Task StartMini(Window mini) => (Task)miniType.GetMethod("StartAsync")!.Invoke(mini, null)!;
        bool Ready(Window mini) => Field<Button>(mini, "send").IsEnabled;
        Window CreateMini(string id, int slot, RemoteDevice? sharedOwner = null)
        {
            var view = new SessionView(id, "恢复验证", "fixture", "idle", null, [new("assistant", "先前已收到的正文")], []);
            object?[] arguments = [url, code, id, view.Title, view, Path.Combine(fixture, "mini-" + slot + ".json"),
                (Func<string>)(() => "保留的草稿"), (Action<string>)(_ => { }), (Action)(() => { }), slot, 0];
            if (sharedOwner is not null) arguments = [.. arguments, sharedOwner, registry];
            var mini = (Window)Activator.CreateInstance(miniType, arguments)!;
            minis.Add(mini);
            return mini;
        }
        async Task<DeviceHost> StartHost(string? identityFile = null)
        {
            string? previousIdentity = Environment.GetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE");
            string? previousAccounts = Environment.GetEnvironmentVariable("FURTHERAGENT_MAILBOX_ACCOUNT_DIR");
            var candidate = new DeviceHost(registry, () => new RecoveryAdapter(state), Path.Combine(fixture, "files"),
                persistentIdentity: true, enableLan: false);
            candidate.Status += message =>
            {
                if (message == "远程端已通过服务端中继连接。") Interlocked.Increment(ref physicalControls);
            };
            try
            {
                Environment.SetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE", identityFile ?? identity);
                Environment.SetEnvironmentVariable("FURTHERAGENT_MAILBOX_ACCOUNT_DIR", Path.Combine(fixture, "accounts"));
                await candidate.StartAsync("isolated mini recovery");
                return candidate;
            }
            catch { await candidate.DisposeAsync(); throw; }
            finally
            {
                Environment.SetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE", previousIdentity);
                Environment.SetEnvironmentVariable("FURTHERAGENT_MAILBOX_ACCOUNT_DIR", previousAccounts);
            }
        }
        try
        {
            await Until(async () =>
            {
                try { await registry.CheckHealthAsync(default); return true; }
                catch { return false; }
            });
            var first = CreateMini("same-session", 1);
            Task recovery = StartMini(first);
            recoveries.Add(recovery);
            Check(ReferenceEquals(recovery, StartMini(first)), "starting a mini twice reuses its single recovery task");
            await Until(() => Volatile.Read(ref initialPairFailures) > 0);
            Check(!recovery.IsCompleted && !Ready(first) && Field<TextBox>(first, "composer").Text == "保留的草稿" &&
                Field<TextBlock>(first, "context").Text.Contains("先前已收到的正文"),
                "an initial pairing failure keeps recovery alive and preserves cached text and draft");

            host = await StartHost();
            await Until(() => Volatile.Read(ref state.AttachAttempts) > 0);
            Check(Field<RemoteSessionConnection>(first, "remote").IsNetworkConnected && !Ready(first) &&
                Field<TextBlock>(first, "statusLabel").Text.Contains("会话"),
                "a failed session attach is distinguished from a live network connection");
            state.InitialAttachRelease.TrySetResult();
            await Until(() => Ready(first));
            Check(state.AttachAttempts >= 2 && Field<TextBox>(first, "composer").Text == "保留的草稿",
                "the mini retries its failed attach after the host comes online");

            var sharedOwner = new RemoteDevice();
            extraConnections.Add(sharedOwner);
            await sharedOwner.ConnectAsync(registry, code);
            var second = CreateMini("same-session", 2, sharedOwner);
            recoveries.Add(StartMini(second));
            var third = CreateMini("other-session", 3, sharedOwner);
            recoveries.Add(StartMini(third));
            await Until(() => Ready(second) && Ready(third));
            Check(Ready(first) && Field<RemoteSessionConnection>(second, "remote").IsSharedConnection &&
                Field<RemoteSessionConnection>(third, "remote").IsSharedConnection && Volatile.Read(ref physicalControls) == 2,
                "the production mini constructor shares one owner transport across independent session windows");
            await host.DisposeAsync();
            host = null;
            await Until(() => !Ready(first) && !Ready(second) && !Ready(third));
            state.FailNextAttach = 1;
            int beforeReconnect = state.AttachAttempts;
            host = await StartHost();
            await Until(() => Ready(first) && Ready(second) && Ready(third));
            Check(state.AttachAttempts >= beforeReconnect + 4 && state.Sends == 0 &&
                Field<RemoteSessionConnection>(second, "remote").IsNetworkConnected &&
                Field<RemoteSessionConnection>(third, "remote").IsNetworkConnected,
                "shared minis follow the owner disconnect and recover their sessions without replaying a message");
            third.Close();
            await sharedOwner.Peer.CallAsync("agents", null);
            Check(Ready(second) && sharedOwner.IsConnected,
                "closing one shared mini leaves the parent transport and the other mini usable");

            for (int i = 0; i < 10; i++)
            {
                var remote = new RemoteDevice(autoReconnect: false);
                extraConnections.Add(remote);
                await remote.ConnectAsync(registry, code);
            }
            Check(extraConnections.All(connection => connection.IsConnected) && Ready(first) && Ready(second),
                "the relay retains twelve concurrent physical controls while minis share their owner");

            var main = CreateMain();
            object mainPool = typeof(MainWindow).GetField("deviceConnections", Private)!.GetValue(main)!;
            try
            {
                var openDevice = typeof(MainWindow).GetMethod("OpenDeviceAsync", Private)!;
                var open = (Task)openDevice.Invoke(main, [url, code])!;
                await open;
                object device = open.GetType().GetProperty("Result")!.GetValue(open)!;
                typeof(MainWindow).GetMethod("ActivateDevice", Private)!.Invoke(main, [device]);
                typeof(MainWindow).GetField("selectedId", Private)!.SetValue(main, "main-session");
                state.FailNextAttach = 1;
                int beforeMainRestore = state.AttachAttempts;
                await ((Task)typeof(MainWindow).GetMethod("RestoreDeviceAsync", Private)!.Invoke(main, [device])!)
                    .WaitAsync(TimeSpan.FromSeconds(15));
                var mainView = (SessionView?)typeof(MainWindow).GetField("current", Private)!.GetValue(main);
                Check(state.AttachAttempts >= beforeMainRestore + 2 && mainView?.Id == "main-session" &&
                    device.GetType().GetField("Recovery")!.GetValue(device) is null && state.Sends == 0,
                    "the main window retries session restoration and clears recovery state without sending a message");

                state.FailAllAttaches = true;
                int beforeFailure = state.AttachAttempts;
                var composer = (TextBox)main.FindName("Composer");
                composer.Text = "fixture draft";
                await ((Task)typeof(MainWindow).GetMethod("RestoreDeviceAsync", Private)!.Invoke(main, [device])!)
                    .WaitAsync(TimeSpan.FromSeconds(15));
                await Until(() => (main.FindName("ActivityStatus") as TextBlock)?.Text.Contains("可刷新重试", StringComparison.Ordinal) == true);
                var failedStatus = Json.Node(main.Control("status"));
                Check(state.AttachAttempts == beforeFailure + 3 && failedStatus["sessionLoading"]?.GetValue<bool>() == false &&
                    failedStatus["sessionError"]?.ToString() == "fixture attach failure" && failedStatus["sessionPhase"]?.ToString() == "读取会话历史" &&
                    ReferenceEquals(mainView, typeof(MainWindow).GetField("current", Private)!.GetValue(main)) && composer.Text == "fixture draft",
                    "persistent session errors stop automatic retries, report their phase and preserve the cached view and draft");

                state.FailAllAttaches = false;
                state.ReturnReadOnly = true;
                typeof(MainWindow).GetMethod("Refresh_Click", Private)!.Invoke(main, [main, new RoutedEventArgs()]);
                await Until(() => (main.FindName("ActivityStatus") as TextBlock)?.Text == "会话已加载 · 只读历史");
                Check(Json.Node(main.Control("status"))["sessionError"] is null && state.AttachAttempts == beforeFailure + 4 &&
                    !((Button)main.FindName("SendButton")).IsEnabled && state.Sends == 0,
                    "manual refresh reloads the selected failed session and reports read-only history without sending a message");
                state.ReturnReadOnly = false;

                string delayedSecret = new('B', 64);
                string delayedIdentity = Path.Combine(fixture, "delayed.identity");
                File.WriteAllText(delayedIdentity, delayedSecret);
                var delayedLease = await registry.PostAsync<DeviceLease>("devices", new DeviceRegistration("isolated late host", delayedSecret));
                var retrying = (Task)openDevice.Invoke(main, [url, delayedLease.Code])!;
                var remoteStatus = (TextBlock)main.FindName("RemoteStatus");
                await Until(() => retrying.IsCompleted || remoteStatus.Text.Contains("自动重试", StringComparison.Ordinal));
                Check(!retrying.IsCompleted, "the main window keeps the first attempt alive after the relay reports an offline host");
                await using var delayedHost = await StartHost(delayedIdentity);
                await retrying.WaitAsync(TimeSpan.FromSeconds(15));
                object recovered = retrying.GetType().GetProperty("Result")!.GetValue(retrying)!;
                var recoveredRemote = (RemoteDevice)recovered.GetType().GetProperty("Remote")!.GetValue(recovered)!;
                Check(recoveredRemote.IsConnected && state.Sends == 0,
                    "the main window completes initial device loading after a registered host comes online without replaying a message");
            }
            finally
            {
                await ((IAsyncDisposable)mainPool).DisposeAsync();
                typeof(MainWindow).GetMethod("DisposeTray", Private)!.Invoke(main, null);
            }

            string shortcutSecret = new('C', 64);
            string shortcutIdentity = Path.Combine(fixture, "shortcut.identity");
            File.WriteAllText(shortcutIdentity, shortcutSecret);
            string shortcutCode = DeviceIdentity.Code(shortcutSecret);
            var shortcutStore = new SavedConnectionStore(Path.Combine(fixture, "shortcut-connection.json"));
            shortcutStore.Save(new(url, shortcutCode, "isolated shortcut fixture", AutoConnect: true));
            var shortcutWindow = CreateMain();
            shortcutWindow.ShowActivated = false; shortcutWindow.ShowInTaskbar = false; shortcutWindow.Opacity = 0;
            var shortcutPool = (IAsyncDisposable)windowType.GetField("deviceConnections", Private)!.GetValue(shortcutWindow)!;
            var openFromShortcut = windowType.GetMethod("OpenFromShortcutAsync", Private)!;
            var connectButton = (Button)shortcutWindow.FindName("ConnectButton");
            var connectError = (TextBlock)shortcutWindow.FindName("ConnectError");
            var remoteField = windowType.GetField("remote", Private)!;
            try
            {
                windowType.GetField("connectionStore", Private)!.SetValue(shortcutWindow, shortcutStore);
                windowType.GetField("startSavedConnection", Private)!.SetValue(shortcutWindow, true);
                ((TextBox)shortcutWindow.FindName("RemoteCode")).Text = shortcutCode;
                ((TextBox)shortcutWindow.FindName("ConnectionName")).Text = "isolated shortcut fixture";
                shortcutWindow.StartSavedConnection();
                await Until(() => connectButton.IsEnabled && !string.IsNullOrEmpty(connectError.Text));
                Check(connectError.Text.Contains("找不到该设备的在线登记", StringComparison.Ordinal) && remoteField.GetValue(shortcutWindow) is null,
                    "saved startup leaves a recoverable profile when its host has no relay registration yet");

                await using var shortcutHost = await StartHost(shortcutIdentity);
                shortcutWindow.RestoreFromTray();
                Check(connectButton.IsEnabled && remoteField.GetValue(shortcutWindow) is null,
                    "restoring an existing window alone leaves the failed startup unchanged");

                int controlsBeforeShortcut = Volatile.Read(ref physicalControls);
                var firstOpen = (Task)openFromShortcut.Invoke(shortcutWindow, null)!;
                var secondOpen = (Task)openFromShortcut.Invoke(shortcutWindow, null)!;
                await Task.WhenAll(firstOpen, secondOpen).WaitAsync(TimeSpan.FromSeconds(15));
                Check((remoteField.GetValue(shortcutWindow) as RemoteDevice)?.IsConnected == true &&
                    Volatile.Read(ref physicalControls) == controlsBeforeShortcut + 1 && state.Sends == 0,
                    "reopening the same window through the shortcut reconnects once without replaying a message");
            }
            finally
            {
                shortcutWindow.Hide();
                await shortcutPool.DisposeAsync();
                ((DispatcherTimer)windowType.GetField("snapshotTimer", Private)!.GetValue(shortcutWindow)!).Stop();
                windowType.GetMethod("DisposeTray", Private)!.Invoke(shortcutWindow, null);
            }

            var waiting = CreateMini("blocked-session", 4);
            Task waitingRecovery = StartMini(waiting);
            recoveries.Add(waitingRecovery);
            await state.BlockedAttachEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            waiting.Close();
            await waitingRecovery.WaitAsync(TimeSpan.FromSeconds(3));
            Check(waitingRecovery.IsCompletedSuccessfully && state.Sends == 0,
                "closing a mini cancels its in-flight attach promptly without a submission");
        }
        finally
        {
            foreach (var mini in minis) mini.Close();
            try { await Task.WhenAll(recoveries).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally
            {
                foreach (var connection in extraConnections) await connection.DisposeAsync();
                try { if (host is not null) await host.DisposeAsync(); }
                finally { if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } }
            }
        }
    }

    private static Task Until(Func<bool> condition) => Until(() => Task.FromResult(condition()));

    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await condition()) await Task.Delay(40, timeout.Token);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class RecoveryState
    {
        public int AttachAttempts;
        public int FailNextAttach = 1;
        public volatile bool FailAllAttaches;
        public volatile bool ReturnReadOnly;
        public int Sends;
        public TaskCompletionSource InitialAttachRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedAttachEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecoveryAdapter(RecoveryState state) : IAgentAdapter
    {
        public AgentInfo Info => new("codex", "mini recovery fixture", true, "isolated fixture");
        public event Action<SessionView>? Changed { add { } remove { } }
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([new("same-session", "恢复验证", "")]);

        public async Task<SessionView> AttachAsync(string id, CancellationToken ct)
        {
            Interlocked.Increment(ref state.AttachAttempts);
            if (id == "blocked-session")
            {
                state.BlockedAttachEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            if (state.FailAllAttaches) throw new IOException("fixture attach failure");
            if (Interlocked.Exchange(ref state.FailNextAttach, 0) == 1)
                throw new IOException("fixture session snapshot is temporarily unavailable");
            await state.InitialAttachRelease.Task.WaitAsync(ct);
            return new SessionView(id, "恢复验证", "fixture", state.ReturnReadOnly ? "readOnly" : "idle", null,
                [new("assistant", "恢复后的正文")], [], CanSend: !state.ReturnReadOnly);
        }

        public Task SendAsync(string text, CancellationToken ct) { Interlocked.Increment(ref state.Sends); return Task.CompletedTask; }
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, string decision, System.Text.Json.Nodes.JsonNode? response, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
