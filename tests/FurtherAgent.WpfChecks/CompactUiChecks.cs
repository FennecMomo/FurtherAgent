using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class CompactUiChecks
{
    internal static void Run(string output)
    {
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        void Layout(double width, double height) { window.Measure(new Size(width,height)); window.Arrange(new Rect(100,100,width,height)); window.UpdateLayout(); var content = (FrameworkElement)window.Content; content.Measure(new Size(width,height)); content.Arrange(new Rect(0,0,width,height)); content.UpdateLayout(); }
        string positionFile = Path.Combine(Path.GetTempPath(), "fa-compact-" + Guid.NewGuid() + ".json");
        typeof(MainWindow).GetField("compactSettingsPath", flags)!.SetValue(window, positionFile);
        try
        {
            Layout(1100,760);
            var composer = (TextBox)window.FindName("Composer"); composer.Text = "保留当前草稿";
            var history = (ConversationHistory)typeof(MainWindow).GetProperty("History", flags)!.GetValue(window)!;
            history.Accept(new("compact", "会话", "", "idle", null, [new("你", "刚才的提问"),new("工具", "工具原文不显示"),new("Codex", "这是最新回复。\n上下文保留约五行，拖动分隔线可以调整高度。")], []));
            Call("Compact_Click", window, new RoutedEventArgs()); Layout(420,210);
            Check(Field<FrameworkElement>("fullSurface").Visibility == Visibility.Collapsed && Field<Grid>("compactSurface").Visibility == Visibility.Visible, "compact mode hides the complete full UI");
            var mini = Field<TextBox>("compactComposer");
            Check(mini.ActualHeight >= 48 && mini.TransformToAncestor((Visual)window.Content).Transform(new Point(0, mini.ActualHeight)).Y <= 210,
                "expanded compact window keeps visible input fully inside window");
            window.Height = 180;
            Field<RowDefinition>("compactContextRow").Height = new GridLength(500);
            Layout(420,180);
            Check(mini.ActualHeight >= 48 && mini.TransformToAncestor((Visual)window.Content).Transform(new Point(0, mini.ActualHeight)).Y <= 180,
                "oversized context cannot push input outside minimum-size window");
            Field<RowDefinition>("compactContextRow").Height = new GridLength(1, GridUnitType.Star);
            window.Height = 210; Layout(420,210);
            Check(mini.Text == composer.Text, "compact composer inherits current draft");
            mini.Text = "迷你窗口编辑的新草稿";
            Check(composer.Text == mini.Text, "compact typing updates the original composer");
            composer.Clear(); Check(mini.Text == "", "successful-send clear propagates into compact composer");
            mini.Text = "继续保留草稿";
            Check(Field<TextBlock>("compactContext").Text.Contains("最新回复") && !Field<TextBlock>("compactContext").Text.Contains("工具原文"), "compact context shows only conversation prose");
            var surface = Field<Grid>("compactSurface");
            var bitmap = new RenderTargetBitmap(420,210,96,96,PixelFormats.Pbgra32); bitmap.Render((Visual)window.Content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(output)) png.Save(file);
            Call("ToggleCompactContext"); Layout(420,110);
            Check(Field<ScrollViewer>("compactScroll").Visibility == Visibility.Collapsed && window.Height <= 110, "context collapses to input-sized window");
            Call("ToggleCompactContext"); Layout(420,210);
            Check(Field<ScrollViewer>("compactScroll").Visibility == Visibility.Visible, "context expands again");
            window.Left = 160; window.Top = 180;
            Call("RestoreCompact"); Layout(1100,760);
            Check(File.Exists(positionFile), "mini position is persisted on restore");
            typeof(MainWindow).GetField("compactPosition", flags)!.SetValue(window, null);
            Call("Compact_Click", window, new RoutedEventArgs()); Layout(420,210);
            Check(window.Left == 160 && window.Top == 180, "next compact entry reloads its saved position from disk");
            Call("RestoreCompact"); Layout(1100,760);
            Check(Field<FrameworkElement>("fullSurface").Visibility == Visibility.Visible && composer.Text == "继续保留草稿" && window.MinWidth == 940, "restoring full window preserves draft and restores size constraints");
        }
        finally { Call("DisposeTray"); app.Shutdown(); if (File.Exists(positionFile)) File.Delete(positionFile); }
    }
}
