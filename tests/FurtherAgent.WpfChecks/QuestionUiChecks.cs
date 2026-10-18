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
            string reviewDetail = string.Join("\n", Enumerable.Range(1, 200).Select(index => $"计划第 {index} 行：需要完整保留的审查内容。"));
            var dshPayload = new JsonObject { ["questions"] = new JsonArray(new JsonObject
            {
                ["id"] = "dsh-review", ["header"] = "DSH 计划审核", ["question"] = "请选择需要执行的步骤。", ["detail"] = reviewDetail,
                ["multiSelect"] = true, ["intent"] = new JsonObject { ["kind"] = "approval", ["approve"] = "执行修改" },
                ["options"] = new JsonArray(new JsonObject { ["label"] = "执行修改", ["description"] = "执行已审核的修改" },
                    new JsonObject { ["label"] = "更新文档" }, new JsonObject { ["label"] = "运行检查" })
            }) };
            void Render(PendingPrompt prompt) => typeof(MainWindow).GetMethod("RenderPrompts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { new List<PendingPrompt> { prompt } });
            Render(new("dsh-question", "item/tool/requestUserInput", "DSH 需要你的回答", "", dshPayload));
            if (!panel.Children.OfType<TextBlock>().Any(text => text.Text == "DSH 计划审核")) throw new Exception("DSH question header missing");
            var detail = panel.Children.OfType<TextBox>().Single(text => text.IsReadOnly);
            if (detail.Text != reviewDetail || detail.VerticalScrollBarVisibility != ScrollBarVisibility.Auto || detail.MaxHeight > 200)
                throw new Exception("DSH review detail is missing, truncated or not scrollable");
            var checkboxes = panel.Children.OfType<WrapPanel>().Single().Children.OfType<CheckBox>().ToArray();
            if (checkboxes.Length != 3) throw new Exception("DSH multi-select was rendered as a single-choice question");
            checkboxes[0].IsChecked = checkboxes[2].IsChecked = true;
            var custom = panel.Children.OfType<TextBox>().Single(text => !text.IsReadOnly); custom.Text = "保留现有配置";
            var submit = panel.Children.OfType<Button>().Single();
            var readers = AnswerReaders(submit);
            if (!readers["dsh-review"]().SequenceEqual(["执行修改", "运行检查", "保留现有配置"]))
                throw new Exception("DSH multi-select response does not preserve selected labels and custom answer as string[]");
            checkboxes[0].IsChecked = false; custom.Text = "";
            if (!readers["dsh-review"]().SequenceEqual(["运行检查"])) throw new Exception("DSH response retains an unchecked or cleared answer");
            checkboxes[0].IsChecked = true; custom.Text = "补充说明";
            Render(new("dsh-approval", "dsh/approval", "DSH 工具权限", "需要执行工具", new JsonObject { ["reason"] = "检查本地项目" }));
            if (!panel.Children.OfType<TextBlock>().Any(text => text.Text == "DSH 工具权限")) throw new Exception("DSH approval heading missing");
            var approvalButtons = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Select(button => button.Content?.ToString()).ToArray();
            if (!approvalButtons.SequenceEqual(["允许本次", "拒绝"])) throw new Exception("DSH approval must offer only allow-once and reject");
            Render(new("dsh-question", "item/tool/requestUserInput", "DSH 需要你的回答", "", dshPayload));
            var content = (FrameworkElement)window.Content;
            content.Width = 1100; content.Height = 760;
            content.Measure(new Size(1100, 760)); content.Arrange(new Rect(0, 0, 1100, 760)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1100, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(output); encoder.Save(file);
            Console.WriteLine("PASS: actual desktop question panel preserves single-choice drafts, shows DSH review details, produces multi-select string[] answers, and renders allow-once/reject approvals");
        }
        finally { typeof(MainWindow).GetMethod("DisposeTray", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); app.Shutdown(); }
    }

    // Inspect the actual submit handler's answer readers without opening a
    // network connection or executing the async click handler against an Agent.
    private static Dictionary<string, Func<string[]>> AnswerReaders(Button submit)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var store = typeof(UIElement).GetProperty("EventHandlersStore", instance)!.GetValue(submit)!;
        var handlers = (Array)store.GetType().GetMethod("GetRoutedEventHandlers", instance)!.Invoke(store, [Button.ClickEvent])!;
        Dictionary<string, Func<string[]>>? Find(object? value, HashSet<object> seen)
        {
            if (value is Dictionary<string, Func<string[]>> readers) return readers;
            if (value is null || !seen.Add(value)) return null;
            foreach (var field in value.GetType().GetFields(instance))
            {
                var child = field.GetValue(value);
                if (child is Dictionary<string, Func<string[]>> result) return result;
                if (child is not null && child.GetType().IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
                    && Find(child, seen) is { } nested) return nested;
            }
            return null;
        }
        foreach (var info in handlers)
        {
            var handler = (Delegate)info!.GetType().GetProperty("Handler", instance)!.GetValue(info)!;
            if (Find(handler.Target, new HashSet<object>(ReferenceEqualityComparer.Instance)) is { } readers) return readers;
        }
        throw new Exception("Question submit handler does not contain its live answer readers");
    }
}
