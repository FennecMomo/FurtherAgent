using System.Windows.Documents;
using FurtherAgent.Client;
using FurtherAgent.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

static class Program
{
    static int passed;
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--hotkey")) { HotkeyUiChecks.Run(); return; }
        if (args.Length == 2 && args[0] == "--compact") { CompactUiChecks.Run(args[1]); return; }
        if (args.Contains("--interactions")) { InteractionUiChecks.Run(""); return; }
        if (args.Length == 2 && args[0] == "--history") { HistoryUiChecks.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--completion") { CompletionUiChecks.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--connections") { ConnectionUiChecks.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--questions") { QuestionUiChecks.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--settings") { SettingsUiChecks.Run(args[1]); return; }
        if (args.Length == 3 && args[0] == "--themes") { ThemePreview.Run(args[1], args[2]); return; }
        var groupSessions = new[] { new SessionInfo("old", "old", "2026-10-10", @"C:\Projects\demo"), new SessionInfo("new", "new", "2026-10-12", "c:/projects/demo/"), new SessionInfo("other", "other", "2026-10-11", @"D:\Projects\demo") };
        var groupWorkspaces = new[] { new WorkspaceInfo("empty", "空工作空间", @"E:\empty") };
        var groups = WorkspaceGroup.Build(groupSessions, groupWorkspaces, "", "new");
        Require(groups.Count == 3 && groups[0].Sessions.Count == 2 && groups[0].Sessions[0].Info.Id == "new", "workspace grouping normalizes Windows paths and sorts by latest session");
        Require(groups.Count(g => g.Name.Equals("demo", StringComparison.OrdinalIgnoreCase)) == 2 && groups.Last().Sessions.Count == 0, "same names on different drives stay separate; empty workspaces stay visible");
        groups[0].IsExpanded = false;
        var refreshedGroups = WorkspaceGroup.Build(groupSessions, groupWorkspaces, "", "new", groups);
        Require(!refreshedGroups[0].IsExpanded && refreshedGroups[0].Sessions[0].IsSelected, "refresh retains collapsed groups and selected conversation");
        Require(WorkspaceGroup.Build(groupSessions, groupWorkspaces, @"D:\Projects", null).Single().Sessions.Single().Info.Id == "other", "workspace path search filters groups");
        Require(WorkspaceGroup.Build(groupSessions, groupWorkspaces, "new", null, groups).Single() is { IsExpanded: true, Sessions.Count: 1 }, "title search expands matching group and excludes unrelated conversations");
        var view = new MarkdownView { Markdown = "# 标题\n**粗体** 和 `代码`\n> 引用\n- 列表\n```cs\nvar x = 1;\n```\n| A | B |\n|---|---|\n| 一 | 二 |\n[文档](https://example.com)\n<script>alert(1)</script>\n![图片](file:///secret)\n[危险](javascript:alert(1))" };
        var blocks = view.Document.Blocks.ToArray();
        Require(blocks.OfType<Table>().Single().RowGroups[0].Rows.Count == 2, "table rows rendered natively");
        Require(blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<Bold>().Any()), "bold inline rendered");
        Require(blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>()).Count() == 1, "only HTTP link activated; local/script URI stays text");
        var text = new TextRange(view.Document.ContentStart, view.Document.ContentEnd).Text;
        Require(text.Contains("alert(1)") && text.Contains("var x = 1;") && text.Contains("标题"), "code and untrusted HTML preserved as selectable text");
        Require(!blocks.OfType<BlockUIContainer>().Any(), "no remote image or embedded HTML controls");
        int thumbnails=0, originals=0;
        var withImage = new MarkdownView { AttachmentFactory = reference =>
        {
            if(reference!="attachment:plot.png") return null;
            thumbnails++; var imageButton=new Button { Content="compressed thumbnail" };
            imageButton.Click+=(_,_)=>originals++;
            return imageButton;
        }, Markdown="图片之前 ![图](attachment:plot.png) 图片之后\n![外部](https://example.com/image.png)\n`![代码](attachment:plot.png)`" };
        var inline=withImage.Document.Blocks.OfType<Paragraph>().First().Inlines.ToArray();
        Require(inline[0] is Run { Text:"图片之前 " } && inline[1] is InlineUIContainer && inline[2] is Run { Text:" 图片之后" }, "thumbnail occupies image position inside message text");
        Require(thumbnails==1 && originals==0,"rendering thumbnail neither requests original nor activates external/code images");
        ((Button)((InlineUIContainer)inline[1]).Child).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(originals==1,"only image click requests original");
        var tool = new ChatMessageView { DataContext = new ChatEntry("tool", new string('x', 18042), "raw") };
        tool.Measure(new Size(600, double.PositiveInfinity));
        Require(tool.Child is TextBlock { Text: "执行了一项操作" } && tool.DesiredSize.Height < 80, "18K tool output is replaced by a compact summary");
        var reasoning = new ChatMessageView { DataContext = new ChatEntry("reasoning.available", "reply-like text", "raw") };
        Require(reasoning.Child is TextBlock { Text: "分析任务与规划步骤" }, "reasoning never renders as a second ordinary reply");
        var user = new ChatMessageView { DataContext = new ChatEntry("user", "你好") };
        var assistant = new ChatMessageView { DataContext = new ChatEntry("assistant", "你好") };
        Require(user.HorizontalAlignment == HorizontalAlignment.Right && assistant.HorizontalAlignment == HorizontalAlignment.Stretch && user.Background.ToString() != assistant.Background.ToString(), "user and assistant alignment and backgrounds differ");
        string footer = "\n\n[FurtherAgent 文件工作区]\n如需向用户交付文件，请把最终文件写入以下输出目录的顶层（普通文件，不使用符号链接，每文件最多 100 MiB），并在回复中告知文件名：\n\"/home/ubuntu/files/" + new string('a', 64) + "/outbox\"";
        Require(ChatPresentation.Display(new("user", "你好" + footer)).Text == "你好", "generated workspace footer hidden without changing original user text");
        Require(ChatPresentation.Display(new("user", "解释\n\n[FurtherAgent 文件工作区]\n示例")).Text.EndsWith("示例"), "user-authored marker alone is preserved");
        Require(ChatPresentation.Display(new("assistant", "回复" + footer)).Text.EndsWith(footer), "assistant content never stripped as user metadata");
        var formatted = new ChatMessageView { DataContext = new ChatEntry("assistant", "# 结果\n__完成__\n- [x] 验收\n1. 下一步\n~~旧内容~~") };
        var markdown = ((StackPanel)formatted.Child).Children.OfType<MarkdownView>().Single();
        var formattedText = new TextRange(markdown.Document.ContentStart, markdown.Document.ContentEnd).Text;
        Require(formattedText.Contains("☑ 验收") && !formattedText.Contains("__") && !formattedText.Contains("~~") && markdown.Document.Blocks.OfType<Paragraph>().First().FontSize > 13,
            "actual assistant bubble renders headings, emphasis, task lists and strikethrough as Markdown");
        string stateFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fa-state-" + Guid.NewGuid().ToString("N"), "connection.json");
        try
        {
            var store = new SavedConnectionStore(stateFile);
            store.Save(new("https://relay.example", "test-only-pairing-code", "test device", true));
            Require(!System.IO.File.ReadAllText(stateFile).Contains("test-only-pairing-code"), "saved authorization code is encrypted on disk");
            var oldFormat = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(stateFile))!.AsArray()[0]!.ToJsonString();
            System.IO.File.WriteAllText(stateFile, oldFormat);
            var restored = new SavedConnectionStore(stateFile).Load("https://relay.example/")!;
            Require(restored.Code == "test-only-pairing-code" && restored.AutoConnect && restored.Name == "test device", "new process store restores canonical endpoint and encrypted connection");
            Require(store.Load("https://different.example") is null, "saved credentials are never restored for another server");
            store.Save(restored with { AutoConnect = false });
            Require(store.Load(restored.Server) is { AutoConnect: false, Code: "test-only-pairing-code" }, "manual disconnect preference persists without forgetting code");
            store.Save(new(restored.Server, "second-test-code", "second device", true));
            Require(store.List().Count == 2 && store.Load(restored.Server)?.Code == "second-test-code", "multiple desktop connections on the same relay persist");
            store.Forget(store.List().Single(p => p.Code == "second-test-code"));
            Require(store.List().Single().Code == restored.Code, "desktop removes only selected connection");
            store.Forget(); Require(store.Load(restored.Server) is null, "forget removes persistent connection");
        }
        finally { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(stateFile)!, true); }
        Console.WriteLine($"ALL {passed} WPF AND PERSISTENCE CHECKS PASSED (no windows opened)");
        if (args.Length == 2) Preview(args[0], args[1]);
    }
    static void Preview(string repo, string output)
    {
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = XDocument.Load(System.IO.Path.Combine(repo, "src/FurtherAgent.Client/App.xaml")).Root!.Element(w + "Application.Resources")!;
        var dictionary = new XElement(w + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x), resources.Elements());
        var panel = new StackPanel { Margin = new Thickness(24), Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString()) };
        panel.Children.Add(new TextBlock { Text = "会话展示", FontSize = 23, Margin = new Thickness(0, 0, 0, 24) });
        var source = XDocument.Load(System.IO.Path.Combine(repo, "src/FurtherAgent.Client/MainWindow.xaml"));
        var toolbar = new XElement(source.Descendants(w + "WrapPanel").Single(e => (string?)e.Attribute(x + "Name") == "ConversationTools"));
        toolbar.SetAttributeValue(XNamespace.Xmlns + "x", x);
        toolbar.AddFirst(new XElement(w + "WrapPanel.Resources", new XElement(dictionary)));
        foreach (var a in toolbar.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName == "Click").ToArray()) a.Remove();
        panel.Children.Add(new ChatMessageView { DataContext = new ChatEntry("user", "你好") });
        panel.Children.Add(new ChatMessageView { DataContext = new ChatEntry("tool", new string('x', 18042), "tool result") });
        panel.Children.Add(new ChatMessageView { DataContext = new ChatEntry("reasoning.available", "本轮思考记录，可按需展开查看。", "details") });
        panel.Children.Add(new ChatMessageView { DataContext = new ChatEntry("assistant", "你好！消息与工具记录现在分开展示。\n\n**正式回复**保留 Markdown，工具详情默认折叠。") });
        panel.Children.Add((UIElement)XamlReader.Parse(toolbar.ToString()));
        var composer = new XElement(source.Descendants(w + "TextBox").Single(e => (string?)e.Attribute(x + "Name") == "Composer").Ancestors(w + "Border").First());
        composer.SetAttributeValue(XNamespace.Xmlns + "x", x);
        var composerResources = new XElement(dictionary);
        XNamespace local = "clr-namespace:FurtherAgent.Client;assembly=FurtherAgent.Client";
        composerResources.Add(new XElement(local + "HalfFontMarginConverter", new XAttribute(x + "Key", "HalfFontMargin")));
        composer.AddFirst(new XElement(w + "Border.Resources", composerResources));
        foreach (var a in composer.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "Click" or "PreviewKeyDown").ToArray()) a.Remove();
        var composerView = (FrameworkElement)XamlReader.Parse(composer.ToString());
        var input = (TextBox)composerView.FindName("Composer");
        input.IsEnabled = true; input.FontSize = 13; input.Text = "从这里开始输入，文字贴左上方。\n第二行也保持左对齐。";
        panel.Children.Add(composerView);
        var health = new XElement(source.Descendants(w + "TextBlock").Single(e => (string?)e.Attribute(x + "Name") == "RelayHealth").Ancestors(w + "StackPanel").First());
        health.SetAttributeValue(XNamespace.Xmlns + "x", x);
        var healthView = (FrameworkElement)XamlReader.Parse(health.ToString());
        ((TextBlock)healthView.FindName("RelayHealth")).Text = "中继：在线 · 85 ms · 1 秒前确认";
        ((TextBlock)healthView.FindName("HostHealth")).Text = "客户端 B：在线 · 130 ms · 1 秒前确认";
        ((TextBlock)healthView.FindName("ActivityStatus")).Text = "Agent 正在执行 / 生成回复…";
        panel.Children.Add(healthView);
        var root = new Border { Background = Brushes.WhiteSmoke, Child = panel, Width = 1000, Height = 650 };
        root.Measure(new Size(1000, 650)); root.Arrange(new Rect(0, 0, 1000, 650)); root.UpdateLayout();
        Require(input.GetRectFromCharacterIndex(0).Top < 6 && input.GetRectFromCharacterIndex(0).Left < 6,
            "actual first character is at top-left, not vertically centered");
        Require(input.Margin == new Thickness(6.5), "composer margin is half of its 13 DIP font size on all sides");
        var bitmap = new RenderTargetBitmap(1000, 650, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(output); encoder.Save(file);
    }
    static void Require(bool success, string label) { if (!success) throw new Exception(label); passed++; Console.WriteLine("PASS: " + label); }
}
