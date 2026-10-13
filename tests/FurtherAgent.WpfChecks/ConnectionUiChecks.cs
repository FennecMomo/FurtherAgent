using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class ConnectionUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(string output)
    {
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow();
        object? pool = null;
        void Invoke(string name, params object[] values) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, values);
        T Find<T>(string name) => (T)window.FindName(name);
        void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        void Require(bool passed, string message) { if (!passed) throw new Exception(message); Console.WriteLine("PASS: " + message); }
        try
        {
            var contextType = typeof(MainWindow).GetNestedType("DeviceContext", BindingFlags.NonPublic)!;
            pool = typeof(MainWindow).GetField("deviceConnections", Private)!.GetValue(window)!;
            object Device(string code, string name)
            {
                var context = Activator.CreateInstance(contextType, "http://127.0.0.1:1", code)!;
                Set(context, "Name", name); Set(context, "Agent", new AgentInfo("fixture", name, true, "isolated UI fixture"));
                Set(context, "SelectedId", "same-id"); Set(context, "View", new SessionView("same-id", name, name, "active", "run", [new("assistant", name + " reply")], []));
                Set(context, "Sessions", new List<SessionInfo> { new("same-id", name, "") });
                typeof(ConnectionUiChecks).GetMethod(nameof(Add), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(contextType).Invoke(null, [pool, context, code]);
                return context;
            }
            var a = Device("AAAA1111", "Codex · 办公电脑"); var b = Device("BBBB2222", "Hermes · 服务器");
            Invoke("ActivateDevice", a);
            Find<TextBox>("Composer").Text = "A 的未发送草稿";
            var attachments = (List<RemoteFile>)typeof(MainWindow).GetField("attachedFiles", Private)!.GetValue(window)!;
            attachments.Add(new("a-file", "A-only.txt", 10));
            Invoke("ActivateDevice", b);
            Require(Find<TextBox>("Composer").Text == "" && !Find<TextBlock>("AttachmentSummary").Text.Contains("A-only"), "actual desktop switch isolates drafts and attachments with identical session IDs");
            Find<TextBox>("Composer").Text = "B 的未发送草稿";
            Set(a, "View", new SessionView("same-id", "A 更新", "A", "completed", "run", [new("assistant", "A background result")], []));
            Invoke("ActivateDevice", a);
            Require(Find<TextBox>("Composer").Text == "A 的未发送草稿" && Find<TextBlock>("AttachmentSummary").Text.Contains("A-only.txt")
                && Find<ItemsControl>("Messages").ItemsSource.Cast<ChatEntry>().Single().Text == "A background result", "actual desktop switch restores retained draft, attachment and background result");
            Require(Find<ComboBox>("ConnectedDevices").Items.Count == 2 && Find<ComboBox>("ConnectedDevices").Visibility == Visibility.Visible,
                "desktop keeps a visible selector for both retained devices");
            Set(window, "actionBusy", true);
            Find<ComboBox>("ConnectedDevices").SelectedItem = b;
            Require(ReferenceEquals(Find<ComboBox>("ConnectedDevices").SelectedItem, a), "desktop refuses to redirect device selection while an operation awaits acknowledgement");
            Set(window, "actionBusy", false);
            VisualTheme.Apply(window, VisualTheme.Original); VisualTheme.Apply(window, VisualTheme.Codex);
            Require(Find<TextBox>("Composer").Text == "A 的未发送草稿", "theme changes preserve the selected device draft");
            var content = (FrameworkElement)window.Content;
            content.Width = 1100; content.Height = 760;
            content.Measure(new Size(1100, 760)); content.Arrange(new Rect(0, 0, 1100, 760)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1100, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            using var file = File.Create(output); encoder.Save(file);
        }
        finally
        {
            if (pool is IAsyncDisposable resources) resources.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Invoke("DisposeTray"); app.Shutdown();
        }
    }
    private static void Add<T>(DeviceConnectionPool<T> pool, T connection, string code) where T : class, IAsyncDisposable =>
        pool.OpenAsync("http://127.0.0.1:1", code, () => Task.FromResult(connection)).GetAwaiter().GetResult();
}
