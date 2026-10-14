using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using FurtherAgent.Client;

internal static class HotkeyUiChecks
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    internal static void Run()
    {
        var app = new App(); app.InitializeComponent(); var window = new MainWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string path = Path.Combine(Path.GetTempPath(), "fa-hotkey-" + Guid.NewGuid() + ".txt");
        void Set(string field, object value) => typeof(MainWindow).GetField(field, flags)!.SetValue(window, value);
        void Apply(string value) => typeof(MainWindow).GetMethod("ApplyCompactHotkey", flags)!.Invoke(window, [value, true]);
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS: " + label); }
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            Set("hotkeyFile", path); Set("compactHotkey", "Ctrl+Alt+F10");
            hwnd = new WindowInteropHelper(window).EnsureHandle();
            Check(MainWindow.ParseCompactHotkey("Alt+P").Key == Key.P, "default Alt+P is valid");
            bool rejected = false;
            try { MainWindow.ParseCompactHotkey("P"); } catch { rejected = true; }
            Check(rejected, "plain typing keys cannot become global shortcuts");
            Apply("Ctrl+Alt+F11");
            Check(File.ReadAllText(path).Contains("F11"), "shortcut is saved after successful native registration");
            Check(RegisterHotKey(hwnd, 0x5FB0, 3 | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(Key.F10)), "changing shortcut releases the old global combination");
            Check(RegisterHotKey(hwnd, 0x5FB1, 3 | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(Key.F12)), "conflict fixture reserves another combination");
            rejected = false;
            try { Apply("Ctrl+Alt+F12"); } catch (TargetInvocationException ex) { rejected = ex.InnerException is IOException; }
            Check(rejected && File.ReadAllText(path).Contains("F11"), "occupied shortcut does not overwrite saved configuration");
            Check(!RegisterHotKey(hwnd, 0x5FB2, 3 | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(Key.F11)), "original shortcut remains registered after conflict");
        }
        finally
        {
            foreach (int id in new[] { 0x5FA1, 0x5FA2, 0x5FB0, 0x5FB1, 0x5FB2 }) UnregisterHotKey(hwnd, id);
            typeof(MainWindow).GetMethod("DisposeTray", flags)!.Invoke(window, null); app.Shutdown();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
