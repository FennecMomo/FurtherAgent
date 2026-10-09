namespace FurtherAgent.Core;

/// <summary>Stop an owned Hermes run, confirm its end, then submit once on the same connection.</summary>
public static class SessionActions
{
    public static async Task InterruptAndSendAsync(RpcPeer peer, AgentInfo agent, SessionView expected,
        string text, IEnumerable<string> fileIds, Action<string>? progress = null,
        CancellationToken ct = default, TimeSpan? stopTimeout = null)
    {
        var ids = fileIds.ToArray();
        if (agent.Id != "hermes" || expected.CanStop != true || string.IsNullOrEmpty(expected.TurnId))
            throw new InvalidOperationException("只能打断本接入端拥有且可停止的 Hermes 运行；其他入口的运行无法接管。");
        if (string.IsNullOrWhiteSpace(text) && ids.Length == 0) throw new ArgumentException("请输入新消息。");
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(stopTimeout ?? TimeSpan.FromSeconds(90));
            try
            {
                async Task<SessionView> Read()
                {
                    var view = Json.Read<SessionView>(await peer.CallAsync("attach", new { id = expected.Id }, deadline.Token));
                    if (view.Id != expected.Id || view.TurnId != expected.TurnId)
                        throw new IOException("远端运行已变化，未发送新消息。请刷新后确认。");
                    return view;
                }
                progress?.Invoke("正在确认要打断的运行…");
                var initial = await Read();
                if (initial.CanStop != true) throw new IOException("该运行已不可停止，未发送新消息。请刷新后确认。");
                progress?.Invoke("正在停止当前运行…");
                await peer.CallAsync("interrupt", null, deadline.Token);
                progress?.Invoke("等待远端确认运行结束；新消息尚未发送…");
                while (true)
                {
                    var view = await Read();
                    // Hermes retains the owned run ID after termination. Stopping/unavailable views cannot send.
                    if (view.CanStop == false && view.CanSend && view.Status is not ("active" or "running")) break;
                    await Task.Delay(500, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("停止确认超时，新消息未发送；请检查远端运行状态。"); }
        }
        ct.ThrowIfCancellationRequested();
        progress?.Invoke("原运行已结束，正在发送新消息…");
        try
        {
            if (agent.CanFiles) await new FileTransferClient(peer).SendAsync(expected.Id, text, ids, ct, TimeSpan.FromMinutes(3));
            else await peer.CallAsync("send", new { text }, ct, TimeSpan.FromMinutes(3));
        }
        catch (TimeoutException)
        { throw new IOException("新消息确认超时，发送结果未知；程序没有重发，请先查看会话结果。"); }
    }
}
