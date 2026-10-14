using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class CodexInteractionChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var state = JsonNode.Parse("""{"id":"test","turns":[{"turnId":"active","status":"inProgress","items":[{"type":"userMessage","id":"old","clientId":"old-client","content":[{"type":"text","text":"相同文本"}]},{"type":"steeringUserMessage","id":"steer","status":"accepted","serverUserMessageId":"server-new","clientUserMessageId":"new-client","input":[{"type":"text","text":"相同文本"}]}]}]}""")!;
        var items = state["turns"]![0]!["items"]!.AsArray();
        var steering = items[1]!;
        check(CodexDesktopAdapter.ToView(state).Messages.Count(m => m.Role == "你") == 2, "accepted steering appears as a user bubble even when its text matches an earlier message");
        check(CodexSubmissionReceipt.Contains(state, "new-client") && !CodexSubmissionReceipt.Contains(state, "different"), "native receipts require the unique submission id, not matching text");
        steering["status"] = "pending";
        check(!CodexSubmissionReceipt.Contains(state, "new-client") && CodexDesktopAdapter.ToView(state).Messages.Count == 1, "pending steering cannot falsely acknowledge delivery");
        steering["status"] = "failed";
        check(!CodexSubmissionReceipt.Contains(state, "new-client"), "failed steering cannot clear a draft");
        steering["status"] = "accepted";
        items.Add(JsonNode.Parse("""{"type":"userMessage","id":"server-new","clientId":"new-client","content":[{"type":"text","text":"相同文本"}]}"""));
        check(CodexDesktopAdapter.ToView(state).Messages.Count == 2, "normal user item and steering echo render only once using their shared identity");
        var indexed = Json.Node(new { turnHistory = new { history = new { entitiesByKey = new { one = state["turns"]![0]!.DeepClone() } } } });
        check(CodexSubmissionReceipt.Contains(indexed, "new-client"), "receipts also follow indexed desktop history");

        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool echoed = false;
        var waiting = CodexSubmissionReceipt.WaitAsync(slow.Task, () => echoed, default);
        echoed = true;
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        check(!slow.Task.IsCompleted, "accepted echo completes submission without waiting for a delayed native response");
        slow.SetException(new IOException("late native timeout"));
        await CodexSubmissionReceipt.WaitAsync(Task.CompletedTask, () => false, default);
        check(true, "ordinary acknowledgement still succeeds without an echo");
        bool rejected = false;
        try { await CodexSubmissionReceipt.WaitAsync(Task.FromException(new IOException("rejected")), () => false, default); }
        catch (IOException) { rejected = true; }
        check(rejected, "rejection without an accepted echo preserves failure for the draft owner");
        await CodexSubmissionReceipt.WaitAsync(Task.FromException(new IOException("lost reply")), () => true, default);
        check(true, "persisted echo wins over a lost response without resending");
        check(CodexSubmissionReceipt.RequestError("thread-follower-start-turn-timeout") is TimeoutException
            && CodexSubmissionReceipt.RequestError("permission-denied") is IOException,
            "native timeout is distinct from a rejected request");
        echoed = false;
        var lateReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting = CodexSubmissionReceipt.WaitAsync(lateReceipt.Task, () => echoed, default, TimeSpan.FromSeconds(2));
        lateReceipt.SetException(CodexSubmissionReceipt.RequestError("thread-follower-start-turn-timeout"));
        await Task.Delay(150);
        check(!waiting.IsCompleted, "native timeout keeps the original submission pending for reconciliation");
        echoed = true;
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        check(true, "accepted echo after native timeout succeeds so the client can clear its submitted draft");
        rejected = false;
        try { await CodexSubmissionReceipt.WaitAsync(Task.FromException(new TimeoutException()), () => false,
            default, TimeSpan.FromMilliseconds(30)); }
        catch (IOException ex) { rejected = ex.Message.Contains("发送结果待确认") && ex.Message.Contains("草稿已保留"); }
        check(rejected, "missing late receipt stays uncertain and preserves draft instead of claiming rejection");
        using var lateCancel = new CancellationTokenSource(50);
        rejected = false;
        try { await CodexSubmissionReceipt.WaitAsync(Task.FromException(new TimeoutException()), () => false,
            lateCancel.Token, TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected, "late receipt reconciliation respects cancellation");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        rejected = false;
        try { await CodexSubmissionReceipt.WaitAsync(new TaskCompletionSource().Task, () => false, canceled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected, "receipt waiting respects cancellation");

        foreach (string permission in CodexPermissions.Values)
        {
            var update = CodexPermissions.Build(permission);
            check(CodexPermissions.Current(Json.Node(new { latestThreadSettings = update })) == permission, "permission preset roundtrip: " + permission);
            check(update["model"] is null && update["collaborationMode"] is null && update["effort"] is null, "permission change preserves model/mode/effort: " + permission);
        }
        rejected = false;
        try { CodexPermissions.Build("custom-injected-value"); } catch (ArgumentException) { rejected = true; }
        check(rejected, "host rejects unsupported permission ids");
        check(CodexPermissions.Current(JsonNode.Parse("""{"currentPermissions":{"sandboxPolicy":{"type":"readOnly"},"approvalPolicy":"never"}}""")!) == ":read-only", "legacy sandbox permissions remain readable");
        check(CodexPermissions.Current(JsonNode.Parse("""{"latestThreadSettings":{"model":"m"},"currentPermissions":{"activePermissionProfile":{"id":":workspace"},"approvalPolicy":"on-request"}}""")!) == ":workspace", "partial thread settings fall back to current native permissions");
        check(CodexPermissions.Current(JsonNode.Parse("""{"latestThreadSettings":{"permissions":"organization-policy"}}""")!).StartsWith("自定义"), "custom profile remains explicit and is not mapped to full access");
        check(CodexPermissions.Current(JsonNode.Parse("""{"latestThreadSettings":{"permissions":":danger-full-access","approvalPolicy":"on-request"}}""")!).StartsWith("自定义"), "nonstandard approval policy is not mislabelled as a preset");
        var old = Json.Read<AgentSettings>(Json.Node(new { model = "m", provider = "", mode = "default", models = Array.Empty<ModelOption>(), canChangeModel = false, canChangeMode = false, detail = "legacy" }));
        check(!old.CanChangePermissions && old.Permission is null, "old hosts do not advertise unsupported permission controls");
    }
}
