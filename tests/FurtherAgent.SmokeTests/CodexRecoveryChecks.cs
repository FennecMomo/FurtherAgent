using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class CodexRecoveryChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(AgentIndicator.From(true, false, new SessionView("legacy", "legacy", "", "systemError", null, [], [])).Label == "执行异常",
            "legacy host systemError is recognized directly by controllers");
        var state = JsonNode.Parse("""{"id":"thread","title":"test","cwd":"C:/test","threadRuntimeStatus":{"type":"systemError"},"turns":[{"turnId":"old","status":"inProgress","items":[]},{"turnId":"failed","status":"failed","error":{"message":"capacity","codexErrorInfo":"serverOverloaded"},"items":[{"type":"error","message":"capacity","errorInfo":"serverOverloaded","willRetry":false}]}]}""")!;
        var view = CodexDesktopAdapter.ToView(state);
        check(view.Status == "error" && view.TurnId is null && view.Messages.Any(m => m.Role == "系统" && m.Text.Contains("容量拥堵")), "terminal capacity error is visible and old running history does not block recovery");
        check(view.Prompts.Single().Kind == CodexRecovery.Kind && CodexMessageSubmission.Build(view, CodexRecovery.ContinueText).Method == "thread-follower-start-turn", "explicit recovery starts a new turn rather than steering a failed turn");
        state["turns"]![1]!["error"]!["codexErrorInfo"] = "usageLimitExceeded";
        check(CodexDesktopAdapter.ToView(state).Prompts.Single().Detail.Contains("等待额度恢复"), "quota exhaustion explains waiting or switching models");
        state["turns"]![1]!["status"] = "inProgress";
        state["threadRuntimeStatus"]!["type"] = "active";
        check(CodexDesktopAdapter.ToView(state).Prompts.Count == 0, "running automatic retry offers no duplicate manual recovery");
        state["turns"]![1]!["status"] = "completed";
        state["threadRuntimeStatus"]!["type"] = "idle";
        check(CodexDesktopAdapter.ToView(state).Prompts.Count == 0, "completed turn offers no stale recovery");
        state["turns"]![1]!["status"] = "failed";
        state["turns"]![1]!["error"]!["codexErrorInfo"] = "other";
        check(CodexDesktopAdapter.ToView(state).Prompts.Count == 0, "unrecognized failures do not receive a guessed recovery action");
    }
}
