using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public partial class MainWindow
{
    private bool preparingAttachment;
    private Task attachmentPreparation = Task.CompletedTask;
    private bool CanAttachFiles => !preparingAttachment && !actionBusy && remote?.IsConnected == true && current is not null && agent?.CanFiles == true;

    private void InitializeAttachments()
    {
        ConversationPage.AllowDrop = true;
        ConversationPage.PreviewDragOver += Attachments_DragOver;
        ConversationPage.PreviewDrop += Attachments_Drop;
        Composer.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
            try { if (Clipboard.GetDataObject() is { } data && TryAttachData(data)) e.Handled = true; }
            catch (Exception ex) { e.Handled = true; Error(ex); }
        };
        DataObject.AddPastingHandler(Composer, (_, e) => { if (TryAttachData(e.DataObject)) e.CancelCommand(); });
    }

    private static bool HasAttachmentData(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Bitmap, true);

    private void Attachments_DragOver(object sender, DragEventArgs e)
    {
        if (!HasAttachmentData(e.Data)) return;
        e.Handled = true;
        e.Effects = CanAttachFiles && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void Attachments_Drop(object sender, DragEventArgs e)
    {
        if (!HasAttachmentData(e.Data)) return;
        e.Handled = true; e.Effects = DragDropEffects.None;
        if ((e.AllowedEffects & DragDropEffects.Copy) != 0 && TryAttachData(e.Data)) e.Effects = DragDropEffects.Copy;
    }

    // Capture the OS data object synchronously: clipboard or drag contents may change after the event returns.
    private bool TryAttachData(IDataObject data)
    {
        if (!HasAttachmentData(data)) return false;
        if (!CanAttachFiles) { StatusBar.Text = "请先选择支持文件的远端会话，并等待当前操作结束。"; return true; }
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = data.GetData(DataFormats.FileDrop) as string[] ?? [];
                attachmentPreparation = AttachPathsAsync(paths.ToArray());
            }
            else if (data.GetData(DataFormats.Bitmap, true) is BitmapSource bitmap)
            {
                if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || (long)bitmap.PixelWidth * bitmap.PixelHeight > 40000000)
                    throw new IOException("图片超过 4000 万像素，请先缩小。");
                var snapshot = bitmap.CloneCurrentValue(); snapshot.Freeze();
                attachmentPreparation = AttachBitmapAsync(snapshot);
            }
            else throw new IOException("图片格式无法读取，请保存图片后拖入。");
        }
        catch (Exception ex) { Error(ex); }
        return true;
    }

    private async Task AttachBitmapAsync(BitmapSource bitmap)
    {
        if (attachedFiles.Count >= 20) { StatusBar.Text = "每条消息最多 20 个附件。"; return; }
        var connection = remote; string session = current!.Id;
        string path = Path.Combine(Path.GetTempPath(), "FurtherAgent-image-" + Guid.NewGuid().ToString("N") + ".png");
        preparingAttachment = true;
        SessionList.IsEnabled = NewButton.IsEnabled = false; SetSessionEnabled(false);
        try
        {
            await Task.Run(() =>
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(path); encoder.Save(output);
            });
            preparingAttachment = false;
            if (remote == connection && current?.Id == session) await AttachPathsAsync([path]);
        }
        catch (Exception ex) { Error(ex); }
        finally
        {
            preparingAttachment = false;
            SessionList.IsEnabled = NewButton.IsEnabled = true;
            SetSessionEnabled(!actionBusy && remote?.IsConnected == true);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task AttachPathsAsync(string[] paths)
    {
        if (!CanAttachFiles) return;
        try
        {
            paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0) throw new IOException("没有可添加的文件。");
            if (paths.Length + attachedFiles.Count > 20) throw new IOException("每条消息最多 20 个附件。");
            foreach (string path in paths)
            {
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new IOException("只能添加本机文件，文件夹请先压缩。");
                if (new FileInfo(path).Length > FileTransferStore.MaxFileBytes) throw new IOException("单文件最多 100 MiB：" + Path.GetFileName(path));
            }
            var connection = remote; string session = current!.Id; var attachments = attachedFiles;
            await TransferAsync(async (client, progress, ct) =>
            {
                foreach (string path in paths)
                {
                    var uploaded = await client.UploadAsync(session, path, progress, ct);
                    attachments.Add(uploaded);
                    if (remote == connection && current?.Id == session && attachedFiles == attachments) RenderAttachments();
                }
            });
        }
        catch (Exception ex) { Error(ex); }
    }

    private void RenderAttachments()
    {
        AttachmentSummary.Text = attachedFiles.Count == 0 ? "" : $"待发送 · {attachedFiles.Count}/20 个附件（点击发送后提交）";
        AttachmentPreviews.Children.Clear();
        if (current is null) return;
        var connection = remote; string session = current.Id; var attachments = attachedFiles;
        foreach (var file in attachments)
        {
            var card = new StackPanel { Width = 132, Margin = new Thickness(2, 2, 8, 6) };
            var visual = CreateFileVisual(session, file); visual.MaxWidth = 128; visual.MaxHeight = 92;
            card.Children.Add(visual);
            var row = new DockPanel();
            var remove = new Button { Content = "×", ToolTip = "移除此待发附件", Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(2) };
            System.Windows.Automation.AutomationProperties.SetName(remove, "移除附件 " + file.Name);
            remove.Click += (_, _) =>
            {
                if (actionBusy || preparingAttachment || remote != connection || current?.Id != session || attachments != attachedFiles) return;
                attachments.Remove(file); RenderAttachments();
            };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = file.Name, ToolTip = file.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            card.Children.Add(row); AttachmentPreviews.Children.Add(card);
        }
    }
}
