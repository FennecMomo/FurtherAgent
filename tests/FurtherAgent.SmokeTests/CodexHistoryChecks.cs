using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class CodexHistoryChecks
{
    public static async Task RunLive(string id, Action<bool, string> check)
    {
        await using var adapter = new CodexDesktopAdapter();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var catalog = await adapter.ListAsync(stop.Token);
        var selected = catalog.Single(s => s.Id == id);
        check(!string.IsNullOrWhiteSpace(selected.Cwd), "live catalog exposes workspace for target conversation");
        var full = await adapter.AttachAsync(id, stop.Token);
        var page = HistoryPaging.Latest(full);
        string html = FurtherAgent.Mobile.Transcript.Render(page, page.History?.Start > 0);
        check(page.Id == id && page.Messages.Count <= 8 && html.Length < 200000, "target live conversation produces bounded initial mobile page");
        var older = HistoryPaging.Page(full, page.History!.Start, page.History.Revision);
        check(older.End == page.History.Start && older.Messages.Count <= 8, "target live conversation serves adjacent older page");
        Console.WriteLine($"LIVE: {full.Messages.Count} total entries; {page.Messages.Count} initial entries; {JsonSerializer.SerializeToUtf8Bytes(page, Json.Options).Length} wire bytes; {html.Length} HTML characters; workspace={selected.Cwd}");
    }
    public static async Task Run(Action<bool, string> check)
    {
        var items = new JsonArray();
        for (int i = 0; i < 650; i++)
            items.Add(new JsonObject { ["type"] = "agentMessage", ["text"] = $"{i:D4}:" + new string('鲸', 12000) });
        var state = new JsonObject
        {
            ["id"] = "long", ["threadRuntimeStatus"] = new JsonObject { ["type"] = "active" },
            ["latestModel"] = "model",
            ["turns"] = new JsonArray(new JsonObject { ["turnId"] = "turn", ["status"] = "inProgress", ["items"] = items }),
            ["requests"] = new JsonArray(new JsonObject { ["id"] = "approval", ["method"] = "item/commandExecution/requestApproval" })
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(state, Json.Options);
        check(payload.Length > Frames.MaxBytes, "fixture exceeds 32 MiB on the wire");
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header); stream.Write(payload);
        await Frames.WriteAsync(stream, Json.Node(new { next = true }), default);
        stream.Position = 0;
        var parsed = await Frames.ReadDesktopAsync(stream, default);
        check((await Frames.ReadAsync(stream, default))["next"]!.GetValue<bool>(), "large desktop frame does not consume next frame");
        var full = CodexDesktopAdapter.ToView(parsed);
        var view = HistoryPaging.Latest(full);
        check(JsonSerializer.SerializeToUtf8Bytes(view.Messages, Json.Options).Length <= HistoryPaging.MaxPageBytes,
            "escaped Unicode page fits the smaller serialized budget");
        check(view.Messages.Count is > 0 and <= 8 && full.Messages.Count == 650 && view.Messages[^1].Text.StartsWith("0649:"),
            "first page holds the bounded latest records while host retains older records");
        int first = int.Parse(view.Messages[0].Text[..4]);
        check(view.Messages.Select((m, i) => m.Text.StartsWith($"{first + i:D4}:")).All(v => v), "retained history stays chronological");
        check(view.History?.Start == 650 - view.Messages.Count && view.TurnId == "turn" && view.Prompts.Single().Id == "approval" && view.Model == "model",
            "truncation preserves active turn, approval and model metadata");
        using (var relayFrame = new MemoryStream())
        {
            await Frames.WriteAsync(relayFrame, Json.Node(new { id = "attach", result = view }), default);
            relayFrame.Position = 0;
            check((await Frames.ReadAsync(relayFrame, default))["result"]!["id"]!.ToString() == "long",
                "trimmed snapshot and metadata fit ordinary relay frame");
        }
        StatePatches.Apply(parsed, JsonNode.Parse("""[{"op":"replace","path":["turns",0,"items",0,"text"],"value":"old updated"},{"op":"replace","path":["turns",0,"items",649,"text"],"value":"latest updated"}]""")!.AsArray());
        bool stale = false;
        try { HistoryPaging.Page(CodexDesktopAdapter.ToView(parsed), view.History!.Start, view.History.Revision); } catch (IOException) { stale = true; }
        check(stale, "rewritten history rejects stale page cursor");
        int before = 650, count = 0;
        while (before > 0) { var page = HistoryPaging.Page(full, before); count += page.Messages.Count; before = page.Start; }
        check(count == 650, "pagination reaches complete history beyond former 20 MiB limit without gaps");
        check(CodexDesktopAdapter.ToView(parsed).Messages[^1].Text == "latest updated", "patches to old and retained items remain valid after truncation");
        var modern = new JsonObject { ["turnHistory"] = new JsonObject { ["history"] = new JsonObject { ["entitiesByKey"] = new JsonObject
        {
            ["new"] = new JsonObject { ["turnStartedAtMs"] = 2d, ["items"] = new JsonArray(new JsonObject { ["type"] = "agentMessage", ["text"] = "new" }) },
            ["old"] = new JsonObject { ["turnStartedAtMs"] = 1d, ["items"] = new JsonArray(new JsonObject { ["type"] = "userMessage", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "old" }) }) }
        } } } };
        var small = CodexDesktopAdapter.ToView(modern);
        check(small.Messages.Select(m => m.Text).SequenceEqual(new[] { "old", "new" }) && small.StatusDetail is null, "small modern history stays complete and ordered");
        await RunStateStreamChecks(modern, check);
        stream.Position = 0;
        bool rejected = false;
        try { await Frames.ReadAsync(stream, default); } catch (IOException) { rejected = true; }
        check(rejected, "ordinary transport still rejects over 32 MiB");
        using var shortFrame = new MemoryStream(new byte[] { 10, 0, 0, 0, (byte)'{', (byte)'}' });
        rejected = false;
        try { await Frames.ReadDesktopAsync(shortFrame, default); } catch (IOException) { rejected = true; }
        check(rejected, "truncated desktop frame fails without hanging");
    }

    private static async Task RunStateStreamChecks(JsonObject modern, Action<bool, string> check)
    {
        const string thread = "stream-test";
        const string desktopOwner = "desktop-test";
        const BindingFlags instanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;
        var adapterType = typeof(CodexDesktopAdapter);
        var dispatch = adapterType.GetMethod("BroadcastAsync", instanceMembers)!;
        foreach (int version in new[] { 11, 13 })
        {
            // Set the result of owner discovery without opening the real desktop pipe.
            await using var adapter = new CodexDesktopAdapter();
            adapterType.GetField("sessionId", instanceMembers)!.SetValue(adapter, thread);
            adapterType.GetField("owner", instanceMembers)!.SetValue(adapter, desktopOwner);
            var broadcast = dispatch.CreateDelegate<Func<JsonNode, Task>>(adapter);
            var updates = new List<SessionView>();
            adapter.Changed += updates.Add;

            Task Receive(int streamVersion, object change) => broadcast(Json.Node(new
            {
                type = "broadcast", method = "thread-stream-state-changed", sourceClientId = desktopOwner,
                version = streamVersion, @params = new { hostId = "local", conversationId = thread, change }
            }));

            var state = version == 11
                ? JsonNode.Parse("""{"turns":[{"items":[{"type":"agentMessage","text":"legacy"}]}]}""")!.AsObject()
                : modern.DeepClone().AsObject();
            state["id"] = thread;
            if (version == 13) state["turns"] = new JsonArray();
            var snapshot = new { type = "snapshot", revision = 1, conversationState = state };
            await Receive(version, snapshot);
            string[] expected = version == 11 ? ["legacy"] : ["old", "new"];
            check(updates is [{ Id: thread } view] && view.Messages.Select(m => m.Text).SequenceEqual(expected),
                $"Codex v{version} stream publishes an ordered snapshot");
            if (version != 13) continue;

            await Receive(13, new
            {
                type = "patches", baseRevision = 1, revision = 2,
                patches = JsonNode.Parse("""[{"op":"replace","path":["turnHistory","history","entitiesByKey","new","items",0,"text"],"value":"updated"}]""")
            });
            check(updates.Count == 2 && updates[^1].Messages.Select(m => m.Text).SequenceEqual(new[] { "old", "updated" }),
                "Codex v13 stream publishes a patch against its snapshot revision");

            bool rejected = false;
            try { await Receive(14, snapshot); }
            catch (IOException error) when (error.Message.Contains("协议版本", StringComparison.Ordinal)) { rejected = true; }
            check(rejected && updates.Count == 2, "unknown Codex stream version is rejected before publishing an update");
        }
    }
}

