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

    public static void Start()
    {
        var context = global::Android.App.Application.Context;
        context.StartForegroundService(new Intent(context, typeof(ConnectionService)));
    }

    public static void Stop()
    {
        var context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(ConnectionService)));
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "远端连接", NotificationImportance.Low));
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity))
            .SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var notification = new Notification.Builder(this, Channel)
            .SetContentTitle("FurtherAgent · 保持远端连接")
            .SetContentText("切换应用后继续接收会话更新；在连接设置中断开可停止。")
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifySyncNoAnim)
            .SetContentIntent(open).SetOngoing(true).SetOnlyAlertOnce(true).Build();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeRemoteMessaging);
        else StartForeground(NotificationId, notification);
        // Do not recreate an empty service after Android has killed the entire process.
        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;
}
