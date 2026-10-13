namespace FurtherAgent.Core;

public record AgentIndicator(string Label, string Color)
{
    public static AgentIndicator From(bool connected, bool connecting, SessionView? view)
    {
        if (!connected) return connecting ? new("连接中", "#D97706") : new("已断连", "#DC2626");
        if (view is null) return new("已连接 · 未选会话", "#64748B");
        if (view.Prompts.Count > 0) return new("等待确认", "#D97706");
        return view.Status switch
        {
            "active" or "running" or "inProgress" or "in_progress" => new("执行中", "#2563EB"),
            "idle" or "ready" or "completed" or "stopped" or "interrupted" => new("空闲", "#16A34A"),
            "error" or "failed" => new("执行异常", "#DC2626"),
            _ => new("状态未知", "#64748B")
        };
    }
}
