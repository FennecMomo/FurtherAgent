namespace FurtherAgent.Core;

public record SessionCompletion(string EventId, string SessionId, string Title, string? TurnId);

// Observe state changes, not old completed history. One tracker belongs to one session adapter.
public sealed class SessionCompletionTracker
{
    private readonly object gate = new();
    private bool running, stopping;
    private string? turn, completedTurn;
    public void SuppressStoppedRun() { lock (gate) { if (running) stopping = true; } }
    public SessionCompletion? Observe(SessionView view)
    {
        lock (gate) return ObserveLocked(view);
    }
    private SessionCompletion? ObserveLocked(SessionView view)
    {
        if (view.Status is "active" or "running" or "inProgress" or "in_progress" or "queued" or "started" or "waiting_for_approval")
        {
            if (!running) turn = null;
            if (view.TurnId is { Length: > 0 } next)
            {
                if (turn is not null && turn != next) stopping = false;
                turn = next;
            }
            running = true; return null;
        }
        if (view.Status is "stopping") { stopping = true; return null; }
        if (view.Status is "failed" or "error" or "stopped" or "cancelled" or "interrupted")
        { running = stopping = false; completedTurn = turn; return null; }
        if (view.Status is not ("idle" or "ready" or "completed") || view.Prompts.Count > 0) return null;
        bool notify = running && !stopping && (turn is null || turn != completedTurn);
        if (running || stopping) completedTurn = turn;
        running = stopping = false;
        if (!notify) return null;
        return new(Guid.NewGuid().ToString("N"), view.Id, view.Title, turn);
    }
}
