using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public static class CodexRecovery
{
    public const string Kind = "codex/recoverTurn";
    public const string ContinueText = "请接着完成刚才因模型服务限制而中断的任务，先核对已有结果，避免重复执行已经完成的操作。";
    public static string ErrorText(JsonNode? error)
    {
        string code = CodexDesktopAdapter.Text(error?["errorInfo"] ?? error?["codexErrorInfo"]);
        string message = CodexDesktopAdapter.Text(error?["message"]);
        string hint = code switch
        {
            "usageLimitExceeded" => "模型使用额度已达上限。请等待额度恢复，或在模型设置中选择仍可用的模型，再手动继续任务。",
            "serverOverloaded" => "模型服务容量拥堵，当前请求未能完成。可以稍后手动继续，或切换可用模型。",
            _ => ""
        };
        return hint.Length == 0 ? message : hint + (message.Length == 0 ? "" : "\n" + message);
    }
    public static PendingPrompt? Pending(JsonNode? latest, string runtime)
    {
        if (latest is null || runtime == "active" || CodexDesktopAdapter.Text(latest["status"]) != "failed") return null;
        var error = latest["error"] ?? (latest["items"] as JsonArray)?.LastOrDefault(i => CodexDesktopAdapter.Text(i?["type"]) == "error");
        string code = CodexDesktopAdapter.Text(error?["codexErrorInfo"] ?? error?["errorInfo"]);
        string id = CodexDesktopAdapter.Text(latest["turnId"]);
        if (id.Length == 0 || code is not ("usageLimitExceeded" or "serverOverloaded")) return null;
        return new("codex-recovery:" + id, Kind, code == "usageLimitExceeded" ? "模型额度已用尽" : "模型服务拥堵",
            ErrorText(error) + "\n继续会发送一条继续任务的消息，开始新一轮；不会自动重放原始请求。", null);
    }
}
