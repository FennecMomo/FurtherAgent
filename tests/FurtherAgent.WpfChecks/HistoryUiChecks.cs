using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class HistoryUiChecks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    public static void Run(string output)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var source = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src/FurtherAgent.Client/App.xaml"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), source.Root!.Elements().Single().Nodes());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = RunAsync(output);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); } finally { app.Shutdown(); }
    }
    private static async Task RunAsync(string output)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string url = $"http://127.0.0.1:{port}";
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        MainWindow? window = null;
        object? pool = null;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS: " + message); }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            bool ready = false;
            for (int i = 0; i < 40; i++)
            { try { ready = (await http.GetAsync(url + "/health")).IsSuccessStatusCode; if (ready) break; } catch { } await Task.Delay(100); }
            Require(ready, "isolated history relay starts");
            using var registry = new RegistryClient(url);
            var adapter = new HistoryAdapter();
            await using var host = new DeviceHost(registry, () => adapter);
            await host.StartAsync("history test");
            window = new MainWindow();
            object? Invoke(string method, params object[] args) => typeof(MainWindow).GetMethod(method, Private | BindingFlags.DeclaredOnly)!.Invoke(window, args);
            void Set(string field, object? value) => typeof(MainWindow).GetField(field, Private)!.SetValue(window, value);
            T Find<T>(string name) => (T)window.FindName(name);
            pool = typeof(MainWindow).GetField("deviceConnections", Private)!.GetValue(window);
            var open = (Task)Invoke("OpenDeviceAsync", url, host.Lease!.Code)!; await open;
            object device = open.GetType().GetProperty("Result")!.GetValue(open)!;
            Invoke("ActivateDevice", device);
            var remote = (RemoteDevice)device.GetType().GetProperty("Remote")!.GetValue(device)!;
            Set("selectedId", "history");
            var latest = Json.Read<SessionView>(await remote.Peer.CallAsync("attach", new { id = "history" }));
            Invoke("Render", latest);
            var content = (FrameworkElement)window.Content;
            void Layout() { content.Width = 1100; content.Height = 760; content.Measure(new Size(1100,760)); content.Arrange(new Rect(0,0,1100,760)); content.UpdateLayout(); }
            Layout();
            var scroll = Find<ScrollViewer>("ChatScroll"); var messages = Find<ItemsControl>("Messages");
            Require(messages.Items.Count == 8 && Find<Button>("OlderHistoryButton").IsEnabled, "desktop exposes older-history action for an eight-record snapshot");
            scroll.ScrollToVerticalOffset(100); Layout();
            var anchor = (FrameworkElement)messages.ItemContainerGenerator.ContainerFromIndex(0);
            double before = anchor.TranslatePoint(new Point(), scroll).Y;
            var firstLoad = (Task)Invoke("LoadOlderHistoryAsync")!;
            var duplicateLoad = (Task)Invoke("LoadOlderHistoryAsync")!;
            await Task.WhenAll(firstLoad, duplicateLoad); Layout();
            var moved = (FrameworkElement)messages.ItemContainerGenerator.ContainerFromIndex(8);
            Require(messages.Items.Count == 16 && ((ChatEntry)messages.Items[0]).Text.StartsWith("记录 24"), "desktop requests and prepends previous page over the real relay");
            Require(messages.Items.Cast<ChatEntry>().Distinct().Count() == 16, "overlapping load triggers cannot duplicate a page");
            Require(Math.Abs(moved.TranslatePoint(new Point(), scroll).Y - before) < 3, "prepending preserves the visible message anchor");
            Invoke("Render", latest with { Status = "active" }); Layout();
            Require(messages.Items.Count == 16, "live status refresh does not replace the older reading window");
            var markdown = Descendants(messages).OfType<MarkdownView>().First();
            scroll.ScrollToEnd(); Layout(); double end = scroll.VerticalOffset;
            markdown.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Layout();
            Require(scroll.VerticalOffset < end, "wheel over rich text scrolls the outer conversation");
            scroll.ScrollToTop(); Layout();
            markdown.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            for (int i = 0; i < 40 && messages.Items.Count == 16; i++) await Task.Delay(50);
            Layout(); Require(messages.Items.Count == 24, "upward wheel at top loads another page automatically");
            Set("restoringScroll", true); scroll.ScrollToTop(); Layout();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var bitmap = new RenderTargetBitmap(1100,760,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(output)) encoder.Save(file);
            Set("restoringScroll", false);
            Invoke("Render", latest with { History = latest.History! with { Revision = "new-revision" }, Prompts = [new("q", "question", "测试", "", null)] });
            await (Task)Invoke("LoadOlderHistoryAsync")!;
            Require(messages.Items.Count == 24 && Find<TextBlock>("StatusBar").Text.Contains("历史已更新"), "changed history revision preserves reading and explains recovery");
            Find<Button>("LatestHistoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            Require(messages.Items.Count == 8 && Find<Button>("LatestHistoryButton").Visibility == Visibility.Collapsed, "return-latest restores current live history");
            Invoke("Render", latest);
            var staleLoad = (Task)Invoke("LoadOlderHistoryAsync")!;
            Set("selectionVersion", 100); Set("selectedId", "another");
            Invoke("Render", new SessionView("another", "另一会话", "", "idle", null, [new("assistant", "另一会话正文")], []));
            await staleLoad;
            Require(messages.Items.Count == 1 && ((ChatEntry)messages.Items[0]).Text == "另一会话正文", "late history response cannot overwrite a newly selected session");
            Set("selectedId", "operations");
            var operationView = Json.Read<SessionView>(await remote.Peer.CallAsync("attach", new { id = "operations" }));
            Require(operationView.Messages.Any(m => m.Operations is not null), "desktop negotiates grouped operation history through the real relay");
            operationView = operationView with { OutputFiles = Enumerable.Range(0, 30).Select(i => new RemoteFile("outbox/" + i, "文件" + i + ".txt", 1024)).ToList() };
            Invoke("Render", operationView); Layout();
            var grouped = Descendants(messages).OfType<Expander>().First(e => e.Header?.ToString()?.Contains("项操作记录") == true);
            Require(!grouped.IsExpanded, "desktop consecutive operations default to collapsed");
            grouped.IsExpanded = true; Layout();
            Require(!Descendants(grouped).OfType<TextBox>().Any() && Descendants(grouped).OfType<TextBlock>().Any(t => t.Text == "执行了一项操作"), "expanded group shows action summaries without raw detail controls");
            var fileGroup = Find<StackPanel>("OutputFilesPanel").Children.OfType<Expander>().Single();
            Require(!fileGroup.IsExpanded && fileGroup.ActualHeight < 50, "thirty session files occupy one collapsed row");
            fileGroup.IsExpanded = true; Layout();
            Require(Descendants(fileGroup).OfType<Button>().Count() == 30, "expanding session files preserves every save action");
            Invoke("Render", operationView with { OutputFiles = operationView.OutputFiles!.Append(new("outbox/new", "新增.txt", 20)).ToList() }); Layout();
            Require(Find<StackPanel>("OutputFilesPanel").Children.OfType<Expander>().Single().IsExpanded, "file list refresh preserves the user's expanded state");
            Require(Descendants(messages).OfType<Expander>().First(e => e.Header?.ToString()?.Contains("项操作记录") == true).IsExpanded, "snapshot refresh preserves manually expanded operation group");
            Find<StackPanel>("OutputFilesPanel").Children.OfType<Expander>().Single().IsExpanded = false;
            foreach (var expander in Descendants(messages).OfType<Expander>().ToList()) expander.IsExpanded = false;
            Layout(); scroll.ScrollToEnd(); Layout();
            var compactBitmap = new RenderTargetBitmap(1100,760,96,96,PixelFormats.Pbgra32); compactBitmap.Render(content);
            var compactEncoder = new PngBitmapEncoder(); compactEncoder.Frames.Add(BitmapFrame.Create(compactBitmap));
            using (var file = File.Create(Path.ChangeExtension(output, ".compact.png"))) compactEncoder.Save(file);
        }
        finally
        {
            if (pool is IAsyncDisposable resources) await resources.DisposeAsync();
            if (window is not null) typeof(MainWindow).GetMethod("DisposeTray", Private)!.Invoke(window, null);
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        { var child=VisualTreeHelper.GetChild(parent,i); yield return child; foreach(var nested in Descendants(child)) yield return nested; }
    }
    private sealed class HistoryAdapter : IAgentAdapter
    {
        public AgentInfo Info => new("codex", "history fixture", true, "isolated history fixture");
        public event Action<SessionView>? Changed { add { } remove { } }
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new("history", "历史加载验证", "")]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) => Task.FromResult(id == "operations"
            ? new SessionView(id, "操作显示验证", "fixture", "idle", null, [new("你", "开始"), new("工具", "读文件"), new("工具", "执行检查"), new("Codex", "回复正文")], [])
            : new SessionView(id,"历史加载验证","fixture","idle",null,
            Enumerable.Range(0,40).Select(i=>new ChatEntry("assistant",$"记录 {i}\n\n这是可选择的正文，用来验证向上加载更早记录时不跳动。\n\n每次请求只返回八条消息。")).ToList(),[]));
        public Task SendAsync(string text,CancellationToken ct) => Task.CompletedTask;
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id,string decision,System.Text.Json.Nodes.JsonNode? response,CancellationToken ct)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
