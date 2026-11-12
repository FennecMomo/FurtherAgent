using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using FurtherAgent.Client;
using FurtherAgent.Core;

internal static class HotkeyUiChecks
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    internal static void Run()
    {
        var app = new App(); app.InitializeComponent(); var window = new MainWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const int numberTestModifiers = 7;
        string folder = Path.Combine(Path.GetTempPath(), "fa-hotkey-" + Guid.NewGuid());
        string path = Path.Combine(folder, "compact-hotkey.txt");
        T Get<T>(string field) => (T)typeof(MainWindow).GetField(field, flags)!.GetValue(window)!;
        void Set(string field, object value) => typeof(MainWindow).GetField(field, flags)!.SetValue(window, value);
        void Apply(string value) => typeof(MainWindow).GetMethod("ApplyCompactHotkey", flags)!.Invoke(window, [value, true]);
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS: " + label); }
        IntPtr hwnd = IntPtr.Zero;
        var minis = Get<IDictionary>("miniWindows");
        var fixtureDeviceType = typeof(MainWindow).GetNestedType("DeviceContext", BindingFlags.NonPublic)!;
        var fixtureDevice = (IAsyncDisposable)Activator.CreateInstance(fixtureDeviceType, flags | BindingFlags.Public,
            null, ["https://example.test", "fixture"], null)!;
        var miniType = typeof(MainWindow).Assembly.GetType("FurtherAgent.Client.MiniWindow")!;
        var miniKeyType = minis.GetType().GetGenericArguments()[0];
        object MiniKey(int slot) => Activator.CreateInstance(miniKeyType, fixtureDevice, "mini-" + slot)!;
        Window AddMini(int slot)
        {
            string id = "mini-" + slot;
            var view = new SessionView(id, "Mini " + slot, "", "idle", null, [], []);
            var mini = (Window)Activator.CreateInstance(miniType, "https://example.test", "fixture", id, view.Title, view,
                Path.Combine(folder, id + ".json"), (Func<string>)(() => ""), (Action<string>)(_ => { }),
                (Action)(() => { }), slot, 0)!;
            minis.Add(MiniKey(slot), mini);
            return mini;
        }
        int NextSlot() => (int)typeof(MainWindow).GetMethod("FindAvailableMiniShortcutSlot", flags)!.Invoke(window, null)!;
        void SendHotkey(int id, Key key) => SendMessage(hwnd, 0x0312, (IntPtr)id,
            (IntPtr)((KeyInterop.VirtualKeyFromKey(key) << 16) | numberTestModifiers));
        bool NumberKeyAvailable(Key key)
        {
            const int probeId = 0x5FB3;
            if (!RegisterHotKey(hwnd, probeId, numberTestModifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(key))) return false;
            UnregisterHotKey(hwnd, probeId);
            return true;
        }
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

            Apply("Ctrl+Alt+Shift+F11");
            int prefix = Get<int>("hotkeyId");
            var status = Get<TextBlock>("StatusBar");
            string before = status.Text;
            SendHotkey(prefix, Key.F11);
            Check(minis.Count == 0 && status.Text == before && NextSlot() == 1, "prefix with no mini takes no action");

            var first = AddMini(1);
            SendHotkey(prefix, Key.F11);
            Check(first.Width == 44 && NumberKeyAvailable(Key.D1), "prefix toggles the only mini without reserving a number key");

            var tenth = AddMini(10);
            Check(NumberKeyAvailable(Key.D0), "number key is free before the prefix");
            SendHotkey(prefix, Key.F11);
            Check(first.Width == 44 && tenth.Width == 420, "prefix alone preserves all minis when multiple windows are open");
            Check(!NumberKeyAvailable(Key.D1) && !NumberKeyAvailable(Key.D0) && NumberKeyAvailable(Key.D3),
                "prefix temporarily reserves only the open minis' number keys");
            SendHotkey(0x5FC0 + 10, Key.D0);
            Check(first.Width == 44 && tenth.Width == 44, "the top-row zero selects the tenth slot");
            Check(NumberKeyAvailable(Key.D0) && NumberKeyAvailable(Key.D1), "selecting a mini releases temporary number keys");

            SendHotkey(prefix, Key.F11);
            Set("compactNumberDeadline", Environment.TickCount64 - 1);
            SendHotkey(0x5FC0 + 10, Key.D0);
            Check(NumberKeyAvailable(Key.D0) && NumberKeyAvailable(Key.D1) && first.Width == 44 && tenth.Width == 44,
                "a queued digit after the selection deadline releases its registration without toggling a mini");

            for (int slot = 2; slot < 10; slot++)
            {
                if (NextSlot() != slot) throw new Exception("a new mini must take the first free slot");
                AddMini(slot);
            }
            Check(NextSlot() == 0 && minis.Count == 10, "ten minis leave no slot for another window");
            first.Close();
            minis.Remove(MiniKey(1));
            Check(NextSlot() == 1 && ReferenceEquals(minis[MiniKey(10)], tenth), "closing one mini frees its number without shifting another window");
        }
        finally
        {
            typeof(MainWindow).GetMethod("CancelCompactHotkeySequence", flags)!.Invoke(window, null);
            foreach (var mini in minis.Values.Cast<Window>().ToArray()) mini.Close();
            minis.Clear();
            foreach (int id in new[] { 0x5FA1, 0x5FA2, 0x5FB0, 0x5FB1, 0x5FB2 }) UnregisterHotKey(hwnd, id);
            typeof(MainWindow).GetMethod("DisposeTray", flags)!.Invoke(window, null); app.Shutdown();
            fixtureDevice.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.EnumerateFiles(folder)) File.Delete(file);
                Directory.Delete(folder);
            }
        }
    }
}
