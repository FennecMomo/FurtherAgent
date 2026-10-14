using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class SettingsUiChecks
{
    internal static void Run(string output)
    {
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow();
        try
        {
            var settings = new AgentSettings("model-a", "openai", "default",
                [new("model-a", "openai", "Model A", ["low", "medium", "high", "xhigh", "ultra"], "medium"),
                 new("model-b", "openai", "Model B", ["low", "medium"], "low")], true, true,
                "读取远端 Codex 模型目录。空闲时修改，后续轮次生效。", "ultra", true,
                "远端 Codex 账号限额 · 10-12 15:22\ncodex · 套餐 pro\n7 天：剩余 71% · 重置 10-18 18:36 +08:00", true, ":workspace", true);
            typeof(MainWindow).GetMethod("ApplyModelSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [settings, "test"]);
            var model = (ComboBox)window.FindName("ModelPicker");
            var effort = (ComboBox)window.FindName("ReasoningPicker");
            if (model.IsEditable || model.Items.Count != 2 || effort.SelectedItem as string != "ultra") throw new Exception("catalog/current effort missing");
            model.SelectedIndex = 1;
            if (effort.Items.Count != 2 || effort.SelectedItem as string != "low") throw new Exception("model change retained unsupported effort");
            if (!((TextBlock)window.FindName("UsageText")).Text.Contains("剩余 71%")) throw new Exception("usage missing");
            var permissions = (ComboBox)window.FindName("PermissionPicker");
            if (permissions.Items.Count != 3 || permissions.SelectedIndex != 1) throw new Exception("permission options/current preset missing");
            var permissionPanel = (FrameworkElement)window.FindName("PermissionPanel");
            if (permissionPanel.Visibility != Visibility.Visible) throw new Exception("permission controls missing");
            typeof(MainWindow).GetMethod("ApplyModelSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [settings with { CanChangePermissions = false }, "test"]);
            if (permissionPanel.Visibility != Visibility.Collapsed) throw new Exception("old host incorrectly offers permissions");
            typeof(MainWindow).GetMethod("ApplyModelSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [settings, "test"]);
            ((FrameworkElement)window.FindName("ModelOverlay")).Visibility = Visibility.Visible;
            var content = (FrameworkElement)window.Content;
            content.Width = 1100; content.Height = 820;
            content.Measure(new Size(1100, 820)); content.Arrange(new Rect(0, 0, 1100, 820)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1100, 820, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(output); encoder.Save(file);
            Console.WriteLine("PASS: actual WPF settings panel selects remote models, restores current effort, replaces incompatible effort and displays quota");
        }
        finally { typeof(MainWindow).GetMethod("DisposeTray", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); app.Shutdown(); }
    }
}
