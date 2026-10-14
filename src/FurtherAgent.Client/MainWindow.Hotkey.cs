using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FurtherAgent.Client;

public partial class MainWindow
{
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
        SourceInitialized += (_, _) =>
        {
            hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            hotkeySource?.AddHook(CompactHotkeyHook);
            try { ApplyCompactHotkey(compactHotkey, false); }
            catch (Exception ex) { CompactHotkeyHint.Text = ex.Message; StatusBar.Text = ex.Message; }
        };
        Closed += (_, _) =>
        {
            if (hotkeySource is null) return;
            if (hotkeyId != 0) UnregisterHotKey(hotkeySource.Handle, hotkeyId);
            hotkeySource.RemoveHook(CompactHotkeyHook);
        };
    }
    private IntPtr CompactHotkeyHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == hotkeyId && hotkeyId != 0)
        {
            handled = true;
            if (CompactHotkeyInput.IsKeyboardFocusWithin) return IntPtr.Zero;
            ToggleCompactHotkey();
        }
        return IntPtr.Zero;
    }
    private void ToggleCompactHotkey()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!compactMode) Compact_Click(this, new RoutedEventArgs());
        else ToggleCompactContext();
        Activate();
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
        try { ApplyCompactHotkey(CompactHotkeyInput.Text, true); CompactHotkeyHint.Text = $"已保存：{compactHotkey}，全局生效。"; }
        catch (Exception ex) { CompactHotkeyHint.Text = ex.Message; }
    }
    private void ApplyCompactHotkey(string text, bool save)
    {
        var gesture = ParseCompactHotkey(text);
        string normalized = gesture.GetDisplayStringForCulture(CultureInfo.InvariantCulture);
        if (hotkeySource is null) throw new InvalidOperationException("窗口尚未就绪，请稍后重试。");
        int nextId = hotkeyId == 0x5FA1 ? 0x5FA2 : 0x5FA1;
        bool same = hotkeyId != 0 && ParseCompactHotkey(compactHotkey).Key == gesture.Key && ParseCompactHotkey(compactHotkey).Modifiers == gesture.Modifiers;
        if (!same && !RegisterHotKey(hotkeySource.Handle, nextId, (uint)gesture.Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key)))
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
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
