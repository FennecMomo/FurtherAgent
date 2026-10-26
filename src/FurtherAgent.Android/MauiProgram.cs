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
        window.Activated += async (_, _) =>
        {
            CompletionNotifications.CurrentPage = page; CompletionNotifications.Foreground = true;
            (navigation.CurrentPage as ConnectionPage)?.RefreshNotificationStatus();
            if (navigation.CurrentPage is MailboxPage mailbox) await mailbox.ResumeForegroundAsync();
            _ = MailboxRuntime.PollAllAsync();
            await page.OpenPendingMailboxAsync();
            await page.ResumeAsync();
        };
        window.Deactivated += (_, _) =>
        {
            CompletionNotifications.Foreground = false;
            (navigation.CurrentPage as MailboxPage)?.SuspendForeground();
        };
        window.Stopped += (_, _) => page.Suspend();
        window.Destroying += (_, _) =>
        {
            page.Detach();
            if (ReferenceEquals(CompletionNotifications.CurrentPage, page))
            { CompletionNotifications.CurrentPage = null; CompletionNotifications.Foreground = false; }
        };
        return window;
    }
}
