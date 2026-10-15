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
        var main = new MainWindow();
        var type = typeof(MainWindow).Assembly.GetType("FurtherAgent.Client.MiniWindow")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pathA = Path.Combine(Path.GetTempPath(), "fa-mini-a-" + Guid.NewGuid() + ".json");
        string pathB = Path.Combine(Path.GetTempPath(), "fa-mini-b-" + Guid.NewGuid() + ".json");
        string draftA = "第一个会话草稿", draftB = "第二个会话草稿";
        var viewA = new SessionView("a", "会话 A", "", "idle", null, [new("user", "问题 A"), new("tool", "不显示工具原文"), new("assistant", "回复 A")], []);
        var viewB = new SessionView("b", "会话 B", "", "idle", null, [new("assistant", "回复 B")], []);
        Window Create(string id, SessionView view, string path, Func<string> read, Action<string> write) =>
            (Window)Activator.CreateInstance(type, "https://example.test", "fixture", id, view.Title, view, path, read, write, (Action)(() => { }), 0)!;
        var a = Create("a", viewA, pathA, () => draftA, text => draftA = text);
        var b = Create("b", viewB, pathB, () => draftB, text => draftB = text);
        T Field<T>(Window window, string name) => (T)type.GetField(name, flags)!.GetValue(window)!;
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
            Check(main.Width == 1240 && main.MinWidth == 940 && main.Content is Grid,
                "creating minis leaves main window size and content intact");
            Check(Field<TextBlock>(a, "context").Text.Contains("回复 A") && !Field<TextBlock>(a, "context").Text.Contains("工具原文") &&
                  Field<TextBlock>(b, "context").Text.Contains("回复 B"),
                "each mini displays only its fixed session conversation");
            Call(a, "Refresh", viewB);
            Check(!Field<TextBlock>(a, "context").Text.Contains("回复 B"), "another session update cannot redirect a mini");
            var inputA = Field<TextBox>(a, "composer"); var inputB = Field<TextBox>(b, "composer");
            Check(inputA.Text == draftA && inputB.Text == draftB, "each session begins with its own draft");
            inputA.Text = "编辑 A";
            Check(draftA == "编辑 A" && draftB == "第二个会话草稿" && inputB.Text == draftB,
                "editing one mini never changes another session draft");
            Call(b, "SetDraft", "主窗口同步 B");
            Check(inputB.Text == "主窗口同步 B" && draftB == "第二个会话草稿", "main window can update mini draft without a write loop");
            Check(inputA.ActualHeight >= 48 && inputA.TransformToAncestor((Visual)a.Content).Transform(new Point(0, inputA.ActualHeight)).Y <= 210,
                "mini composer stays fully inside expanded window");
            a.Height = 180; Field<RowDefinition>(a, "contextRow").Height = new GridLength(500); Layout(a, 420, 180);
            Check(inputA.ActualHeight >= 48 && inputA.TransformToAncestor((Visual)a.Content).Transform(new Point(0, inputA.ActualHeight)).Y <= 180,
                "long context cannot push composer outside minimum window");
            Field<RowDefinition>(a, "contextRow").Height = new GridLength(1, GridUnitType.Star);
            a.Height = 210; a.Left = 120; a.Top = 140; Layout(a, 420, 210);
            for (int i = 0; i < 10; i++)
            {
                Call(a, "ToggleContext"); Layout(a, 44, 44);
                Check(a.Width == 44 && a.Height == 44 && a.Left == 496 && a.Top == 306, "mini ball preserves its bottom-right corner");
                Call(a, "ToggleContext"); Layout(a, 420, 210);
                Check(a.Width == 420 && a.Height == 210 && a.Left == 120 && a.Top == 140, "mini expansion returns to exact bounds");
            }
            Check(b.Width == 420 && b.Height == 210 && Field<TextBlock>(b, "context").Text.Contains("回复 B"),
                "collapsing one mini does not alter another mini");
            var bitmap = new RenderTargetBitmap(420, 210, 96, 96, PixelFormats.Pbgra32); bitmap.Render((Visual)a.Content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(output)) png.Save(file);
            Call(a, "SavePosition");
            Check(File.Exists(pathA) && !File.Exists(pathB), "each mini persists only its own position");
        }
        finally
        {
            a.Close(); b.Close(); typeof(MainWindow).GetMethod("DisposeTray", flags)!.Invoke(main, null); app.Shutdown();
            if (File.Exists(pathA)) File.Delete(pathA);
            if (File.Exists(pathB)) File.Delete(pathB);
        }
    }
}
