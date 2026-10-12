using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class QuestionUiChecks
{
    internal static void Run(string output)
    {
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow();
        try
        {
            var payload = JsonNode.Parse("""{"questions":[{"id":"test-question","question":"Question 链路测试：你能看到并选择下面的选项吗？","options":[{"label":"链路测试通过"},{"label":"需要调整"}]}]}""");
            typeof(MainWindow).GetMethod("RenderPrompts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                new object[] { new List<PendingPrompt> { new("test", CodexQuestions.AsyncKind, "测试", "", payload) } });
            var panel = (StackPanel)window.FindName("PromptPanel");
            var choice = panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().First();
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var answer = panel.Children.OfType<TextBox>().Single();
            if (answer.Text != "链路测试通过") throw new Exception("Question option click failed");
            answer.Text = "自定义回答";
            VisualTheme.Apply(window, VisualTheme.Original); VisualTheme.Apply(window, VisualTheme.Codex);
            if (answer.Text != "自定义回答") throw new Exception("Theme switch lost question draft");
            var content = (FrameworkElement)window.Content;
            content.Width = 1100; content.Height = 760;
            content.Measure(new Size(1100, 760)); content.Arrange(new Rect(0, 0, 1100, 760)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1100, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(output); encoder.Save(file);
            Console.WriteLine("PASS: actual desktop question panel displays choices, option click fills answer, theme switch preserves custom answer");
        }
        finally { typeof(MainWindow).GetMethod("DisposeTray", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); app.Shutdown(); }
    }
}
