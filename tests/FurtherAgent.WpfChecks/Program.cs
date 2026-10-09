using System.Windows.Documents;
using FurtherAgent.Client;
using FurtherAgent.Core;
using System.Windows;
using System.Windows.Controls;

static class Program
{
    [STAThread]
    static void Main()
    {
        var view = new MarkdownView { Markdown = "# 标题\n**粗体** 和 `代码`\n> 引用\n- 列表\n```cs\nvar x = 1;\n```\n| A | B |\n|---|---|\n| 一 | 二 |\n[文档](https://example.com)\n<script>alert(1)</script>\n![图片](file:///secret)\n[危险](javascript:alert(1))" };
        var blocks = view.Document.Blocks.ToArray();
        Require(blocks.OfType<Table>().Single().RowGroups[0].Rows.Count == 2, "table rows rendered natively");
        Require(blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<Bold>().Any()), "bold inline rendered");
        Require(blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>()).Count() == 1, "only HTTP link activated; local/script URI stays text");
        var text = new TextRange(view.Document.ContentStart, view.Document.ContentEnd).Text;
        Require(text.Contains("alert(1)") && text.Contains("var x = 1;") && text.Contains("标题"), "code and untrusted HTML preserved as selectable text");
        Require(!blocks.OfType<BlockUIContainer>().Any(), "no remote image or embedded HTML controls");
        var tool = new ChatMessageView { DataContext = new ChatEntry("tool", new string('x', 18042), "raw") };
        tool.Measure(new Size(600, double.PositiveInfinity));
        Require(tool.Child is Expander { IsExpanded: false } && tool.DesiredSize.Height < 80, "18K tool output is collapsed and compact");
        var reasoning = new ChatMessageView { DataContext = new ChatEntry("reasoning.available", "reply-like text", "raw") };
        Require(reasoning.Child is Expander { IsExpanded: false }, "reasoning never renders as a second ordinary reply");
        var user = new ChatMessageView { DataContext = new ChatEntry("user", "你好") };
        var assistant = new ChatMessageView { DataContext = new ChatEntry("assistant", "你好") };
        Require(user.HorizontalAlignment == HorizontalAlignment.Right && assistant.HorizontalAlignment == HorizontalAlignment.Stretch && user.Background.ToString() != assistant.Background.ToString(), "user and assistant alignment and backgrounds differ");
        string footer = "\n\n[FurtherAgent 文件工作区]\n如需向用户交付文件，请把最终文件写入以下输出目录的顶层（普通文件，不使用符号链接，每文件最多 100 MiB），并在回复中告知文件名：\n\"/home/ubuntu/files/" + new string('a', 64) + "/outbox\"";
        Require(ChatPresentation.Display(new("user", "你好" + footer)).Text == "你好", "generated workspace footer hidden without changing original user text");
        Require(ChatPresentation.Display(new("user", "解释\n\n[FurtherAgent 文件工作区]\n示例")).Text.EndsWith("示例"), "user-authored marker alone is preserved");
        Require(ChatPresentation.Display(new("assistant", "回复" + footer)).Text.EndsWith(footer), "assistant content never stripped as user metadata");
        Console.WriteLine("ALL 11 WPF DOCUMENT AND CHAT CHECKS PASSED (no windows opened)");
    }
    static void Require(bool success, string label) { if (!success) throw new Exception(label); Console.WriteLine("PASS: " + label); }
}
