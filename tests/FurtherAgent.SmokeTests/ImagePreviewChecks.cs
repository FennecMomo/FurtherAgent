using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class ImagePreviewChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(ImageThumbnail.CanRequest(new AgentInfo("codex", "Codex", true, "", CanFiles: true)), "Legacy Codex capability omission still permits thumbnail requests");
        check(!ImageThumbnail.CanRequest(new AgentInfo("dsh", "DSH", true, "", CanFiles: true)), "Other backends retain their negotiated thumbnail capability");
        var image = new RemoteFile("outbox/gate-flow-after-draining.png", "gate-flow-after-draining.png", 982314, Version: "v1");
        var second = new RemoteFile("outbox/other.png", "other.png", 10, Version: "v2");
        const string reference = "D:/Projects/PersonalWorks/ValleyTest/Validation/gate-flow-after-draining.png";
        var view = new SessionView("preview", "", "", "idle", null,
            [new("assistant", "![实际画面](" + reference + ")")], [], OutputFiles: [image, second]);
        string html = Transcript.Render(view);
        check(RemoteFileReferences.Resolve(view.OutputFiles, reference) == image, "Actual ValleyTest Windows image path resolves to delivered attachment");
        check(html.Contains("data-attachment='outbox/gate-flow-after-draining.png'") && !html.Contains("![实际画面]"), "Markdown image is an inline preview instead of literal image syntax");
        check(html.IndexOf("data-attachment=", StringComparison.Ordinal) < html.IndexOf("</article>", StringComparison.Ordinal), "Referenced preview stays in its message");
        check(html.Contains("data-attachment='outbox/other.png'"), "Unreferenced output images remain automatically visible");
        check(System.Text.RegularExpressions.Regex.Matches(html, "data-attachment='outbox/gate-flow-after-draining.png'").Count == 1, "Gallery does not duplicate the inline image");
        check(html.Contains("href='attachment:outbox%2Fgate-flow-after-draining.png'"), "Image opens the exact attachment ID");
        CheckProjectImage(check);
    }

    private static void CheckProjectImage(Action<bool, string> check)
    {
        var fixture = Directory.CreateTempSubdirectory("furtheragent-chat-image-");
        try
        {
            string project = fixture.CreateSubdirectory("测试项目 (验证)").FullName;
            string imagePath = Path.Combine(project, "画面 01.png");
            string outsidePath = Path.Combine(fixture.FullName, "outside.png");
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jP1sAAAAASUVORK5CYII=");
            File.WriteAllBytes(imagePath, png);
            File.WriteAllBytes(outsidePath, png);
            string archivePath = Path.Combine(project, "bundle.zip");
            using (System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create)) { }

            static string MarkdownPath(string path) => OperatingSystem.IsWindows() ? "/" + path.Replace('\\', '/') : path;
            string sourceImage = $"![生成结果](<{MarkdownPath(imagePath)}>)";
            string outsideImage = $"![范围外](<{MarkdownPath(outsidePath)}>)";
            string archiveLink = $"[工程压缩包](<{MarkdownPath(archivePath)}>)";
            string text = string.Join("\n\n", sourceImage, outsideImage, archiveLink);
            var snapshot = new SessionView("project-history", "", project, "idle", null, [new("assistant", text)], []);

            var images = new ChatImageFiles();
            var projected = images.Project(snapshot);
            var attachment = projected.OutputFiles?.SingleOrDefault();
            check(attachment is not null && attachment.Id.StartsWith("image/", StringComparison.Ordinal) &&
                attachment.Name == "画面 01.png" && attachment.Size == png.Length,
                "a saved project image becomes a file attachment without a manually seeded outbox");
            var imageId = attachment!.Id;
            string presentation = projected.Messages.Single().Text;
            string html = Transcript.Render(projected);
            check(presentation.Contains("attachment:" + Uri.EscapeDataString(imageId), StringComparison.Ordinal) &&
                html.Contains("data-attachment='" + imageId + "'", StringComparison.Ordinal) &&
                !presentation.Contains(sourceImage, StringComparison.Ordinal),
                "a Markdown path with spaces becomes an inline Android preview");
            check(images.Resolve(snapshot.Id, imageId) == imagePath && images.Resolve("another-session", imageId) is null,
                "the registered image can be fetched only by its owning session");
            check(presentation.Contains(outsideImage, StringComparison.Ordinal) && presentation.Contains(archiveLink, StringComparison.Ordinal) &&
                snapshot.Messages.Single().Text == text,
                "the projection keeps other paths and persisted conversation text unchanged");

            string regularProject = fixture.CreateSubdirectory("DesktopMaid").FullName;
            string regularImage = Path.Combine(regularProject, "catalog-signal-0.png");
            File.WriteAllBytes(regularImage, png);
            string regularReference = $"![实机截图]({MarkdownPath(regularImage)})";
            var history = snapshot with { Cwd = regularProject, Messages = [new("assistant", regularReference),
                .. Enumerable.Range(0, HistoryPaging.BatchSize).Select(i => new ChatEntry("assistant", $"Recent {i}"))] };
            var full = images.Project(history);
            var latest = HistoryPaging.Latest(full);
            var older = HistoryPaging.Page(full, latest.History!.Start, latest.History.Revision);
            var regularAttachment = latest.OutputFiles!.Single();
            check(older.Messages.Single().Text.Contains("attachment:" + Uri.EscapeDataString(regularAttachment.Id), StringComparison.Ordinal),
                "an older /drive:/project screenshot is registered before pagination");

            using var store = new FileTransferStore(Path.Combine(fixture.FullName, "files"));
            var stat = Json.Read<RemoteFile>(store.HandleAsync("files/stat", Json.Node(new { session = history.Id, id = regularAttachment.Id }), images, default).GetAwaiter().GetResult());
            var chunk = store.HandleAsync("files/read", Json.Node(new { session = history.Id, id = regularAttachment.Id, offset = 0L, count = png.Length }), images, default).GetAwaiter().GetResult();
            check(stat.Version == regularAttachment.Version && Convert.FromBase64String(chunk["data"]!.GetValue<string>()).AsSpan().SequenceEqual(png),
                "the registered history image uses the original file bytes and cache version");
        }
        finally { fixture.Delete(recursive: true); }
    }
}
