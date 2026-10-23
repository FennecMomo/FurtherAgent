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
    }
}
