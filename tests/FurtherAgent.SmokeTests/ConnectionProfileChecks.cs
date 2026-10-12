using FurtherAgent.Mobile;

internal static class ConnectionProfileChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string? saved = null;
        ConnectionProfiles Store() => new(() => Task.FromResult(saved), value => { saved = value; return Task.CompletedTask; });
        var a = await Store().SaveAsync("办公室", "https://EXAMPLE.com", "abcd-1234");
        var b = await Store().SaveAsync("家里", "https://example.com", "EFGH5678");
        var state = await Store().LoadAsync();
        check(state.Connections.Count == 2 && state.SelectedId == b.Id && state.Connections.Any(p => p.Code == "abcd-1234"), "multiple devices on one relay persist across store recreation");
        var renamed = await Store().SaveAsync("办公电脑", "https://example.com/", "ABCD1234");
        check(renamed.Id == a.Id && (await Store().LoadAsync()).Connections.Count == 2, "normalized relay and pairing code deduplicate profiles");
        var replacement = await Store().SaveAsync("办公电脑", "https://second.example", "12345678", a.Id);
        state = await Store().LoadAsync();
        check(state.Connections.Count == 2 && state.Connections.Any(p => p.Id == b.Id) && !state.Connections.Any(p => p.Id == a.Id), "editing selected profile preserves other devices");
        await Store().RemoveAsync(replacement.Id);
        state = await Store().LoadAsync();
        check(state.Connections.Single().Id == b.Id && state.SelectedId is null, "deleting one connection preserves remaining credentials");
        saved = "broken";
        bool failed = false;
        try { await Store().SaveAsync("new", "https://example.com", "12345678"); } catch { failed = true; }
        check(failed && saved == "broken", "unreadable profile store is not overwritten");
    }
}
