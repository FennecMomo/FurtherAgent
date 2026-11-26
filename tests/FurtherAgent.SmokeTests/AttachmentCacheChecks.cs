using FurtherAgent.Core;

internal static class AttachmentCacheChecks
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "FurtherAgent-cache-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new DownloadedAttachments(root);
            var file = new RemoteFile("outbox/result.txt", "result.txt", 3, Version: "v1");
            string key = DownloadedAttachments.Key("device-a", "session-a", file);
            string path = cache.NewPath(file.Name);
            File.WriteAllText(path, "abc");
            check(cache.Find(key) is null, "Unrecorded download is never reused");
            cache.Remember(key, path);
            check(new DownloadedAttachments(root).Find(key) == path, "Completed download survives cache recreation");
            check(cache.Find(DownloadedAttachments.Key("device-b", "session-a", file)) is null &&
                  cache.Find(DownloadedAttachments.Key("device-a", "session-b", file)) is null,
                  "Same filenames are isolated across connections and sessions");
            check(cache.Find(DownloadedAttachments.Key("device-a", "session-a", file with { Version = "v2" })) is null,
                  "New remote version requires a fresh download");
            File.WriteAllText(path, "truncated");
            check(cache.Find(key) is null, "Changed local size is not reused");
            File.Delete(path);
            check(cache.Find(key) is null, "Deleted local attachment requires a fresh download");

            string outbox = Path.Combine(root, "outbox");
            var sent = new RemoteFile("inbox/" + new string('a', 32) + "__sent.png", "sent.png", 21, Version: "v1");
            var unsent = new RemoteFile("inbox/" + new string('b', 32) + "__draft.png", "draft.png", 55, Version: "v1");
            var result = new RemoteFile("outbox/result.png", "result.png", 32, Version: "v2");
            string sentPath = Path.Combine(root, "inbox", Path.GetFileName(sent.Id));
            string sentText = "收到这张图" + ChatPresentation.UploadFooter + Json.Node(sentPath).ToJsonString();
            string externalText = "无关路径" + ChatPresentation.UploadFooter + Json.Node(Path.Combine(root, "elsewhere", Path.GetFileName(unsent.Id))).ToJsonString();
            var view = new SessionView("session-a", "", "", "idle", null,
                [new("user", sentText), new("user", externalText)], []);
            var linked = ChatPresentation.IncludeFiles(view, new FileWorkspace(outbox, FileTransferStore.MaxFileBytes,
                FileTransferStore.ChunkBytes, [sent, unsent, result]));
            check(linked.OutputFiles?.Select(f => f.Id).SequenceEqual([result.Id, sent.Id]) == true &&
                linked.Messages[0].Text == "收到这张图\n\n![图片](attachment:" + Uri.EscapeDataString(sent.Id) + ")",
                "chat includes only verified already-sent inbox image links alongside output metadata");
            check(linked.Messages[1].Text == externalText && view.Messages[0].Text == sentText,
                "unrelated paths and the underlying persisted chat are left intact");
            var sameNameOutput = result with { Id = "outbox/sent.png", Name = sent.Name };
            check(RemoteFileReferences.Resolve([sent, sameNameOutput], "attachment:sent.png") == sameNameOutput &&
                RemoteFileReferences.Resolve([sent, sameNameOutput], "attachment:" + Uri.EscapeDataString(sent.Id)) == sent,
                "an existing output filename link stays compatible with explicitly addressed sent images");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
