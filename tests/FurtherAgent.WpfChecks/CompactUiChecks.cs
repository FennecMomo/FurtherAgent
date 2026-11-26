using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class CompactUiChecks
{
    internal static void Run(string output)
    {
        var app = new App(); app.InitializeComponent();
        var main = new MainWindow();
        var type = typeof(MainWindow).Assembly.GetType("FurtherAgent.Client.MiniWindow")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pathA = Path.Combine(Path.GetTempPath(), "fa-mini-a-" + Guid.NewGuid() + ".json");
        string pathB = Path.Combine(Path.GetTempPath(), "fa-mini-b-" + Guid.NewGuid() + ".json");
        string draftA = "第一个会话草稿", draftB = "第二个会话草稿";
        const string formattedReply = """
            **回复 A**
            - [x] 完成
            ```cs
            var completed = true;
            ```
            | 项目 | 状态 |
            | --- | --- |
            | 渲染 | 完成 |
            [说明](https://example.test/docs)
            """;
        var viewA = new SessionView("a", "会话 A", "", "idle", null, [new("user", "# 问题 A"), new("tool", "不显示工具原文"), new("assistant", formattedReply)], []);
        var imageFixture = Directory.CreateTempSubdirectory("fa-mini-preview-");
        string imagePath = Path.Combine(imageFixture.FullName, "preview.png");
        var imageEncoder = new PngBitmapEncoder();
        imageEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray8, null, new byte[] { 0, 255, 255, 0 }, 2)));
        using (var imageFile = File.Create(imagePath)) imageEncoder.Save(imageFile);
        var preview = new RemoteFile("outbox/preview.png", "preview.png", new FileInfo(imagePath).Length);
        var imageCache = new DownloadedAttachments(imageFixture.FullName);
        const string imageDevice = "cached-fixture";
        string imageKey = DownloadedAttachments.Key(imageDevice, "b", preview);
        imageCache.Remember(imageKey, imagePath);
        var viewB = new SessionView("b", "会话 B", "", "idle", null,
            [new("assistant", "回复 B\n![示意图](attachment:preview.png)")], [], OutputFiles: [preview]);
        Window Create(string id, SessionView view, string path, Func<string> read, Action<string> write, int shortcutSlot) =>
            (Window)Activator.CreateInstance(type, "https://example.test", "fixture", id, view.Title, view, path, read, write, (Action)(() => { }), shortcutSlot, 0)!;
        var a = Create("a", viewA, pathA, () => draftA, text => draftA = text, 1);
        var recovery = Activator.CreateInstance(type.Assembly.GetType("FurtherAgent.Client.SubmissionRecovery")!, nonPublic: true)!;
        var b = (Window)Activator.CreateInstance(type, flags, null,
            ["https://example.test", "fixture", "b", viewB.Title, viewB, pathB, (Func<string>)(() => draftB),
                (Action<string>)(text => draftB = text), (Action)(() => { }), 10, 0, null, null, recovery, imageDevice, imageCache, null], null)!;
        T Field<T>(Window window, string name) => (T)type.GetField(name, flags)!.GetValue(window)!;
        string Transcript(Window window) => TranscriptText(Field<StackPanel>(window, "context"));
        void Call(Window window, string name, params object[] args) => type.GetMethod(name, flags | BindingFlags.Public)!.Invoke(window, args);
        void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        void Layout(Window window, double width, double height)
        {
            window.Measure(new Size(width, height)); window.Arrange(new Rect(100, 100, width, height)); window.UpdateLayout();
            var content = (FrameworkElement)window.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        }
        try
        {
            Layout(a, 420, 210); Layout(b, 420, 210);
            Check(Field<TextBlock>(a, "title").Text == "1 · 会话 A" && Field<TextBlock>(b, "title").Text == "0 · 会话 B",
                "expanded mini titles show their numeric shortcuts with zero as the tenth slot");
            Check(Field<TextBlock>(a, "statusLabel").Text == "网络连接中", "mini shows network connection progress before its own session attaches");
            Check(main.Width == 1240 && main.MinWidth == 940 && main.Content is Grid,
                "creating minis leaves main window size and content intact");
            Check(Transcript(a).Contains("回复 A") && !Transcript(a).Contains("工具原文") && Transcript(b).Contains("回复 B"),
                "each mini displays only its fixed session conversation");
            var messages = TranscriptMessages(Field<StackPanel>(a, "context")).ToArray();
            Check(messages.Length == 2 && messages[0].Document.Blocks.FirstBlock is Paragraph { FontSize: > 13 },
                "mini renders user headings and keeps conversation entries in separate documents");
            var blocks = messages[1].Document.Blocks.ToArray();
            var link = blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Hyperlink>().Single();
            Check(blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<Bold>().Any()) &&
                blocks.OfType<Paragraph>().Any(p => p.Tag is "list") &&
                blocks.OfType<Paragraph>().Any(p => p.FontFamily.Source == "Consolas") &&
                blocks.OfType<Table>().Single().RowGroups[0].Rows.Count == 2 &&
                link.NavigateUri.AbsoluteUri == "https://example.test/docs",
                "mini formats assistant emphasis, task lists, code, tables and links");
            var linkRect = link.Inlines.FirstInline!.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var linkPoint = new Point(linkRect.X + 2, linkRect.Y + linkRect.Height / 2);
            var hit = messages[1].GetPositionFromPoint(linkPoint, false)?.Parent;
            var isTranscriptActionSource = type.GetMethod("IsTranscriptActionSource", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check(hit is not null && link.IsEnabled && (bool)isTranscriptActionSource.Invoke(null, [hit])!,
                "the rendered link's text is recognized before starting a transcript drag");
            var imageAction = TranscriptMessages(Field<StackPanel>(b, "context")).Single().Document.Blocks.OfType<Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines).OfType<InlineUIContainer>().Single().Child;
            Check(imageAction is Button { Content: TextBlock { Text: "预览图片" }, IsEnabled: true },
                "a cached Mini image still appears only as a text preview button in the transcript");
            Check((bool)isTranscriptActionSource.Invoke(null, [((Button)imageAction).Content])!,
                "clicking the preview label is recognized as an action before starting a transcript drag");
            Check(Field<Dictionary<string, Window>>(b, "imagePreviews").Count == 0,
                "rendering the Mini transcript does not create a preview window");
            var firstDocument = messages[0].Document;
            Call(a, "Refresh", viewA with { Messages = [.. viewA.Messages.Take(2), new("assistant", formattedReply + "\n\n**实时尾部**")] });
            var updated = TranscriptMessages(Field<StackPanel>(a, "context")).ToArray();
            Check(ReferenceEquals(firstDocument, updated[0].Document) &&
                updated[1].Document.Blocks.LastBlock is Paragraph tail && tail.Inlines.OfType<Bold>().Any() &&
                Transcript(a).Contains("实时尾部"),
                "streaming renders the updated entry while preserving the earlier message document");
            Call(a, "Refresh", viewB);
            Check(!Transcript(a).Contains("回复 B"), "another session update cannot redirect a mini");
            var inputA = Field<TextBox>(a, "composer"); var inputB = Field<TextBox>(b, "composer");
            Check(inputA.Text == draftA && inputB.Text == draftB, "each session begins with its own draft");
            inputA.Text = "编辑 A";
            Check(draftA == "编辑 A" && draftB == "第二个会话草稿" && inputB.Text == draftB,
                "editing one mini never changes another session draft");
            Call(b, "SetDraft", "主窗口同步 B");
            Check(inputB.Text == "主窗口同步 B" && draftB == "第二个会话草稿", "main window can update mini draft without a write loop");
            Check(inputA.ActualHeight >= 48 && inputA.TransformToAncestor((Visual)a.Content).Transform(new Point(0, inputA.ActualHeight)).Y <= 210,
                "mini composer stays fully inside expanded window");
            var contextScroll = Field<ScrollViewer>(a, "scroll");
            var growing = viewA with { Messages = [new("user", new string('甲', 260)), new("assistant", new string('乙', 260))] };
            Call(a, "Refresh", growing); Layout(a, 420, 210);
            Check(contextScroll.ScrollableHeight > 0 && Math.Abs(contextScroll.VerticalOffset - contextScroll.ScrollableHeight) < 1,
                "new long messages remain visible above the composer after layout");
            Call(a, "Refresh", growing with { Messages = [.. growing.Messages, new("user", new string('丙', 260))] }); Layout(a, 420, 210);
            Check(Math.Abs(contextScroll.VerticalOffset - contextScroll.ScrollableHeight) < 1,
                "the newest user message remains visible without waiting for another update");
            contextScroll.ScrollToVerticalOffset(contextScroll.ScrollableHeight - 40); Layout(a, 420, 210);
            var continued = growing with { Messages = [.. growing.Messages, new("assistant", "新回复应该立即显示")] };
            Call(a, "Refresh", continued); Layout(a, 420, 210);
            Check(Transcript(a).Contains("新回复应该立即显示"),
                "a stale scroll offset does not hide the next live update");
            a.Height = 300; Layout(a, 420, 300); contextScroll.ScrollToEnd(); Layout(a, 420, 300);
            a.Height = 180; Layout(a, 420, 180);
            var afterResize = growing with { Messages = [.. growing.Messages, new("assistant", "缩小窗口之后的新回复")] };
            Call(a, "Refresh", afterResize); Layout(a, 420, 180);
            Check(Transcript(a).Contains("缩小窗口之后的新回复"),
                "viewport changes do not freeze new conversation content");
            var complete = viewA with { Messages = [new("user", "第一条用户消息"),
                new("assistant", "第二条"), new("assistant", "第三条"), new("assistant", "第四条"),
                new("assistant", "第五条"), new("assistant", "第六条"), new("assistant", "第七条"),
                new("assistant", "长回复开头" + new string('长', 30100) + "长回复结尾")] };
            Call(a, "Refresh", complete); Layout(a, 420, 180);
            string completeText = Transcript(a);
            Check(completeText.Contains("第一条用户消息") && completeText.Contains("长回复开头") && completeText.Contains("长回复结尾"),
                "mini does not cut conversation entries or long replies inside its latest snapshot");
            contextScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Layout(a, 420, 180);
            double readingOffset = contextScroll.VerticalOffset;
            Call(a, "Refresh", complete with { Messages = [.. complete.Messages, new("assistant", "**阅读中暂不跳到底部**")] });
            Layout(a, 420, 180);
            Check(Transcript(a) == completeText && Math.Abs(contextScroll.VerticalOffset - readingOffset) < 1,
                "user wheel reading remains anchored during live updates");
            contextScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -100000) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Layout(a, 420, 180);
            Check(Transcript(a).Contains("阅读中暂不跳到底部"),
                "returning to the bottom immediately reveals updates received while reading");
            a.Height = 180; Field<RowDefinition>(a, "contextRow").Height = new GridLength(500); Layout(a, 420, 180);
            Check(inputA.ActualHeight >= 48 && inputA.TransformToAncestor((Visual)a.Content).Transform(new Point(0, inputA.ActualHeight)).Y <= 180,
                "long context cannot push composer outside minimum window");
            Field<RowDefinition>(a, "contextRow").Height = new GridLength(1, GridUnitType.Star);
            a.Height = 210; a.Left = 120; a.Top = 140; Layout(a, 420, 210);
            var ball = Field<Border>(a, "ball");
            for (int i = 0; i < 10; i++)
            {
                Call(a, "ToggleContext"); Layout(a, 44, 44);
                Check(a.Width == 44 && a.Height == 44 && a.Left == 496 && a.Top == 306, "mini ball preserves its bottom-right corner");
                Check(a.AllowsTransparency && a.Background == Brushes.Transparent && Math.Abs(a.Opacity - 0.18) < 0.001,
                    "collapsed ball uses 18 percent opacity over a transparent window background");
                if (i == 0)
                {
                    Call(a, "Refresh", viewA with { Title = "更新后的标题", StatusDetail = "更新后的状态",
                        Messages = [.. viewA.Messages, new("assistant", "**展开后显示**")] });
                    Check(Field<TextBlock>(a, "title").Text == "1 · 更新后的标题", "session title refresh preserves the shortcut digit");
                    Check(!Transcript(a).Contains("展开后显示"), "collapsed mini defers Markdown rendering");
                    Check(ball.ToolTip is null, "refreshing the collapsed ball does not expose a session tooltip");
                    ball.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    Check(ball.Visibility == Visibility.Visible, "an uncaptured left button release cannot expand the ball");
                }
                Call(a, "ToggleContext"); Layout(a, 420, 210);
                if (i == 0) Check(Transcript(a).Contains("展开后显示"), "expanding a mini renders its latest messages");
                Check(a.Width == 420 && a.Height == 210 && a.Left == 120 && a.Top == 140, "mini expansion returns to exact bounds");
                Check(a.Background == Brushes.White && Math.Abs(a.Opacity - 1) < 0.001, "expanded mini returns to an opaque window");
            }
            Check(b.Width == 420 && b.Height == 210 && Transcript(b).Contains("回复 B"),
                "collapsing one mini does not alter another mini");
            var bitmap = new RenderTargetBitmap(420, 210, 96, 96, PixelFormats.Pbgra32); bitmap.Render((Visual)a.Content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(output)) png.Save(file);
            Call(a, "SavePosition");
            Check(File.Exists(pathA) && !File.Exists(pathB), "each mini persists only its own position");
            CheckFloatingImage(app, b, (Button)imageAction, Field<Dictionary<string, Window>>(b, "imagePreviews"), Check);
        }
        finally
        {
            a.Close(); b.Close(); typeof(MainWindow).GetMethod("DisposeTray", flags)!.Invoke(main, null); app.Shutdown();
            if (File.Exists(pathA)) File.Delete(pathA);
            if (File.Exists(pathB)) File.Delete(pathB);
            File.Delete(imagePath);
            File.Delete(Path.Combine(imageFixture.FullName, imageKey + ".json"));
            imageFixture.Delete();
        }
    }

    private static void CheckFloatingImage(App app, Window owner, Button button, Dictionary<string, Window> previews, Action<bool, string> check)
    {
        var offscreenStyle = new Style(typeof(Window));
        offscreenStyle.Setters.Add(new Setter(Window.WindowStyleProperty, WindowStyle.None));
        offscreenStyle.Setters.Add(new Setter(Window.AllowsTransparencyProperty, true));
        offscreenStyle.Setters.Add(new Setter(Window.OpacityProperty, 0d));
        offscreenStyle.Setters.Add(new Setter(Window.ShowActivatedProperty, false));
        app.Resources[typeof(Window)] = offscreenStyle;
        try
        {
            _ = new WindowInteropHelper(owner).EnsureHandle();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var preview = previews.Values.Single();
            check(preview.Owner == owner && preview.IsVisible && !owner.IsVisible,
                "clicking the text opens a separate modeless preview window");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (preview.Content is not Image && DateTime.UtcNow < deadline)
            {
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(10);
            }
            check(preview.Content is Image { Source: BitmapSource { PixelWidth: 2, PixelHeight: 2 } },
                "the preview displays the existing attachment cache without a session connection");
            preview.Close();
            check(previews.Count == 0, "the preview closes independently of the Mini window");
        }
        finally { app.Resources.Remove(typeof(Window)); }
    }

    private static IEnumerable<MarkdownView> TranscriptMessages(StackPanel context) =>
        context.Children.OfType<DockPanel>().SelectMany(message => message.Children.OfType<MarkdownView>());

    internal static string TranscriptText(StackPanel context) =>
        string.Join("\n", TranscriptMessages(context).Select(message =>
            new TextRange(message.Document.ContentStart, message.Document.ContentEnd).Text));
}
