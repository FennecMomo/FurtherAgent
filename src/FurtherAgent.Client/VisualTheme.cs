using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;

namespace FurtherAgent.Client;

public static class VisualTheme
{
    public static FontFamily CodexFont { get; } = CreateCodexFont();
    private static FontFamily CreateCodexFont()
    {
        var font = new FontFamily();
        font.FamilyMaps.Add(new FontFamilyMap { Unicode = "0000-024F", Target = "Segoe UI" });
        font.FamilyMaps.Add(new FontFamilyMap { Target = "Microsoft YaHei UI" });
        return font;
    }
    public const string Codex = "codex", Original = "original";
    public static readonly DependencyProperty IsCodexProperty = DependencyProperty.RegisterAttached("IsCodex", typeof(bool), typeof(VisualTheme),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsCodex(DependencyObject element) => (bool)element.GetValue(IsCodexProperty);
    public static void SetIsCodex(DependencyObject element, bool value) => element.SetValue(IsCodexProperty, value);
    public static string Normalize(string? value) => value == Original ? Original : Codex;
    public static void Apply(FrameworkElement root, string theme)
    {
        bool codex = Normalize(theme) == Codex;
        var r = root.Resources;
        void Put(string key, object original, object modern) => r[key] = codex ? modern : original;
        foreach (var (original, modern) in Colors)
            r["Brush" + original.TrimStart('#')] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(codex ? modern : original));
        Put("RailWidth", new GridLength(0), new GridLength(60));
        Put("SidebarWidth", new GridLength(264), new GridLength(288));
        Put("TopbarHeight", new GridLength(0), new GridLength(50));
        Put("CodexOnly", Visibility.Collapsed, Visibility.Visible);
        Put("OriginalOnly", Visibility.Visible, Visibility.Collapsed);
        Put("ShellRadius", new CornerRadius(0), new CornerRadius(16, 16, 0, 0));
        Put("SidebarPadding", new Thickness(18), new Thickness(10, 14, 10, 12));
        Put("ContentMargin", new Thickness(30, 20, 30, 14), new Thickness(20, 18, 20, 10));
        Put("UIFont", new FontFamily("Microsoft YaHei UI"), CodexFont);
        Put("UIFontSize", 13d, 14d);
        Put("HeaderSize", 23d, 14d);
        Put("PageTitleSize", 28d, 22d);
        Put("DialogTitleSize", 24d, 20d);
        Put("SectionTitleSize", 18d, 15d);
        Put("HeaderMargin", new Thickness(0, 0, 0, 18), new Thickness(10, 4, 4, 14));
        Put("ComposerRadius", new CornerRadius(12), new CornerRadius(28));
        Put("ComposerPadding", new Thickness(0), new Thickness(14, 10, 10, 8));
        Put("ComposerMinHeight", 76d, 48d);
        Put("ComposerFooterMargin", new Thickness(0), new Thickness(0, 4, 0, 0));
        Put("ButtonRadius", new CornerRadius(7), new CornerRadius(9));
        Put("SidebarButtonPadding", new Thickness(16, 10, 16, 10), new Thickness(12, 7, 12, 7));
        Put("SidebarButtonBackground", r["Brush24644D"], Brushes.Transparent);
        Put("SidebarActionBackground", r["BrushEAEDEA"], Brushes.Transparent);
        Put("SidebarActionAlignment", HorizontalAlignment.Center, HorizontalAlignment.Left);
        Put("SidebarButtonForeground", Brushes.White, r["Brush202C32"]);
        Put("GroupPathVisibility", Visibility.Visible, Visibility.Collapsed);
        Put("SessionDateVisibility", Visibility.Visible, Visibility.Collapsed);
        Put("GroupWeight", FontWeights.SemiBold, FontWeights.Normal);
        Put("RowWidth", 175d, 225d);
        Put("GroupWidth", 195d, 235d);
        Put("GroupHeader", "工作空间", "项目");
        Put("ArchiveLabel", "恢复归档", "已归档  ›");
        Put("NewChatLabel", "＋  新建会话", "新聊天");
        Put("GroupHeadingPath", "Heading", "Name");
        Put("DiagnosticVisibility", Visibility.Visible, Visibility.Collapsed);
        Put("SearchVisibility", Visibility.Visible, Visibility.Collapsed);
        Put("ShellBackground", r["BrushFCFCFA"], new SolidColorBrush(Color.FromRgb(248, 241, 236)));
        Put("SidebarRadius", new CornerRadius(0), new CornerRadius(16, 0, 0, 0));
        Put("ContentRadius", new CornerRadius(0), new CornerRadius(0, 16, 0, 0));
        Put("ModelAlignment", HorizontalAlignment.Left, HorizontalAlignment.Right);
        r[typeof(ComboBox)] = root.TryFindResource(codex ? "CodexComboBox" : "OriginalComboBox")!;
        r[typeof(TextBox)] = root.TryFindResource(codex ? "CodexTextBox" : "OriginalTextBox")!;
        r["NewChatContentTemplate"] = codex ? root.TryFindResource("CodexNewChatTemplate") : null;
        Put("ComposerHintVisibility", Visibility.Visible, Visibility.Collapsed);
        r["ActiveTreeStyle"] = root.TryFindResource(codex ? "CodexTreeItem" : "OriginalTreeItem")!;
        r["SendStyle"] = root.TryFindResource(codex ? "CodexSend" : "CompactPrimary")!;
        r["RuntimeButtonStyle"] = root.TryFindResource(codex ? "CodexRuntimeButton" : "Compact")!;
        if (root.FindName("ConversationTools") is WrapPanel tools && root.FindName("ComposerStack") is StackPanel stack && root.FindName("FooterToolsHost") is ContentControl host)
        {
            if (codex && stack.Children.Contains(tools)) { stack.Children.Remove(tools); host.Content = tools; }
            else if (!codex && host.Content == tools) { host.Content = null; stack.Children.Insert(0, tools); }
        }
        SetIsCodex(root, codex);
    }
    // Preserve the exact original palette when switching back.
    private static readonly (string, string)[] Colors =
    [
        ("#FCFCFA", "#FFFFFF"), ("#F1F3EE", "#FCFAF9"), ("#E1E5DD", "#E5DFDC"),
        ("#202C32", "#202123"), ("#EAEDEA", "#F3F2F1"), ("#263E35", "#424242"),
        ("#24644D", "#367BF5"), ("#D8DFD9", "#E9E7E6"), ("#E9EDE8", "#F4F1EF"),
        ("#DCE9DF", "#F0EEEC"), ("#738079", "#8F8F8F"), ("#65736B", "#797979"),
        ("#879188", "#929292"), ("#7E8981", "#929292"), ("#748078", "#919191"),
        ("#7A877E", "#919191"), ("#FFF2D7", "#FFF5DF"), ("#CFD9CF", "#ECEAE9"),
        ("#537363", "#707070"), ("#859088", "#A0A0A0"), ("#77847B", "#818181"),
        ("#758178", "#949494"), ("#660F2118", "#55000000"), ("#A34036", "#B53B32")
    ];
}
