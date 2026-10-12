using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class ThemePreview
{
    internal static void Run(string repo, string output)
    {
        Directory.CreateDirectory(output);
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(Path.Combine(repo, "src/FurtherAgent.Client/MainWindow.xaml"));
        var root = source.Root!; root.Attribute(x + "Class")!.Remove();
        root.SetAttributeValue("xmlns", w.NamespaceName);
        root.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:FurtherAgent.Client;assembly=FurtherAgent.Client");
        foreach (var element in root.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:FurtherAgent.Client"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:FurtherAgent.Client;assembly=FurtherAgent.Client");
        foreach (var a in root.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "Closing" or "Click" or "TextChanged" or "SelectionChanged" or "SelectedItemChanged" or "MouseDoubleClick" or "PreviewKeyDown").ToArray()) a.Remove();
        root.Attribute("Icon")?.Remove();
        foreach (var image in root.Descendants(w + "Image")) image.SetAttributeValue("Source", "pack://application:,,,/FurtherAgent.Client;component/" + (string?)image.Attribute("Source"));
        var resources = XDocument.Load(Path.Combine(repo, "src/FurtherAgent.Client/App.xaml")).Root!.Element(w + "Application.Resources")!;
        root.Element(w + "Window.Resources")!.AddFirst(resources.Elements().Select(e => new XElement(e)));
        var window = (Window)XamlReader.Parse(source.ToString());
        T Find<T>(string name) where T : class => (window.FindName(name) as T) ?? throw new Exception("Missing " + name);
        VisualTheme.Apply(window, VisualTheme.Codex);
        Find<StackPanel>("EmptyState").Visibility = Visibility.Collapsed;
        Find<TextBlock>("SessionTitle").Text = "拉取最新更新";
        Find<TextBlock>("SessionMeta").Text = @"E:\etc\FurtherAgent";
        Find<TextBlock>("RemoteStatus").Text = "MOMO · Codex";
        Find<TextBlock>("VersionLabel").Text = "0.12.0 · 远程工作空间";
        Find<TextBlock>("StatusBar").Text = "";
        Find<TextBlock>("ActivityStatus").Text = "已连接 · 等待指令";
        Find<Button>("NewButton").IsEnabled = true;
        Find<Button>("WorkspaceButton").Visibility = Visibility.Visible;
        Find<Button>("RefreshButton").IsEnabled = true;
        foreach (string name in new[] { "SendButton", "ModelButton", "UploadButton", "FilesButton" }) Find<Button>(name).IsEnabled = true;
        Find<Button>("ModelButton").Content = "GPT-6 Astra  中  ⌄";
        Find<TextBox>("Composer").IsEnabled = true;
        Find<ItemsControl>("Messages").ItemsSource = new[]
        {
            new ChatEntry("assistant", "已核对远端 `main` 与本地 HEAD，同步一致。未跟踪的 `反馈文件/` 保留在本地，没有提交或上传。"),
            new ChatEntry("user", "你看你桌面端UI设计方面能不能照抄一下Codex？最好伪装成codex。现在的ui也可以保留作为原始UI风格"),
            new ChatEntry("assistant", "可以，做成**可切换的两套界面风格**：\n\n- **Codex 风格**：尽量复刻侧栏、工作空间与会话列表、顶部栏、消息区、底部输入框，以及字体、间距、配色、圆角和按钮图标。\n- **原始风格**：保留现在的界面，在设置里随时切回。记住选择，重启后继续使用。\n\n这次会调整布局和控件外观，不只是换颜色。现有的多连接、工作空间分组、附件、模型设置等功能都保留。\n\n**按你这张截图做。** 两套风格共用连接、会话和输入框，切换不会清空草稿或附件。")
        };
        var sessions = new[] { new SessionInfo("one", "拉取最新更新", "2026-10-12", @"E:\etc\FurtherAgent"), new SessionInfo("two", "分析并整理两个仓库触点", "2026-10-11", @"E:\etc\FurtherAgent"), new SessionInfo("three", "初始化 FurtherAgent 仓库", "2026-10-10", @"E:\etc\FurtherAgent"), new SessionInfo("four", "调整右臂右腿分组命名", "2026-10-09", @"E:\UnityProjects\AssemblyTrainingFramework") };
        var workspaces = new[] { new WorkspaceInfo("a", "FurtherAgent", @"E:\etc\FurtherAgent"), new WorkspaceInfo("b", "AssemblyTrainingFramework", @"E:\UnityProjects\AssemblyTrainingFramework"), new WorkspaceInfo("c", "DesktopHelper", @"E:\etc\DesktopHelper"), new WorkspaceInfo("d", "WorkManager", @"E:\etc\WorkManager"), new WorkspaceInfo("e", "RobotAssembly", @"E:\UnityProjects\RobotAssembly"), new WorkspaceInfo("f", "ResourceManager", @"E:\etc\ResourceManager") };
        Find<TreeView>("SessionList").ItemsSource = WorkspaceGroup.Build(sessions, workspaces, "", "one");
        void Capture(string name, double width, double height)
        {
            var content = (FrameworkElement)window.Content; content.Width = width; content.Height = height;
            content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        Capture("codex", 1214, 984);
        Console.WriteLine($"Font sizes: window={window.FontSize}, tree={Find<TreeView>("SessionList").FontSize}, new={Find<Button>("NewButton").FontSize}, composer={Find<TextBox>("Composer").FontSize}");
        var composer = Find<TextBox>("Composer"); composer.Text = "未发送的草稿";
        var messages = Find<ItemsControl>("Messages").ItemsSource;
        var tools = Find<WrapPanel>("ConversationTools");
        VisualTheme.Apply(window, VisualTheme.Original);
        if (composer.Text != "未发送的草稿" || !ReferenceEquals(messages, Find<ItemsControl>("Messages").ItemsSource) || !Find<StackPanel>("ComposerStack").Children.Contains(tools)) throw new Exception("Original theme lost state or toolbar");
        Capture("original", 1214, 984);
        VisualTheme.Apply(window, VisualTheme.Codex);
        if (composer.Text != "未发送的草稿" || !ReferenceEquals(Find<ContentControl>("FooterToolsHost").Content, tools)) throw new Exception("Codex theme lost state or toolbar");
        Capture("codex-narrow", 940, 650);
        Find<Button>("ModelButton").Content = "Build · a-long-custom-model-name";
        Find<Button>("StopButton").IsEnabled = Find<Button>("InterruptSendButton").IsEnabled = true;
        Find<ComboBox>("DeliveryPicker").Visibility = Visibility.Visible;
        Capture("codex-narrow-running", 940, 650);
        Find<Button>("StopButton").IsEnabled = Find<Button>("InterruptSendButton").IsEnabled = false;
        Find<ComboBox>("DeliveryPicker").Visibility = Visibility.Collapsed;
        Find<ComboBox>("ThemePicker").SelectedIndex = 0;
        Find<Grid>("ConversationPage").Visibility = Visibility.Collapsed; Find<ScrollViewer>("SettingsPage").Visibility = Visibility.Visible;
        Capture("codex-settings", 1214, 984);
        Find<Border>("ModelOverlay").Visibility = Visibility.Visible;
        var model = Find<ComboBox>("ModelPicker");
        model.ItemsSource = new[] { new { Id = "gpt-6-astra" }, new { Id = "gpt-6-sol" } };
        model.SelectedIndex = 0;
        Capture("codex-model", 1214, 984);
        var editor = (TextBox)model.Template.FindName("PART_EditableTextBox", model);
        editor.Text = "custom-model";
        if (model.Text != "custom-model") throw new Exception("Custom model input did not propagate from editable template");
        VisualTheme.Apply(window, VisualTheme.Original);
        window.UpdateLayout();
        VisualTheme.Apply(window, VisualTheme.Codex);
        window.UpdateLayout();
        if (model.Text != "custom-model") throw new Exception("Custom model input lost during theme switch");
        model.SelectedIndex = 1;
        if (model.Text != "gpt-6-sol") throw new Exception("Model selection did not replace custom input");
        Console.WriteLine("PASS: editable model picker accepts unlisted model IDs and retains text across both themes");
        Console.WriteLine("PASS: actual window XAML renders both themes and narrow layout; switching retains composer, messages and attachment toolbar instances");
        window.Close();
    }
}
