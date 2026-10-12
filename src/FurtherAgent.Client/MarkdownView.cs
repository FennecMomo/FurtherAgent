using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FurtherAgent.Client;

// Native WPF text only: no HTML renderer, remote image fetches or embedded scripts.
public sealed class MarkdownView : RichTextBox
{
    static MarkdownView() => VisualTheme.IsCodexProperty.OverrideMetadata(typeof(MarkdownView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((MarkdownView)d).Refresh()));
    public Func<string, FrameworkElement?>? AttachmentFactory { get; init; }
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).Refresh()));
    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public MarkdownView()
    {
        Loaded += (_, _) => Refresh();
        IsReadOnly = true; IsDocumentEnabled = true; BorderThickness = new Thickness(0); Background = Brushes.Transparent;
        Padding = new Thickness(0); VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }
    private void Refresh()
    {
        bool codex = VisualTheme.GetIsCodex(this);
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = codex ? 17 : 13,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(codex ? "#202123" : "#202C32")), LineHeight = codex ? 27 : double.NaN };
        string text = Markdown ?? "";
        if (text.Length > 30000) text = text[..30000] + "\n[显示已截断]";
        string[] lines = text.Replace("\r", "").Split('\n');
        bool fenced = false; var code = new System.Text.StringBuilder(); Table? table = null;
        foreach (string line in lines)
        {
            if (!fenced && line.Trim().StartsWith('|') && line.Trim().EndsWith('|'))
            {
                if (Regex.IsMatch(line, @"^[\s|:\-]+$")) continue;
                if (table is null) { table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 10) }; table.RowGroups.Add(new TableRowGroup()); doc.Blocks.Add(table); }
                var row = new TableRow();
                foreach (string cell in line.Trim().Trim('|').Split('|').Take(20))
                {
                    var para = new Paragraph { Margin = new Thickness(0) }; InlineText(para.Inlines, cell.Trim());
                    row.Cells.Add(new TableCell(para) { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(.5), Padding = new Thickness(6) });
                }
                table.RowGroups[0].Rows.Add(row); continue;
            }
            table = null;
            if (line.TrimStart().StartsWith("```"))
            {
                if (fenced) { AddCode(doc, code.ToString()); code.Clear(); }
                fenced = !fenced; continue;
            }
            if (fenced) { code.AppendLine(line); continue; }
            if (line.Trim().Length == 0) continue;
            var p = new Paragraph { Margin = codex ? new Thickness(0, 3, 0, 10) : new Thickness(0, 2, 0, 6) };
            string value = line;
            var heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (heading.Success) { p.FontSize = 23 - heading.Groups[1].Length * 1.5; p.FontWeight = FontWeights.SemiBold; value = heading.Groups[2].Value; }
            else if (line.StartsWith(">")) { value = line[1..].TrimStart(); p.BorderThickness = new Thickness(3, 0, 0, 0); p.BorderBrush = Brushes.DarkSeaGreen; p.Padding = new Thickness(10, 0, 0, 0); }
            else if (Regex.IsMatch(line, @"^\s*[-*+]\s+\[[ xX]\]\s+"))
            {
                bool done = Regex.IsMatch(line, @"\[[xX]\]");
                value = Regex.Replace(line, @"^\s*[-*+]\s+\[[ xX]\]\s+", done ? "☑ " : "☐ ");
                p.Margin = new Thickness(12, 2, 0, 6);
            }
            else if (Regex.IsMatch(line, @"^\s*[-*+]\s+")) { value = Regex.Replace(line, @"^\s*[-*+]\s+", "• "); p.Margin = new Thickness(12, 2, 0, 6); }
            else if (Regex.IsMatch(line, @"^\s*\d+[.)]\s+")) { value = line.TrimStart(); p.Margin = new Thickness(12, 2, 0, 6); }
            else if (line.Trim() is "---" or "***") { p.BorderThickness = new Thickness(0, 0, 0, 1); p.BorderBrush = Brushes.LightGray; value = ""; }
            InlineText(p.Inlines, value); doc.Blocks.Add(p);
        }
        if (code.Length > 0) AddCode(doc, code.ToString());
        Document = doc;
    }
    private static void AddCode(FlowDocument doc, string value) => doc.Blocks.Add(new Paragraph(new Run(value.TrimEnd('\n')))
    { FontFamily = new FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromRgb(230, 235, 229)), Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 10) });
    private void InlineText(InlineCollection output, string value)
    {
        int offset = 0;
        foreach (Match m in Regex.Matches(value, @"(`[^`]+`|!?\[[^\]]*\]\([^\r\n)]+\)|MEDIA:[^\s]+|\*\*[^*]+\*\*|__[^_]+__|~~[^~]+~~|\*[^*]+\*|(?<!\w)_[^_]+_(?!\w))"))
        {
            output.Add(new Run(value[offset..m.Index])); string token = m.Value;
            int attachmentSplit = token.IndexOf("](", StringComparison.Ordinal);
            string? reference = token.StartsWith("MEDIA:") ? token[6..] : attachmentSplit >= 0 ? token[(attachmentSplit+2)..^1] : null;
            var attachment = reference is null ? null : AttachmentFactory?.Invoke(reference);
            if (attachment is not null) output.Add(new InlineUIContainer(attachment) { BaselineAlignment = BaselineAlignment.Center });
            else if (token.StartsWith('`')) output.Add(new Run(token[1..^1]) { FontFamily = new FontFamily("Consolas"), Background = Brushes.Gainsboro });
            else if (token.StartsWith("**") || token.StartsWith("__")) output.Add(new Bold(new Run(token[2..^2])));
            else if (token.StartsWith("~~")) output.Add(new Run(token[2..^2]) { TextDecorations = TextDecorations.Strikethrough });
            else if (token.StartsWith('*') || token.StartsWith('_')) output.Add(new Italic(new Run(token[1..^1])));
            else
            {
                int split = token.IndexOf("](", StringComparison.Ordinal);
                if (split >= 0 && !token.StartsWith('!') && Uri.TryCreate(token[(split + 2)..^1], UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    var link = new Hyperlink(new Run(token[1..split])) { NavigateUri = uri, ToolTip = uri.ToString() };
                    link.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { } e.Handled = true; };
                    output.Add(link);
                }
                else output.Add(new Run(token));
            }
            offset = m.Index + m.Length;
        }
        output.Add(new Run(value[offset..]));
    }
}
