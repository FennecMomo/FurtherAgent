using System.Text.Json;
using FurtherAgent.Core;
using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

public sealed partial class MainPage
{
    private readonly DownloadedAttachments downloadedAttachments = new(Path.Combine(FileSystem.AppDataDirectory, "downloads"));
    private readonly Dictionary<string, string> imagePreviews = [];
    private readonly HashSet<(CancellationToken, string)> loadingImagePreviews = [];
    private readonly SemaphoreSlim imagePreviewSlots = new(2);
    private CancellationTokenSource? imagePreviewLifetime;
    private (MobileSession Owner, string Session, RpcPeer? Peer)? imagePreviewSource;
    private bool refreshImagePreviews;

    private string AttachmentKey(RemoteFile file) => DownloadedAttachments.Key(activeConnection ?? "", session.View?.Id ?? "", file);

    private void StopImagePreviews()
    {
        imagePreviewLifetime?.Cancel();
        imagePreviewLifetime?.Dispose();
        imagePreviewLifetime = null;
        imagePreviewSource = null;
    }

    private void UpdateImagePreviews(MobileSession owner, SessionView view, List<RemoteFile> inlineImages, bool transcriptChanged)
    {
        if (closed || suspended || preview || !ReferenceEquals(session, owner) || owner.View?.Id != view.Id)
        {
            StopImagePreviews();
            return;
        }
        var source = (Owner: owner, Session: view.Id, Peer: owner.ImagePeer(view.Id));
        if (imagePreviewSource != source)
        {
            StopImagePreviews();
            imagePreviewSource = source;
            imagePreviewLifetime = new();
        }
        else if (!transcriptChanged && !refreshImagePreviews) return;
        refreshImagePreviews = false;
        var inlineIds = inlineImages.Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
        _ = LoadImagePreviewsAsync(owner, view, source.Peer, inlineIds, activeConnection ?? "", imagePreviewLifetime!.Token);
    }

    private async Task LoadImagePreviewsAsync(MobileSession owner, SessionView view, RpcPeer? peer,
        HashSet<string> inlineIds, string connection, CancellationToken ct)
    {
        foreach (var file in (view.OutputFiles ?? [])
            .Where(file => ImageThumbnail.Supported(file.Name) && (!file.Id.StartsWith("inbox/", StringComparison.Ordinal) || inlineIds.Contains(file.Id)))
            .OrderByDescending(file => inlineIds.Contains(file.Id)))
        {
            if (ct.IsCancellationRequested) return;
            string key = DownloadedAttachments.Key(connection, view.Id, file);
            bool ownsLoad = loadingImagePreviews.Add((ct, key));
            try
            {
                if (!ownsLoad)
                {
                    // A transcript reset can clear the WebView's copy while a download is still running.
                    if (imagePreviews.TryGetValue(key, out string? existing))
                        await ShowImagePreviewAsync(owner, view.Id, file, existing, ct);
                    continue;
                }
                await imagePreviewSlots.WaitAsync(ct);
                try { await LoadImagePreviewAsync(owner, view.Id, file, key, peer, ct); }
                finally { imagePreviewSlots.Release(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { /* Leave the attachment clickable when its preview cannot be read. */ }
            finally { if (ownsLoad) loadingImagePreviews.Remove((ct, key)); }
        }
    }

    private async Task LoadImagePreviewAsync(MobileSession owner, string sessionId, RemoteFile file, string key,
        RpcPeer? prefetchPeer, CancellationToken ct)
    {
        if (imagePreviews.TryGetValue(key, out string? data))
            await ShowImagePreviewAsync(owner, sessionId, file, data, ct);

        var direct = downloadCancel is null && prefetchPeer?.IsDirectRoute == true ? prefetchPeer : null;
        string? local = await downloadedAttachments.PrefetchImageAsync(direct, key, sessionId, file, ct);
        if (data is not null || ct.IsCancellationRequested) return;

        FileThumbnail? thumbnail = null;
        if (local is not null)
        {
            // Reopened history can reconstruct a bounded preview from its downloaded image offline.
            try { thumbnail = await Task.Run(() => ImageThumbnail.Create(local, file.Version), ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { /* Older or unsupported cached images can still request the existing server thumbnail. */ }
        }
        if (thumbnail is null && downloadCancel is null && ImageThumbnail.CanRequest(owner.Agent) && owner.ImagePeer(sessionId) is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            thumbnail = await owner.ThumbnailAsync(sessionId, file, timeout.Token);
        }
        if (thumbnail is null || ct.IsCancellationRequested) return;
        data = "data:image/jpeg;base64," + thumbnail.Data;
        if (imagePreviews.Count >= 64) imagePreviews.Remove(imagePreviews.Keys.First());
        imagePreviews[key] = data;
        await ShowImagePreviewAsync(owner, sessionId, file, data, ct);
    }

    private async Task ShowImagePreviewAsync(MobileSession owner, string sessionId, RemoteFile file, string data, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested && ReferenceEquals(session, owner) && session.View?.Id == sessionId && webReady)
            await transcript.EvaluateJavaScriptAsync($"setAttachmentImage({JsonSerializer.Serialize(file.Id)},{JsonSerializer.Serialize(file.Version)},{JsonSerializer.Serialize(data)});");
    }
}
