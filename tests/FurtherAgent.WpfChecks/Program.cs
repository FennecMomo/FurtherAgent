using System.Windows.Documents;
using FurtherAgent.Client;

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
        Console.WriteLine("ALL 5 WPF DOCUMENT CHECKS PASSED (no windows opened)");
    }
    static void Require(bool success, string label) { if (!success) throw new Exception(label); Console.WriteLine("PASS: " + label); }
}
