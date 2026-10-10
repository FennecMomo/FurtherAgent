using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Content;
using Android.Speech;

namespace FurtherAgent.AndroidApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
    ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : MauiAppCompatActivity
{
    private const int SpeechRequest = 6201;
    private TaskCompletionSource<string?>? speech;

    public Task<string?> RecognizeSpeechAsync()
    {
        if (speech is not null) throw new InvalidOperationException("语音识别正在进行，请先完成或取消。");
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        speech = pending;
        using var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        intent.PutExtra(RecognizerIntent.ExtraLanguage, Java.Util.Locale.Default?.ToLanguageTag() ?? "zh-CN");
        intent.PutExtra(RecognizerIntent.ExtraPrompt, "说出要输入的内容，识别后可编辑，不会自动发送");
        intent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);
        try
        {
            // The recognition activity owns microphone access; our app receives text only.
#pragma warning disable CS0618 // Framework result API remains available across our supported Android versions.
            StartActivityForResult(intent, SpeechRequest);
#pragma warning restore CS0618
        }
        catch (ActivityNotFoundException)
        {
            speech = null;
            pending.SetException(new NotSupportedException("这台手机没有可用的系统语音识别服务。可以使用输入法自带的语音输入，或安装、启用系统语音服务后再试。原草稿已保留。"));
        }
        catch (Exception)
        {
            speech = null;
            pending.SetException(new InvalidOperationException("无法启动系统语音识别，请检查系统语音服务的麦克风权限后重试。原草稿已保留。"));
        }
        return pending.Task;
    }

#pragma warning disable CS0672, CS0618
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != SpeechRequest) return;
        var pending = speech; speech = null;
        if (resultCode == Result.Canceled) pending?.TrySetResult(null);
        else if (resultCode == Result.Ok)
        {
            string? text = data?.GetStringArrayListExtra(RecognizerIntent.ExtraResults)?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (string.IsNullOrWhiteSpace(text)) pending?.TrySetException(new InvalidOperationException("没有识别到文字，请再试一次。原草稿已保留。"));
            else pending?.TrySetResult(text.Trim());
        }
        else pending?.TrySetException(new InvalidOperationException("系统语音识别未完成，请检查语音服务或网络后重试。原草稿已保留。"));
    }
#pragma warning restore CS0672, CS0618

    protected override void OnDestroy()
    {
        var pending = speech; speech = null;
        pending?.TrySetResult(null);
        base.OnDestroy();
    }
}
