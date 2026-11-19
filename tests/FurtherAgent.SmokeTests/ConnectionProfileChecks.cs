using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class ConnectionProfileChecks
{
    public static async Task RunBuiltIn(Action<bool, string> check)
    {
        string? saved = null;
        var store = new ConnectionProfiles(() => Task.FromResult(saved), value => { saved = value; return Task.CompletedTask; });
        var legacy = await store.SaveAsync("原中继设备", RelayEndpoint.LegacyAddress, "ABCD-1234");
        var current = await store.SaveAsync("直连设备", RelayEndpoint.Address, "4321-DCBA");
        var unrelated = await store.SaveAsync("独立设备", "https://unrelated.example", "AABB-1234");
        string? beforeLoad = saved;
        var eligible = (await store.LoadAsync()).ForBuiltInRelay();
        check(eligible.Connections.SequenceEqual([legacy, current]) && eligible.SelectedId is null,
            "the Android built-in relay view excludes unrelated profiles and their selection without changing legacy IDs");
        check(saved == beforeLoad && (await store.LoadAsync()).Connections.Contains(unrelated),
            "loading Android built-in relay profiles leaves unrelated encrypted records untouched");
        var selected = await store.SaveAsync(legacy.Name, legacy.Server, legacy.Code, legacy.Id);
        eligible = (await store.LoadAsync()).ForBuiltInRelay();
        check(selected == legacy && eligible.SelectedId == legacy.Id,
            "reusing a saved Android device preserves its old selection and local settings namespace");
    }

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
