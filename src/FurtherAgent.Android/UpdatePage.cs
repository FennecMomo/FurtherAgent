using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

internal sealed class UpdatePage : ContentPage
{
    private readonly Entry address = new() { Placeholder = "版本信息 HTTPS 地址", Keyboard = Keyboard.Url };
    private readonly Label message = Ui.Text("", 13);
    private readonly ProgressBar progress = new();
    private readonly Button update;
    private CancellationTokenSource? cancellation;
    private string? readyApk;
    public UpdatePage()
    {
        Title = "应用更新"; Ui.Theme(this);
        address.Text = Preferences.Get("android-update-url", "https://bridge.fennecmomo.top/files/updates/android/latest.json");
        update = new Button { Text = "检查并更新" }; Ui.AddFeedback(update);
        update.Clicked += async (_, _) => await UpdateAsync();
        var cancel = new Button { Text = "取消下载" }; Ui.AddFeedback(cancel);
        cancel.Clicked += (_, _) => cancellation?.Cancel();
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 24, Spacing = 14, Children =
        {
            Ui.Text("当前版本 " + AppInfo.Current.VersionString, 20, true),
            Ui.Text("从自有服务器检查新版，校验安装包后由 Android 确认安装。", 13),
            address, update, cancel, progress, message
        } } };
        Ui.TextTheme(message);
    }
    protected override void OnDisappearing() { cancellation?.Cancel(); base.OnDisappearing(); }
    private async Task UpdateAsync()
    {
        if (cancellation is not null) return;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15)); cancellation = cancel;
        update.IsEnabled = address.IsEnabled = false;
        try
        {
            if (readyApk is null || !File.Exists(readyApk))
            {
                var url = AppUpdate.Https(address.Text?.Trim() ?? "").AbsoluteUri;
                Preferences.Set("android-update-url", url);
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                message.Text = "正在检查版本…";
                using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                checkTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                var info = await AppUpdate.CheckAsync(http, url, checkTimeout.Token);
                if (info.VersionCode <= int.Parse(AppInfo.Current.BuildString)) { message.Text = "已是服务器上的最新版本"; return; }
                string installedApk = global::Android.App.Application.Context.ApplicationInfo?.SourceDir ?? "";
                message.Text = "正在检查可用差分包…";
                var delta = await AppDelta.SelectAsync(info, int.Parse(AppInfo.Current.BuildString), installedApk, cancel.Token);
                string mode = delta is null ? "完整更新" : "增量更新";
                if (!await DisplayAlertAsync("更新到 " + info.Version, $"{mode} {(delta?.Size ?? info.Size) / 1048576d:F1} MiB\n{info.Notes}", "下载并安装", "稍后")) return;
                string directory = Path.Combine(FileSystem.CacheDirectory, "app-update"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "FurtherAgent-update.apk");
                var reporter = new Progress<double>(p => { progress.Progress = p; message.Text = $"{mode} {p:P0}（下载完成后校验）"; });
                if (delta is not null)
                {
                    try { await delta.DownloadAndApplyAsync(http, installedApk, path, info, reporter, cancel.Token); }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException)
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        mode = "差分不可用，完整更新";
                        await info.DownloadAsync(http, path, reporter, cancel.Token);
                    }
                }
                else await info.DownloadAsync(http, path, reporter, cancel.Token);
                readyApk = path;
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(26) && !global::Android.App.Application.Context.PackageManager!.CanRequestPackageInstalls())
            {
                message.Text = "安装包已校验。请允许本应用安装更新，返回后再点安装。";
                var intent = new global::Android.Content.Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                    global::Android.Net.Uri.Parse("package:" + AppInfo.Current.PackageName));
                intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
                return;
            }
            message.Text = "安装包已校验，请在系统安装窗口确认。";
            if (!await Launcher.Default.OpenAsync(new OpenFileRequest("安装 FurtherAgent 更新", new ReadOnlyFile(readyApk, "application/vnd.android.package-archive"))))
                throw new IOException("无法打开系统安装器，请检查设备的应用安装限制。");
        }
        catch (OperationCanceledException) { message.Text = "已取消或网络超时，可以重试"; }
        catch (Exception ex) { message.Text = "更新未完成：" + ex.Message; }
        finally { cancellation = null; update.IsEnabled = address.IsEnabled = true; update.Text = readyApk is null ? "检查并更新" : "安装已下载更新"; }
    }
}
