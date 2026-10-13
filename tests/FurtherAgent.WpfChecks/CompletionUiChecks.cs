using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class CompletionUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(string output)
    {
        var app = new App(); app.InitializeComponent();
        bool foreground = true; int activations = 0;
        var shown = new List<CompletionToastWindow>();
        var window = new MainWindow(new(() => foreground, shown.Add, () => activations++));
        object pool = typeof(MainWindow).GetField("deviceConnections", Private)!.GetValue(window)!;
        object? Invoke(string name, params object[] values) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, values);
        T Find<T>(string name) => (T)window.FindName(name);
        void Set(object target, string name, object? value) => target.GetType().GetField(name, Private | BindingFlags.Public)!.SetValue(target, value);
        void Require(bool passed, string message) { if (!passed) throw new Exception(message); Console.WriteLine("PASS: " + message); }
        void Pump(Task task)
        {
            var frame = new DispatcherFrame(); var timeout = DateTime.UtcNow.AddSeconds(5);
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background,
                (_, _) => { if (task.IsCompleted || DateTime.UtcNow > timeout) frame.Continue = false; }, window.Dispatcher);
            Dispatcher.PushFrame(frame); timer.Stop();
        }
        SessionCompletion Completion(string id = "same-id") => new(Guid.NewGuid().ToString("N"), id, "桌面端会话完成通知", "turn");
        try
        {
            var contextType = typeof(MainWindow).GetNestedType("DeviceContext", BindingFlags.NonPublic)!;
            object Device(string code, string name)
            {
                var context = Activator.CreateInstance(contextType, "http://127.0.0.1:1", code)!;
                Set(context, "Name", name); Set(context, "Agent", new AgentInfo("fixture", name, true, "isolated"));
                Set(context, "SelectedId", "same-id");
                Set(context, "View", new SessionView("same-id", name, "", "idle", null, [], []));
                typeof(CompletionUiChecks).GetMethod(nameof(Add), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(contextType).Invoke(null, [pool, context, code]);
                return context;
            }
            var a = Device("AAAA1111", "Codex · 办公电脑"); var b = Device("BBBB2222", "Hermes · 服务器");
            Invoke("ActivateDevice", a); Invoke("HandleCompletion", a, Completion());
            Require(shown.Count == 0, "foreground current conversation does not display a toast");
            Invoke("HandleCompletion", a, Completion("other"));
            Require(shown.Count == 1, "foreground other conversation displays a toast");
            Invoke("HandleCompletion", b, Completion());
            Require(shown.Count == 2, "another device with the same session ID still displays a toast");
            foreground = false; var duplicate = Completion(); Invoke("HandleCompletion", a, duplicate);
            Require(shown.Count == 3 && activations == 0, "background current conversation displays a toast without activating the app");
            Invoke("HandleCompletion", a, duplicate);
            Require(shown.Count == 3, "duplicate delivery cannot display a second toast");
            foreground = true; Find<FrameworkElement>("ConversationPage").Visibility = Visibility.Collapsed;
            Invoke("HandleCompletion", a, Completion());
            Require(shown.Count == 4, "settings page counts as not viewing the conversation");
            Find<FrameworkElement>("ConversationPage").Visibility = Visibility.Visible;
            Find<FrameworkElement>("ModelOverlay").Visibility = Visibility.Visible;
            Invoke("HandleCompletion", a, Completion());
            Require(shown.Count == 5, "an open overlay counts as not viewing the conversation");
            var toast = shown.Last(); var border = (Border)toast.Content; var stack = (StackPanel)border.Child;
            Require(toast.Width == 340 && toast.Height == 112 && !toast.ShowActivated && !toast.ShowInTaskbar && toast.Topmost
                && border.CornerRadius.TopLeft == 12 && border.Padding.Left == 16 && ((TextBlock)stack.Children[0]).FontSize == 15
                && ((TextBlock)stack.Children[1]).FontSize == 12 && ((TextBlock)stack.Children[2]).FontSize == 11,
                "notification uses ResourceManager dimensions, typography, rounding and no-focus window flags");
            Require(((DispatcherTimer)typeof(CompletionToastWindow).GetField("timer", Private)!.GetValue(toast)!).Interval == TimeSpan.FromSeconds(5),
                "notification uses ResourceManager five-second timeout");
            border.Width = 340; border.Height = 112; border.Measure(new Size(340, 112)); border.Arrange(new Rect(0, 0, 340, 112)); border.UpdateLayout();
            var bitmap = new RenderTargetBitmap(340, 112, 96, 96, PixelFormats.Pbgra32); bitmap.Render(border);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            using (var file = File.Create(output)) encoder.Save(file);

            // Exercise actual click navigation with an in-memory RPC transport, never show/activate a real window.
            var remote = (RemoteDevice)contextType.GetProperty("Remote")!.GetValue(b)!;
            var transport = new AttachTransport(); var peer = new RpcPeer(transport); peer.Start();
            typeof(RemoteDevice).GetProperty("Peer")!.SetValue(remote, peer);
            Find<TextBox>("Composer").Text = "保留 A 草稿";
            var task = (Task<bool>)Invoke("OpenCompletedSessionAsync", b, Completion("finished-b"))!;
            Pump(task);
            Require(task.IsCompletedSuccessfully && task.Result && transport.Attached == "finished-b" && activations == 1
                && ReferenceEquals(typeof(MainWindow).GetField("activeDevice", Private)!.GetValue(window), b),
                "click opens the exact originating device and session through attach RPC");
            Find<TextBox>("Composer").Text = "B 当前会话尚未保存的草稿";
            task = (Task<bool>)Invoke("OpenCompletedSessionAsync", b, Completion("finished-b"))!; Pump(task);
            Require(task.IsCompletedSuccessfully && task.Result && Find<TextBox>("Composer").Text == "B 当前会话尚未保存的草稿",
                "clicking a toast for the current session preserves unsaved composer text");
            Invoke("ActivateDevice", a);
            Require(Find<TextBox>("Composer").Text == "保留 A 草稿", "notification navigation preserves the previous device draft");
            Invoke("CloseCompletionToasts");
            Require(((System.Collections.ICollection)typeof(MainWindow).GetField("completionToasts", Private)!.GetValue(window)!).Count == 0,
                "application cleanup closes every completion toast");
        }
        finally
        {
            Task.Run(async () => await ((IAsyncDisposable)pool).DisposeAsync()).GetAwaiter().GetResult();
            Invoke("DisposeTray"); app.Shutdown();
        }
    }
    private static void Add<T>(DeviceConnectionPool<T> pool, T connection, string code) where T : class, IAsyncDisposable =>
        pool.OpenAsync("http://127.0.0.1:1", code, () => Task.FromResult(connection)).GetAwaiter().GetResult();
    private sealed class AttachTransport : IJsonTransport
    {
        private readonly Channel<JsonNode> responses = Channel.CreateUnbounded<JsonNode>();
        public string? Attached;
        public async Task<JsonNode> ReadAsync(CancellationToken ct) => await responses.Reader.ReadAsync(ct);
        public Task WriteAsync(JsonNode message, CancellationToken ct)
        {
            Attached = message["args"]?["id"]?.ToString();
            responses.Writer.TryWrite(new JsonObject { ["id"] = message["id"]!.DeepClone(),
                ["result"] = Json.Node(new SessionView(Attached!, "完成的 B 会话", "", "idle", null, [new("assistant", "完成")], [])) });
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { responses.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
