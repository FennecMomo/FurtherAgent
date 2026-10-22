using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class ImageUiChecks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int passed;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS: " + message); passed++; }
    public static void Run(string output)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(repo, "src/FurtherAgent.Client/App.xaml"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new System.Xml.Linq.XElement(ns + "ResourceDictionary", new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), document.Root!.Elements().Single().Nodes());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = RunAsync(repo, output);
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); } finally { app.Shutdown(); }
    }

    private static async Task RunAsync(string repo, string output)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "fa-images-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string url = $"http://127.0.0.1:{port}";
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(repo, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        MainWindow? window = null;
        object? Invoke(string method, params object[] values) => typeof(MainWindow).GetMethod(method, Private | BindingFlags.DeclaredOnly)!.Invoke(window, values);
        void Set(string field, object? value) => typeof(MainWindow).GetField(field, Private)!.SetValue(window, value);
        T Field<T>(string field) => (T)typeof(MainWindow).GetField(field, Private)!.GetValue(window)!;
        T Find<T>(string name) => (T)window!.FindName(name);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            await Eventually(async () => { try { return (await http.GetAsync(url + "/health")).IsSuccessStatusCode; } catch { return false; } });
            string filesRoot = Path.Combine(fixture, "remote");
            using var store = new FileTransferStore(filesRoot);
            string picture = Path.Combine(store.List("images").Outbox, "远端 图片.png");
            var pixels = new byte[1600 * 1200 * 3]; new Random(12).NextBytes(pixels);
            using (var file = File.Create(picture)) new StbImageWriteSharp.ImageWriter().WritePng(pixels, 1600, 1200, StbImageWriteSharp.ColorComponents.RedGreenBlue, file);
            var adapter = new ImageAdapter();
            using var registry = new RegistryClient(url);
            await using var host = new DeviceHost(registry, () => adapter, filesRoot, enableLan: false);
            await host.StartAsync("isolated image fixture");
            await using var remote = new RemoteDevice(); await remote.ConnectAsync(registry, host.Lease!.Code);
            window = new MainWindow(); Set("remote", remote); Set("agent", adapter.Info); Set("selectedId", "images");
            var initial = Json.Read<SessionView>(await remote.Peer.CallAsync("attach", new { id = "images" }));
            Invoke("Render", initial);
            var outputs = Find<StackPanel>("OutputFilesPanel");
            Check(outputs.Children.OfType<WrapPanel>().Single().Children.Count == 1, "bare output filename gets an automatic image preview outside the collapsed file list");
            Check(outputs.Children.OfType<Expander>().Single().IsExpanded == false, "file list stays collapsed while image preview is visible");
            var imageFile = initial.OutputFiles!.Single();
            Check(window.CreateFileVisual("attachment:" + imageFile.Name) is not null, "Unicode attachment reference resolves");
            Check(window.CreateFileVisual(imageFile.Id) is not null && window.CreateFileVisual("attachment:" + imageFile.Id) is not null, "file IDs with attachment prefix resolve");
            Check(window.CreateFileVisual("sandbox:/mnt/data/" + Uri.EscapeDataString(imageFile.Name)) is not null, "sandbox reference resolves only to known session metadata");
            Check(window.CreateFileVisual("https://example.com/" + imageFile.Name) is null && window.CreateFileVisual("file:///" + imageFile.Name) is null && window.CreateFileVisual("attachment:missing.png") is null, "external URLs and unknown files never become local image reads");

            var explicitView = initial with { Messages = [new("assistant", "图片之前 ![结果](attachment:" + Uri.EscapeDataString(imageFile.Name) + ") 图片之后")] };
            Invoke("Render", explicitView);
            Check(!outputs.Children.OfType<WrapPanel>().Any(), "inline image is not duplicated in the automatic gallery");
            Invoke("Render", initial with { Messages = [new("assistant", "`![代码](attachment:" + imageFile.Name + ")`\n```\nattachment:" + imageFile.Name + "\n```")] });
            Check(outputs.Children.OfType<WrapPanel>().Single().Children.Count == 1, "code examples cannot suppress a real output image preview");
            Invoke("Render", initial);

            // A hidden HWND supplies WPF Loaded/Unloaded without showing or activating any window.
            using var hidden = new HwndSource(new HwndSourceParameters("FurtherAgent isolated image check") { WindowStyle = unchecked((int)0x80000000), Width = 360, Height = 280 });
            var imageButton = (Button)window.CreateFileVisual("attachment:" + imageFile.Id)!;
            long before = remote.ReceivedBytes;
            hidden.RootVisual = imageButton;
            await Eventually(() => Task.FromResult(imageButton.Content is Image));
            var thumbnail = (BitmapSource)((Image)imageButton.Content).Source;
            Check(thumbnail.PixelWidth <= 320 && thumbnail.PixelHeight <= 240, "real relay automatically supplies bounded thumbnail on load");
            Check(remote.ReceivedBytes - before < 200 * 1024 && new FileInfo(picture).Length > 5_000_000, "rendering a 5 MB image transfers less than 200 KiB and does not download its original");
            long cachedBefore = remote.ReceivedBytes;
            hidden.RootVisual = (Button)window.CreateFileVisual("attachment:" + imageFile.Id)!;
            await Eventually(() => Task.FromResult(((Button)hidden.RootVisual).Content is Image));
            Check(remote.ReceivedBytes - cachedBefore < 4096, "unchanged image reuses the thumbnail cache");
            Check(imageButton.ContextMenu!.Items.OfType<MenuItem>().Single().Header.ToString() == "另存为…", "image exposes a save-as context action");
            hidden.RootVisual = null;

            string localImage = Path.Combine(fixture, "拖拽图片.png"); File.Copy(picture, localImage);
            string localText = Path.Combine(fixture, "说明.txt"); await File.WriteAllTextAsync(localText, "isolated fixture");
            var drop = new DataObject(); drop.SetData(DataFormats.FileDrop, new[] { localImage, localText, localImage });
            Check((bool)Invoke("TryAttachData", drop)!, "file-drop data is recognized");
            await Field<Task>("attachmentPreparation");
            var attachments = Field<List<RemoteFile>>("attachedFiles");
            Check(attachments.Count == 2 && Find<WrapPanel>("AttachmentPreviews").Children.Count == 2, "mixed image/file drop stages previews and deduplicates paths");
            Check(adapter.Sent.Count == 0 && Find<TextBox>("Composer").Text == "", "dropping attachments neither sends a message nor inserts local paths into text");
            var workspace = await new FileTransferClient(remote.Peer).ListAsync("images");
            Check(workspace.Files.Count(f => f.Id.StartsWith("inbox/")) == 2 && workspace.Files.Any(f => f.Name == "拖拽图片.png"), "real upload preserves Unicode filename in the selected session");
            var clipboard = new DataObject(); clipboard.SetData(DataFormats.Bitmap, thumbnail);
            Check((bool)Invoke("TryAttachData", clipboard)!, "bitmap clipboard data is recognized"); await Field<Task>("attachmentPreparation");
            Check(attachments.Count == 3 && attachments.Last().Name.EndsWith(".png"), "clipboard bitmap becomes a PNG attachment");
            Check(!(bool)Invoke("TryAttachData", new DataObject(DataFormats.UnicodeText, "普通文本"))!, "ordinary text paste remains a TextBox operation");
            var bad = new DataObject(); bad.SetData(DataFormats.FileDrop, new[] { fixture }); Invoke("TryAttachData", bad); await Field<Task>("attachmentPreparation");
            Check(attachments.Count == 3 && Find<TextBlock>("StatusBar").Text.Contains("文件夹"), "directory drop is rejected without altering staged attachments");
            var overLimit = new DataObject(); overLimit.SetData(DataFormats.FileDrop, Enumerable.Range(0, 21).Select(i => Path.Combine(fixture, i + ".png")).ToArray());
            Invoke("TryAttachData", overLimit); await Field<Task>("attachmentPreparation");
            Check(attachments.Count == 3 && Find<TextBlock>("StatusBar").Text.Contains("20"), "all attachment entry points enforce the 20-file limit");
            Set("actionBusy", true); Invoke("TryAttachData", clipboard); Set("actionBusy", false);
            Check(attachments.Count == 3, "busy session cannot enqueue another clipboard upload");

            // Bind the already-verified RPC thumbnails to the offscreen layout fixture.
            // The hidden-HWND check above separately exercises the actual Loaded handler.
            foreach (var pair in Find<WrapPanel>("AttachmentPreviews").Children.OfType<StackPanel>().Zip(attachments))
                if (ImageThumbnail.Supported(pair.Second.Name))
                {
                    var bitmap = await (Task<BitmapSource>)Invoke("LoadThumbnailAsync", remote, "images", pair.Second.Id)!;
                    ((Button)pair.First.Children[0]).Content = new Image { Source = bitmap, MaxWidth = 320, MaxHeight = 240, Stretch = Stretch.Uniform };
                }
            foreach (var button in outputs.Children.OfType<WrapPanel>().SelectMany(p => p.Children.OfType<Button>()))
                button.Content = new Image { Source = thumbnail, MaxWidth = 320, MaxHeight = 240, Stretch = Stretch.Uniform };
            // Render the main surface without showing or activating a window.
            Find<TextBlock>("RemoteStatus").Text = "隔离测试设备 · 本地回环中继";
            Find<TextBlock>("StatusBar").Text = "图片与普通文件可拖入聊天区；附入后点击发送。";
            var surface = (FrameworkElement)window.Content; surface.Width = 1100; surface.Height = 800;
            surface.Measure(new Size(1100, 800)); surface.Arrange(new Rect(0, 0, 1100, 800)); surface.UpdateLayout();
            var rendered = new RenderTargetBitmap(1100, 800, 96, 96, PixelFormats.Pbgra32); rendered.Render(surface);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
            using (var file = File.Create(output)) encoder.Save(file);
            var remove = Descendants(Find<WrapPanel>("AttachmentPreviews")).OfType<Button>().First(b => b.Content?.ToString() == "×"); remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(attachments.Count == 2, "individual attachment can be removed without clearing the rest");
            Check(window.Control("shutdown").ToString()!.Contains("busy_or_unsent_draft"), "client update refuses to discard staged attachments");
            Find<TextBox>("Composer").Text = "查看这些附件";
            Invoke("Send_Click", Find<Button>("SendButton"), new RoutedEventArgs(Button.ClickEvent));
            await Eventually(() => Task.FromResult(adapter.Sent.Count == 1 && attachments.Count == 0));
            Check(adapter.Sent.Single().Contains("查看这些附件") && Find<WrapPanel>("AttachmentPreviews").Children.Count == 0, "explicit send submits the staged batch and clears its previews only after acknowledgement");
            Console.WriteLine($"ALL {passed} IMAGE UI CHECKS PASSED (isolated relay, no windows opened)");
        }
        finally
        {
            if (window is not null) { Invoke("DisposeTray"); Field<DispatcherTimer>("snapshotTimer").Stop(); }
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            // This generated fixture is the only directory removed by the check.
            if (Directory.Exists(fixture) && Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(fixture, true);
        }
    }
    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 150; i++) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException("Image fixture did not reach the expected state.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private sealed class ImageAdapter : IAgentAdapter
    {
        public List<string> Sent { get; } = [];
        public AgentInfo Info => new("hermes", "image fixture", true, "isolated", CanFiles: true, CanThumbnails: true);
        public event Action<SessionView>? Changed { add { } remove { } }
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new("images", "图片收发验证", "")]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) => Task.FromResult(new SessionView(id, "图片收发验证", "isolated fixture", "idle", null, [new("assistant", "生成完成：远端 图片.png。")], []));
        public Task SendAsync(string text, CancellationToken ct) { Sent.Add(text); return Task.CompletedTask; }
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, string decision, System.Text.Json.Nodes.JsonNode? response, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
