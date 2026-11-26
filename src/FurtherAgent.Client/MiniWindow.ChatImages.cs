using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

internal sealed partial class MiniWindow
{
    private readonly DownloadedAttachments? chatImages;
    private readonly string? legacyImageScope;
    private readonly Dictionary<string, Window> imagePreviews = new(StringComparer.Ordinal);
    private IReadOnlyList<RemoteFile>? imageFiles;

    private bool UpdateChatImageContext(SessionView sessionView)
    {
        if (chatImages is null) return false;
        var files = sessionView.OutputFiles;
        if (ReferenceEquals(files, imageFiles) ||
            (files is not null && imageFiles is not null && files.SequenceEqual(imageFiles))) return false;
        imageFiles = files;
        return true;
    }

    private FrameworkElement? CreateChatImageVisual(string reference)
    {
        if (chatImages is null || RemoteFileReferences.Resolve(imageFiles, reference) is not { } file ||
            !ImageThumbnail.Supported(file.Name)) return null;

        var button = new Button
        {
            Content = new TextBlock { Text = "预览图片", TextDecorations = TextDecorations.Underline },
            ToolTip = file.Name,
            Margin = new Thickness(0),
            Padding = new Thickness(1, 0, 1, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = SystemColors.HotTrackBrush,
            Cursor = Cursors.Hand
        };
        AutomationProperties.SetName(button, "预览图片 · " + file.Name);
        button.Click += async (_, _) => await ShowChatImageAsync(file);
        return button;
    }

    private async Task ShowChatImageAsync(RemoteFile file)
    {
        if (closed) return;
        string key = DownloadedAttachments.Key(submissionKey.Device, sessionId, file);
        if (imagePreviews.TryGetValue(key, out var opened)) { opened.Activate(); return; }

        var status = new TextBlock { Text = "正在加载图片…", Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var preview = new Window
        {
            Owner = this, Title = file.Name + " · 图片预览", Content = status,
            Width = 520, Height = 400, MinWidth = 240, MinHeight = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Topmost = true
        };
        preview.KeyDown += (_, e) => { if (e.Key == Key.Escape) preview.Close(); };
        imagePreviews.Add(key, preview);
        preview.Closed += (_, _) => imagePreviews.Remove(key);
        using var load = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        void Cancel(object? sender, EventArgs e) => load.Cancel();
        preview.Closed += Cancel;
        try
        {
            preview.Show();
            var bitmap = await LoadChatImageAsync(key, file, load.Token);
            load.Token.ThrowIfCancellationRequested();
            preview.Content = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception ex)
        {
            status.Text = "无法预览图片：" + ex.Message;
            if (!preview.IsVisible) { imagePreviews.Remove(key); SetStatus(status.Text); }
        }
        finally { preview.Closed -= Cancel; }
    }

    private async Task<BitmapSource> LoadChatImageAsync(string key, RemoteFile file, CancellationToken ct)
    {
        var cache = chatImages!;
        string? path = null;
        bool previewOriginal = file.Size is > 0 and <= DownloadedAttachments.MaxPrefetchedImageBytes;
        if (previewOriginal)
        {
            path = await cache.FindOrWaitAsync(key, ct);
            if (path is null && legacyImageScope is not null)
                path = cache.Find(DownloadedAttachments.Key(legacyImageScope, sessionId, file));
        }
        if (path is null)
        {
            var peer = SessionReady && agent?.CanFiles == true ? attachedPeer : null;
            if (peer is null) throw new IOException("会话暂未连接，请连接后重试。");
            var transfer = new FileTransferClient(peer);
            if (previewOriginal && peer.IsDirectRoute)
                path = await cache.PrefetchImageAsync(peer, key, sessionId, file, ct);
            else if (previewOriginal)
            {
                path = cache.NewPath(file.Name);
                await transfer.DownloadAsync(sessionId, file.Id, path, ct: ct);
                cache.Remember(key, path);
            }
            if (path is null)
            {
                if (!ImageThumbnail.CanRequest(agent)) throw new IOException("接入端暂未提供这张图片的预览。");
                return MainWindow.ReadThumbnailBitmap(await transfer.ThumbnailAsync(sessionId, file.Id, ct));
            }
        }
        return await Task.Run(() => MainWindow.ReadPreviewBitmap(path, 1200, 900), ct).ConfigureAwait(false);
    }
}
