namespace FurtherAgent.AndroidApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp.CreateBuilder().UseMauiApp<App>().Build();
}

public sealed class App : Application
{
    public App() => Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.Application.SetWindowSoftInputModeAdjust(
        this, Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.WindowSoftInputModeAdjust.Resize);

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new MainPage();
        var navigation = new NavigationPage(page) { BarBackgroundColor = Color.FromArgb("#183F34"), BarTextColor = Colors.White };
        NavigationPage.SetHasNavigationBar(page, false);
        var window = new Window(navigation);
        window.Resumed += async (_, _) => await page.ResumeAsync();
        window.Stopped += (_, _) => page.Suspend();
        window.Destroying += async (_, _) => await page.CloseAsync();
        return window;
    }
}
