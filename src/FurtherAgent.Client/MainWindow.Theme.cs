using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;

namespace FurtherAgent.Client;

public partial class MainWindow
{
    private string uiTheme = VisualTheme.Codex;
    private bool applyingTheme, sidebarHidden;
    private void ApplyTheme(string value)
    {
        uiTheme = VisualTheme.Normalize(value);
        applyingTheme = true;
        VisualTheme.Apply(this, uiTheme);
        bool codex = uiTheme == VisualTheme.Codex;
        WindowStyle = codex ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowChrome.SetWindowChrome(this, codex ? new WindowChrome { CaptionHeight = 50, ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(10), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false } : null);
        ThemePicker.SelectedIndex = codex ? 0 : 1;
        sidebarHidden = false; SidebarSurface.Visibility = Visibility.Visible;
        applyingTheme = false;
    }
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (applyingTheme || !IsInitialized || ThemePicker.SelectedIndex < 0) return;
        string previous = uiTheme, next = ThemePicker.SelectedIndex == 0 ? VisualTheme.Codex : VisualTheme.Original;
        try { ApplyTheme(next); SaveServerAddress(ServerUrl.Text.Trim()); StatusBar.Text = "界面风格已保存。"; }
        catch (Exception ex) { ApplyTheme(previous); Error(ex); }
    }
    private void CodexTheme_Click(object sender, RoutedEventArgs e) => ThemePicker.SelectedIndex = 0;
    private void OriginalTheme_Click(object sender, RoutedEventArgs e) => ThemePicker.SelectedIndex = 1;
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        sidebarHidden = !sidebarHidden;
        Resources["SidebarWidth"] = new GridLength(sidebarHidden ? 0 : uiTheme == VisualTheme.Codex ? 288 : 264);
        SidebarSurface.Visibility = sidebarHidden ? Visibility.Collapsed : Visibility.Visible;
    }
    private void SearchFocus_Click(object sender, RoutedEventArgs e)
    {
        if (sidebarHidden) ToggleSidebar_Click(sender, e);
        Resources["SearchVisibility"] = Visibility.Visible;
        SearchBox.Focus();
    }
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => Resources["DiagnosticVisibility"] =
        (Visibility)Resources["DiagnosticVisibility"] == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();
    private void Cut_Click(object sender, RoutedEventArgs e) => Composer.Cut();
    private void Paste_Click(object sender, RoutedEventArgs e) => Composer.Paste();
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"FurtherAgent {typeof(App).Assembly.GetName().Version?.ToString(3)}\n\n当前为 Codex 风格。可在设置 → 界面风格切换为原始风格。\n\nCtrl + Enter 发送 · 关闭窗口收到托盘。", "关于 FurtherAgent", MessageBoxButton.OK, MessageBoxImage.Information);
}
