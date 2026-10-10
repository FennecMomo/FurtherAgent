using Android.Media;
using System.Runtime.CompilerServices;

namespace FurtherAgent.AndroidApp;

internal static class MicrophoneAudio
{
    // 16-bit little endian, mono, 16 kHz. Capture only after the provider accepts the task.
    public static async IAsyncEnumerable<byte[]> Capture(Action started, CancellationToken stop,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (stop.IsCancellationRequested) yield break;
        int minimum = AudioRecord.GetMinBufferSize(16000, ChannelIn.Mono, Encoding.Pcm16bit);
        if (minimum <= 0) throw new IOException("这台手机无法以所需格式录音。");
        using var recorder = new AudioRecord(AudioSource.VoiceRecognition, 16000, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(minimum, 6400));
        if (recorder.State != State.Initialized) throw new IOException("麦克风初始化失败，请检查权限或其他录音应用。");
        try
        {
            recorder.StartRecording();
            if (recorder.RecordingState != RecordState.Recording) throw new IOException("无法开始录音。");
            using var stopRegistration = stop.Register(() => { try { recorder.Stop(); } catch { } });
            using var cancelRegistration = cancellationToken.Register(() => { try { recorder.Stop(); } catch { } });
            started();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            byte[] buffer = new byte[3200];
            while (!stop.IsCancellationRequested && elapsed.Elapsed < TimeSpan.FromMinutes(3))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = recorder.Read(buffer, 0, buffer.Length, (int)AudioRecordReadOptions.NonBlocking);
                if (stop.IsCancellationRequested) yield break;
                cancellationToken.ThrowIfCancellationRequested();
                if (count < 0) throw new IOException("麦克风录音中断，已识别的文字已保留。");
                if (count > 0) yield return buffer[..count];
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { if (recorder.RecordingState == RecordState.Recording) recorder.Stop(); }
    }
}
