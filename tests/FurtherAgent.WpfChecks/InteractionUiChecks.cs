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

internal static class InteractionUiChecks
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
            Require(ready, "isolated interaction relay starts");
            using var registry = new RegistryClient(url);
            var adapter = new InteractionAdapter();
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
            async Task Until(Func<bool> condition)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!condition()) await Task.Delay(20, deadline.Token);
            }
            var composer = Find<TextBox>("Composer");
            var drafts = (Dictionary<string,string>)typeof(MainWindow).GetField("drafts", Private)!.GetValue(window)!;
            bool Busy() => (bool)typeof(MainWindow).GetField("actionBusy", Private)!.GetValue(window)!;
            composer.Text = "已发送内容"; drafts["history"] = composer.Text;
            Invoke("Send_Click", window, new RoutedEventArgs());
            await Until(() => adapter.Sends == 1);
            adapter.Acknowledgement.SetResult();
            await Until(() => !Busy());
            Require(composer.Text == "" && drafts["history"] == "", "successful send clears both visible text and saved session draft");

            adapter.Acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            composer.Text = "第二条";
            Invoke("Send_Click", window, new RoutedEventArgs());
            await Until(() => adapter.Sends == 2);
            composer.Text = "后来编辑的新草稿"; drafts["history"] = composer.Text;
            adapter.Acknowledgement.SetResult();
            await Until(() => !Busy());
            Require(composer.Text == "后来编辑的新草稿" && drafts["history"] == composer.Text, "late acknowledgement preserves newly edited text and saved draft");

            adapter.Acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            composer.Text = "第三条"; drafts["history"] = composer.Text;
            Invoke("Send_Click", window, new RoutedEventArgs());
            await Until(() => adapter.Sends == 3);
            Set("selectedId", "another"); composer.Text = "第三条";
            adapter.Acknowledgement.SetResult();
            await Until(() => !Busy());
            Require(composer.Text == "第三条" && drafts["history"] == "", "late acknowledgement cannot erase an identical draft in a different session");

            Set("selectedId", "history"); Invoke("Render", latest);
            adapter.Acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            composer.Text = "失败时保留的草稿"; drafts["history"] = composer.Text;
            Invoke("Send_Click", window, new RoutedEventArgs());
            await Until(() => adapter.Sends == 4);
            adapter.Acknowledgement.SetException(new IOException("fixture rejection"));
            await Until(() => !Busy());
            Require(composer.Text == "失败时保留的草稿" && drafts["history"] == composer.Text, "failed submission retains visible and saved draft without retrying");
            adapter.Acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            composer.Text = "先超时后执行"; drafts["history"] = composer.Text;
            Invoke("Send_Click", window, new RoutedEventArgs());
            await Until(() => adapter.Sends == 5);
            adapter.Acknowledgement.SetException(CodexSubmissionReceipt.RequestError("thread-follower-start-turn-timeout"));
            await Task.Delay(200);
            Require(Busy() && composer.Text == "先超时后执行", "native timeout waits for original receipt without presenting rejection");
            adapter.Accepted = true;
            await Until(() => !Busy());
            Require(composer.Text == "" && drafts["history"] == "" && adapter.Sends == 5
                && Find<TextBlock>("StatusBar").Text.Contains("接收"), "late native receipt clears draft and reports success without duplicate submission");
            adapter.Accepted = false;
            adapter.Acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            composer.Text = "超时前的消息";
            Invoke("Send_Click", window, new RoutedEventArgs());
            await Until(() => adapter.Sends == 6);
            adapter.Acknowledgement.SetException(new TimeoutException());
            await Task.Delay(150);
            composer.Text = "超时后新写的草稿"; drafts["history"] = composer.Text;
            adapter.Accepted = true;
            await Until(() => !Busy());
            Require(composer.Text == "超时后新写的草稿" && drafts["history"] == composer.Text,
                "late receipt after native timeout preserves newly edited draft");
            var settings = Json.Read<AgentSettings>(await remote.Peer.CallAsync("settings", null));
            Invoke("ApplyModelSettings", settings, "history");
            Find<ComboBox>("PermissionPicker").SelectedIndex = 0;
            Invoke("ApplyPermissions_Click", window, new RoutedEventArgs());
            await Until(() => adapter.Permission == ":read-only" && !Busy());
            Require(adapter.Permission == ":read-only", "actual WPF permission action traverses relay and adapter capability");
            Require(adapter.Model == "model-a", "permission action does not invoke or overwrite model configuration");
        }
        finally
        {
            if (pool is IAsyncDisposable resources) await resources.DisposeAsync();
            if (window is not null) typeof(MainWindow).GetMethod("DisposeTray", Private)!.Invoke(window, null);
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
        }
    }
    private sealed class InteractionAdapter : IAgentAdapter
    {
        public string Permission = ":workspace", Model = "model-a";
        public int Sends;
        public volatile bool Accepted;
        public TaskCompletionSource Acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentInfo Info => new("codex", "interaction fixture", true, "isolated fixture", CanConfigure:true);
        public event Action<SessionView>? Changed { add { } remove { } }
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new("history", "发送验证", "")]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) => Task.FromResult(new SessionView(id,"发送验证","fixture","idle",null,[],[]));
        public Task<AgentSettings> SettingsAsync(CancellationToken ct) => Task.FromResult(new AgentSettings(Model,"openai","default",[],false,false,"fixture", Permission:Permission, CanChangePermissions:true));
        public Task ConfigurePermissionsAsync(string permission, CancellationToken ct) { Permission = permission; return Task.CompletedTask; }
        public async Task SendAsync(string text,CancellationToken ct) { Interlocked.Increment(ref Sends); await CodexSubmissionReceipt.WaitAsync(Acknowledgement.Task, () => Accepted, ct); }
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id,string decision,System.Text.Json.Nodes.JsonNode? response,CancellationToken ct)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
