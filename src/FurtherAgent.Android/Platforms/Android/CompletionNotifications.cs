using Android.App;
using Android.Content;
using FurtherAgent.Core;
using System.Security.Cryptography;
using System.Text;

namespace FurtherAgent.AndroidApp;

internal static class CompletionNotifications
{
    private const string Channel = "session-completed";
    private const string OpenAction = "top.fennecmomo.furtheragent.OPEN_COMPLETION";
    private const string ConnectionExtra = "completion-connection", SessionExtra = "completion-session";
    private const string AskedKey = "completion-notifications-permission-asked";
    public sealed record Target(string ConnectionId, string SessionId);
    public static MainPage? CurrentPage { get; set; }
    public static bool Foreground { get; set; }
    public static Target? Pending { get; private set; }
    private static bool asking;
    private static Context Context => global::Android.App.Application.Context;
    private static NotificationManager Manager => (NotificationManager)Context.GetSystemService(global::Android.Content.Context.NotificationService)!;

    private static void EnsureChannel() => Manager.CreateNotificationChannel(
        new NotificationChannel(Channel, "会话完成提醒", NotificationImportance.High)
        { Description = "后台或查看其他会话时，提醒远端任务已完成。" });

    public static async Task RequestPermissionOnceAsync()
    {
        EnsureChannel();
        if (!Foreground || asking || !OperatingSystem.IsAndroidVersionAtLeast(33)
            || Preferences.Get(AskedKey, false)) return;
        asking = true;
        try
        {
            if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() == PermissionStatus.Granted)
            { Preferences.Set(AskedKey, true); return; }
            Preferences.Set(AskedKey, true);
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
        catch { /* Permission refusal/unavailability must not prevent connecting. */ }
        finally { asking = false; }
    }

    public static string PermissionSummary
    {
        get
        {
            EnsureChannel();
            return Manager.AreNotificationsEnabled() && Manager.GetNotificationChannel(Channel)?.Importance != NotificationImportance.None
                ? "会话完成提醒已开启；前台正在查看同一会话时不打扰。"
                : "会话完成提醒未开启，请在系统通知设置中允许通知及会话完成提醒。";
        }
    }

    public static void OpenSettings()
    {
        EnsureChannel();
        Context.StartActivity(new Intent(global::Android.Provider.Settings.ActionAppNotificationSettings)
            .PutExtra(global::Android.Provider.Settings.ExtraAppPackage, Context.PackageName)
            .AddFlags(ActivityFlags.NewTask));
    }

    public static void Show(string connectionId, string device, SessionCompletion completion)
    {
        if (Foreground && CurrentPage?.IsViewingCompletion(connectionId, completion.SessionId) == true) return;
        try
        {
            EnsureChannel();
            if (!Manager.AreNotificationsEnabled()) return;
            string tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionId + "\n" + completion.SessionId)));
            var intent = new Intent(Context, typeof(MainActivity)).SetAction(OpenAction)
                .SetData(global::Android.Net.Uri.Parse("furtheragent://completion/" + tag))
                .SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop)
                .PutExtra(ConnectionExtra, connectionId).PutExtra(SessionExtra, completion.SessionId);
            var open = PendingIntent.GetActivity(Context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var notification = new Notification.Builder(Context, Channel)
                .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyChat)
                .SetContentTitle(string.IsNullOrWhiteSpace(completion.Title) ? "会话执行完成" : completion.Title)
                .SetContentText(device + " · 执行完成")
                .SetContentIntent(open).SetAutoCancel(true).SetCategory(Notification.CategoryStatus)
                .SetVisibility(NotificationVisibility.Private).Build();
            // Replace a previous completion for the same device/session; never replace the keepalive notification.
            Manager.Notify(tag, 4201, notification);
        }
        catch { /* Notification delivery must not terminate the connection's event dispatcher. */ }
    }

    public static void ReceiveIntent(Intent? intent)
    {
        if (intent?.Action != OpenAction) return;
        string? connection = intent.GetStringExtra(ConnectionExtra), session = intent.GetStringExtra(SessionExtra);
        intent.RemoveExtra(ConnectionExtra); intent.RemoveExtra(SessionExtra); intent.SetAction(null);
        if (connection is not { Length: 64 } || !connection.All(char.IsAsciiHexDigit)
            || string.IsNullOrWhiteSpace(session) || session.Length > 2048) return;
        Pending = new(connection, session);
        // Activation and the page timer consume this for both existing and cold-start Activities.
    }

    public static void Clear(Target target) { if (ReferenceEquals(Pending, target)) Pending = null; }
    public static void CancelPending() => Pending = null;
}
