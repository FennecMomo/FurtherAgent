using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class QuestionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var state = JsonNode.Parse("""{"id":"test","requests":[],"turns":[{"turnId":"t","status":"inProgress","items":[{"id":"call_test","type":"agentMessage","text":"","questions":[{"title":"选哪个？","options":["甲","乙"]},{"title":"补充说明"}]}]}]}""")!;
        var prompt = CodexDesktopAdapter.ToView(state).Prompts.Single();
        check(prompt.Kind == CodexQuestions.AsyncKind && prompt.Payload!["questions"]!.AsArray().Count == 2, "async questions surface even without requests or message text");
        string id = prompt.Payload!["questions"]![0]!["id"]!.ToString();
        check(id == "[\"request_user_input_async\",\"call_test\",0]" && prompt.Payload["questions"]![0]!["options"]![0]!["label"]!.ToString() == "甲", "desktop question IDs and string options are normalized");
        var answers = new JsonObject();
        foreach (var q in prompt.Payload["questions"]!.AsArray()) answers[q!["id"]!.ToString()] = new JsonObject { ["answers"] = new JsonArray("甲") };
        string text = CodexQuestions.Reply(prompt, new JsonObject { ["answers"] = answers });
        check(CodexQuestions.DisplayReply(text).Contains("回答：甲") && !CodexQuestions.DisplayReply(text).Contains("questionItemId"), "reply envelope projects as readable user answers");
        var items = state["turns"]![0]!["items"]!.AsArray();
        var steering = JsonNode.Parse("""{"type":"steeringUserMessage","status":"pending","input":[]}""")!;
        steering["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }); items.Add(steering);
        check(CodexDesktopAdapter.ToView(state).Prompts.Count == 1, "unaccepted steering does not hide pending questions");
        steering["status"] = "accepted";
        check(CodexDesktopAdapter.ToView(state).Prompts.Count == 0, "accepted remote reply clears every answered question");
        items.Remove(steering);
        state["turns"]!.AsArray().Add(JsonNode.Parse("""{"turnId":"next","items":[]}"""));
        check(CodexDesktopAdapter.ToView(state).Prompts.Count == 0, "later turn does not resurrect stale questions");
        bool rejected = false;
        try { CodexQuestions.Reply(prompt, new JsonObject()); } catch (ArgumentException) { rejected = true; }
        check(rejected, "empty answers are rejected before sending");
    }
    public static async Task Watch(string id, string output, int minutes = 10)
    {
        if (minutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(minutes), "测试连接时长必须为 1 到 1440 分钟。");
        using var registry = new RegistryClient("https://furtheragent.fennecmomo.top");
        await using var host = new DeviceHost(registry, () => new CodexDesktopAdapter());
        await host.StartAsync("Question 本机验证");
        await File.WriteAllTextAsync(output + ".code", host.Lease!.Code);
        await using var remote = new RemoteDevice();
        await remote.ConnectAsync(registry, host.Lease.Code);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        bool saw = false;
        void Record(SessionView view)
        {
            if (view.Prompts.Any(p => p.Kind == CodexQuestions.AsyncKind)) saw = true;
            File.WriteAllText(output, Json.Node(new { observedAt = DateTimeOffset.Now, sawQuestion = saw,
                prompts = view.Prompts, answers = view.Messages.Where(m => m.Role == "你" && m.Text.Contains("回答：")).TakeLast(2) }).ToJsonString());
        }
        remote.Notification += (method, value) => { if (method == "session") Record(Json.Read<SessionView>(value)); };
        Record(Json.Read<SessionView>(await remote.Peer.CallAsync("attach", new { id }, stop.Token)));
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
    }
}
