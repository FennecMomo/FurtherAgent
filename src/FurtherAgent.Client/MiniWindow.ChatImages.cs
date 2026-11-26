using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

internal sealed partial class MiniWindow
{
    private readonly DownloadedAttachments? chatImages;
    private readonly string? legacyImageScope;
    private readonly Dictionary<string, Task<BitmapSource?>> miniChatImages = new(StringComparer.Ordinal);
    private RpcPeer? imagePeer;
    private IReadOnlyList<RemoteFile>? imageFiles;

    private bool UpdateChatImageContext(SessionView sessionView)
    {
        if (chatImages is null) return false;
        var peer = SessionReady && attachedPeer?.IsDirectRoute == true ? attachedPeer : null;
        var files = sessionView.OutputFiles;
        if (peer == imagePeer && (ReferenceEquals(files, imageFiles) ||
            (files is not null && imageFiles is not null && files.SequenceEqual(imageFiles)))) return false;
        if (peer != imagePeer) miniChatImages.Clear();
        imagePeer = peer;
        imageFiles = files;
        return true;
    }

    private FrameworkElement? CreateChatImageVisual(string reference)
    {
        if (chatImages is null || RemoteFileReferences.Resolve(imageFiles, reference) is not { } file ||
            !ImageThumbnail.Supported(file.Name)) return null;

        string key = DownloadedAttachments.Key(submissionKey.Device, sessionId, file);
        string? cached = chatImages.Find(key);
        if (cached is null && legacyImageScope is not null)
            cached = chatImages.Find(DownloadedAttachments.Key(legacyImageScope, sessionId, file));
        if (cached is null && (imagePeer is null || agent?.CanFiles != true)) return null;

        var picture = new ContentControl { Content = new TextBlock { Text = "加载图片…" }, ToolTip = file.Name };
        var peer = imagePeer;
        picture.Loaded += async (_, _) =>
        {
            if (closed) return;
            if (!miniChatImages.TryGetValue(key, out var load))
            {
                if (miniChatImages.Count >= 128) miniChatImages.Remove(miniChatImages.Keys.First());
                miniChatImages[key] = load = LoadChatImageAsync(SessionReady && attachedPeer == peer ? peer : null,
                    key, file, cached, lifetime.Token);
            }
            var bitmap = await load;
            if (closed || !picture.IsLoaded) return;
            picture.Content = bitmap is not null
                ? new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = 240, MaxHeight = 160 }
                : new TextBlock { Text = file.Name };
            if (bitmap is not null && followLatest && !collapsed && !dragging) ScrollToLatestAfterLayout();
        };
        return picture;
    }

    private async Task<BitmapSource?> LoadChatImageAsync(RpcPeer? peer, string key, RemoteFile file, string? cached, CancellationToken ct)
    {
        try
        {
            var path = cached ?? await chatImages!.PrefetchImageAsync(peer, key, sessionId, file, ct);
            if (path is not null) return await Task.Run(() => MainWindow.ReadPreviewBitmap(path, 240, 160), ct);
        }
        catch { /* An unavailable original may still have a host thumbnail. */ }

        if (ct.IsCancellationRequested || peer is null || !SessionReady || attachedPeer != peer ||
            !peer.IsDirectRoute || !ImageThumbnail.CanRequest(agent)) return null;
        try
        {
            var thumbnail = await new FileTransferClient(peer).ThumbnailAsync(sessionId, file.Id, ct);
            return MainWindow.ReadThumbnailBitmap(thumbnail);
        }
        catch { return null; }
    }
}
