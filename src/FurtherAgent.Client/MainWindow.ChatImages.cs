using System.IO;
using System.Windows.Media.Imaging;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public partial class MainWindow
{
    private RpcPeer? chatImagePeer;
    private string? chatImageSession;
    private CancellationTokenSource? chatImageScope;
    private readonly HashSet<string> requestedChatImages = new(StringComparer.Ordinal);

    private void StopChatImagePrefetch()
    {
        chatImageScope?.Cancel();
        chatImageScope?.Dispose();
        chatImageScope = null;
        chatImagePeer = null;
        chatImageSession = null;
        requestedChatImages.Clear();
    }

    private void PrefetchChatImages(SessionView view, HashSet<string> referencedFiles, bool transcriptChanged)
    {
        var peer = remote is { IsConnected: true } connection ? connection.Peer : null;
        if (peer?.IsDirectRoute != true || agent?.CanFiles != true)
        {
            StopChatImagePrefetch();
            return;
        }

        if (peer != chatImagePeer || view.Id != chatImageSession)
        {
            StopChatImagePrefetch();
            chatImagePeer = peer;
            chatImageSession = view.Id;
            chatImageScope = new();
        }
        else if (!transcriptChanged) return;

        if (view.OutputFiles is not { Count: > 0 } files) return;
        var images = files.Where(file => referencedFiles.Contains(file.Id) && ImageThumbnail.Supported(file.Name))
            .Concat(GalleryImages(files, referencedFiles).Take(24))
            .Where(file => file.Size is > 0 and <= DownloadedAttachments.MaxPrefetchedImageBytes)
            .Select(file => (Key: AttachmentKey(view.Id, file), File: file))
            .Where(image => requestedChatImages.Add(image.Key) && FindDownloadedAttachment(view.Id, image.File, image.Key) is null)
            .ToArray();
        if (images.Length == 0) return;
        var ct = chatImageScope!.Token;
        _ = Task.WhenAll(images.Select(image => PrefetchChatImageAsync(peer, view.Id, image, ct)));
    }

    private async Task PrefetchChatImageAsync(RpcPeer peer, string session, (string Key, RemoteFile File) image, CancellationToken ct)
    {
        try { await downloadedAttachments.PrefetchImageAsync(peer, image.Key, session, image.File, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        { /* The normal preview and explicit download remain available. */ }
    }

    internal static BitmapSource ReadPreviewBitmap(string path, int width, int height = int.MaxValue)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 40000000)
            throw new IOException("预览图片超过 4000 万像素，请下载查看。");
        double scale = Math.Min(1, Math.Min((double)width / frame.PixelWidth, (double)height / frame.PixelHeight));
        input.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
        bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

    internal static BitmapSource ReadThumbnailBitmap(FileThumbnail thumbnail)
    {
        if (thumbnail.Data.Length > (ImageThumbnail.MaxEncodedBytes + 2) / 3 * 4)
            throw new IOException("缩略图数据超过限制。");
        using var stream = new MemoryStream(Convert.FromBase64String(thumbnail.Data));
        var probe = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
        if (probe.PixelWidth > 320 || probe.PixelHeight > 240) throw new IOException("缩略图尺寸超过限制。");
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
}
