using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FurtherAgent.Client;

public partial class MainWindow
{
    private const int HotkeyMessage = 0x0312;
    private const uint HotkeyNoRepeat = 0x4000;
    private const int CompactNumberHotkeyBase = 0x5FC0;
    private const int CompactNumberTimeoutMilliseconds = 2000;
    private readonly DispatcherTimer compactNumberTimer = new() { Interval = TimeSpan.FromMilliseconds(CompactNumberTimeoutMilliseconds) };
    private readonly Dictionary<int, MiniWindow> compactNumberTargets = [];
    private long compactNumberDeadline;
    private string hotkeyFile = Path.Combine(Path.GetDirectoryName(SettingsFile)!, "compact-hotkey.txt");
    private string compactHotkey = "Alt+P";
    private HwndSource? hotkeySource;
    private int hotkeyId;

    public static KeyGesture ParseCompactHotkey(string text)
    {
        var gesture = (KeyGesture?)new KeyGestureConverter().ConvertFromInvariantString(text)
            ?? throw new ArgumentException("请输入组合键。");
        if ((gesture.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) == 0 ||
            gesture.Key is Key.None or Key.System or Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            throw new ArgumentException("请使用包含 Ctrl 或 Alt 的组合键。");
        return gesture;
    }
    private void InitializeCompactHotkey()
    {
        try { if (File.Exists(hotkeyFile)) { var saved = File.ReadAllText(hotkeyFile).Trim(); ParseCompactHotkey(saved); compactHotkey = saved; } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        CompactHotkeyInput.Text = compactHotkey;
        compactNumberTimer.Tick += (_, _) => CancelCompactHotkeySequence();
        SourceInitialized += (_, _) =>
        {
            hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            hotkeySource?.AddHook(CompactHotkeyHook);
            try { ApplyCompactHotkey(compactHotkey, false); }
            catch (Exception ex) { CompactHotkeyHint.Text = ex.Message; StatusBar.Text = ex.Message; }
        };
        Closed += (_, _) =>
        {
            CancelCompactHotkeySequence();
            if (hotkeySource is null) return;
            if (hotkeyId != 0) UnregisterHotKey(hotkeySource.Handle, hotkeyId);
            hotkeySource.RemoveHook(CompactHotkeyHook);
        };
    }
    private IntPtr CompactHotkeyHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != HotkeyMessage) return IntPtr.Zero;
        int id = wParam.ToInt32();
        bool prefix = id == hotkeyId && hotkeyId != 0;
        if (!prefix && !compactNumberTargets.ContainsKey(id)) return IntPtr.Zero;
        handled = true;
        if (CompactHotkeyInput.IsKeyboardFocusWithin)
        {
            CancelCompactHotkeySequence();
            return IntPtr.Zero;
        }
        if (compactNumberTargets.TryGetValue(id, out var target))
        {
            bool valid = Environment.TickCount64 < compactNumberDeadline && miniWindows.Count > 1;
            CancelCompactHotkeySequence();
            if (valid)
            {
                target.ToggleContext(); target.Activate();
                return IntPtr.Zero;
            }
        }
        if (prefix) ToggleCompactHotkey();
        return IntPtr.Zero;
    }
    private void ToggleCompactHotkey()
    {
        CancelCompactHotkeySequence();
        if (miniWindows.Count == 0) return;
        if (miniWindows.Count == 1)
        {
            var mini = miniWindows.Values.Single();
            mini.ToggleContext(); mini.Activate();
            return;
        }
        if (hotkeySource is null) return;

        var gesture = ParseCompactHotkey(compactHotkey);
        uint modifiers = (uint)gesture.Modifiers | HotkeyNoRepeat;
        uint prefixKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        var unavailable = new List<int>();
        foreach (var mini in miniWindows.Values)
        {
            int digit = mini.ShortcutSlot % 10;
            uint key = (uint)('0' + digit);
            // A custom prefix may itself use a number key. Its existing registration also serves as that suffix.
            int id = key == prefixKey ? hotkeyId : CompactNumberHotkeyBase + mini.ShortcutSlot;
            if (id == hotkeyId || RegisterHotKey(hotkeySource.Handle, id, modifiers, key))
                compactNumberTargets.Add(id, mini);
            else unavailable.Add(digit);
        }
        if (unavailable.Count > 0) StatusBar.Text = $"迷你窗口数字快捷键已被占用：{string.Join("、", unavailable)}。";
        if (compactNumberTargets.Count > 0)
        {
            compactNumberDeadline = Environment.TickCount64 + CompactNumberTimeoutMilliseconds;
            compactNumberTimer.Start();
        }
    }
    private void CancelCompactHotkeySequence()
    {
        compactNumberTimer.Stop();
        if (hotkeySource is { IsDisposed: false } source)
            foreach (int id in compactNumberTargets.Keys)
                if (id != hotkeyId) UnregisterHotKey(source.Handle, id);
        compactNumberTargets.Clear();
    }
    private void CompactHotkey_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        try
        {
            string text = new KeyGesture(key, Keyboard.Modifiers).GetDisplayStringForCulture(CultureInfo.InvariantCulture);
            ParseCompactHotkey(text); CompactHotkeyInput.Text = text;
            CompactHotkeyHint.Text = "点击保存后生效。";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { CompactHotkeyHint.Text = "请按包含 Ctrl 或 Alt 的组合键。"; }
    }
    private void SaveCompactHotkey_Click(object sender, RoutedEventArgs e)
    {
        try { ApplyCompactHotkey(CompactHotkeyInput.Text, true); }
        catch (Exception ex) { CompactHotkeyHint.Text = ex.Message; }
    }
    private void ApplyCompactHotkey(string text, bool save)
    {
        var gesture = ParseCompactHotkey(text);
        string normalized = gesture.GetDisplayStringForCulture(CultureInfo.InvariantCulture);
        if (hotkeySource is null) throw new InvalidOperationException("窗口尚未就绪，请稍后重试。");
        CancelCompactHotkeySequence();
        int nextId = hotkeyId == 0x5FA1 ? 0x5FA2 : 0x5FA1;
        bool same = hotkeyId != 0 && ParseCompactHotkey(compactHotkey).Key == gesture.Key && ParseCompactHotkey(compactHotkey).Modifiers == gesture.Modifiers;
        if (!same && !RegisterHotKey(hotkeySource.Handle, nextId, (uint)gesture.Modifiers | HotkeyNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key)))
            throw new IOException($"快捷键 {normalized} 已被占用或不可用，请换一个组合键；原快捷键保留。");
        try
        {
            if (save)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(hotkeyFile)!);
                File.WriteAllText(hotkeyFile + ".tmp", normalized);
                File.Move(hotkeyFile + ".tmp", hotkeyFile, true);
            }
        }
        catch { if (!same) UnregisterHotKey(hotkeySource.Handle, nextId); throw; }
        if (!same) { if (hotkeyId != 0) UnregisterHotKey(hotkeySource.Handle, hotkeyId); hotkeyId = nextId; }
        compactHotkey = normalized; CompactHotkeyInput.Text = normalized;
        CompactHotkeyHint.Text = $"只有一个迷你窗口时按 {normalized} 收放。多个窗口时按住修饰键，按主键后在 2 秒内按窗口编号。";
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
