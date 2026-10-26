using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace FurtherAgent.AndroidApp;

// Remote messaging continues while the user switches to another application.
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeRemoteMessaging)]
public sealed class ConnectionService : Service
{
    private const string Channel = "remote-connection";
    private const int NotificationId = 4101;
    private const string WantedKey = "background-connection-wanted";
    private PowerManager.WakeLock? cpu;
    private CancellationTokenSource? stop;
    private Task? worker;

    public static void Start()
    {
        var context = global::Android.App.Application.Context;
        Preferences.Set(WantedKey, true);
        context.StartForegroundService(new Intent(context, typeof(ConnectionService)));
    }

    public static void Stop()
    {
        var context = global::Android.App.Application.Context;
        Preferences.Set(WantedKey, false);
        context.StopService(new Intent(context, typeof(ConnectionService)));
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (!Preferences.Get(WantedKey, false)) { StopSelf(); return StartCommandResult.NotSticky; }
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "远端连接", NotificationImportance.Low));
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity))
            .SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var notification = new Notification.Builder(this, Channel)
            .SetContentTitle("FurtherAgent · 保持远端连接")
            .SetContentText("后台保持连接会增加耗电；在连接设置中断开可停止。")
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifySyncNoAnim)
            .SetContentIntent(open).SetOngoing(true).SetOnlyAlertOnce(true).Build();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeRemoteMessaging);
        else StartForeground(NotificationId, notification);
        if (cpu is null)
        {
            var power = (PowerManager)GetSystemService(PowerService)!;
            cpu = power.NewWakeLock(WakeLockFlags.Partial, "FurtherAgent:connection");
            cpu!.SetReferenceCounted(false);
            cpu.Acquire();
            stop = new CancellationTokenSource();
            Connectivity.Current.ConnectivityChanged += NetworkChanged;
            worker = MaintainAsync(stop.Token);
        }
        // Unlike the former notification-only service, a recreated service restores its own sessions.
        return StartCommandResult.Sticky;
    }

    private static async Task MaintainAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.WhenAll(MainThread.InvokeOnMainThreadAsync(ConnectionRuntime.RestoreAsync),
                    MainThread.InvokeOnMainThreadAsync(MailboxRuntime.PollAllAsync)); }
                catch { /* Secure storage can be temporarily unavailable before device unlock. */ }
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async void NetworkChanged(object? sender, ConnectivityChangedEventArgs args)
    {
        if (args.NetworkAccess != NetworkAccess.Internet || stop?.IsCancellationRequested != false) return;
        try { await MainThread.InvokeOnMainThreadAsync(ConnectionRuntime.CheckConnectionsAsync); }
        catch { /* Heartbeats and the service loop continue recovery. */ }
    }

    public override void OnDestroy()
    {
        Connectivity.Current.ConnectivityChanged -= NetworkChanged;
        stop?.Cancel();
        if (cpu?.IsHeld == true) cpu.Release();
        cpu?.Dispose(); cpu = null;
        // Sessions belong to the application; a transient service recreation must not close them.
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;
}
