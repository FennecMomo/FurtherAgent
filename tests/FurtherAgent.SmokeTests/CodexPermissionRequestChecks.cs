using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class CodexPermissionRequestChecks
{
    public static void Run(Action<bool, string> check)
    {
        var parameters = JsonNode.Parse("""
            {
              "threadId":"permission-thread","turnId":"permission-turn","itemId":"permission-item",
              "cwd":"E:/permission-fixture","reason":"读取参考文件并导出报告，需要联网核对结果。",
              "permissions":{
                "network":{"enabled":true},
                "fileSystem":{
                  "read":["C:/reference"],"write":["E:/output"],
                  "entries":[
                    {"access":"write","path":{"type":"path","path":"E:/reports"}},
                    {"access":"read","path":{"type":"glob_pattern","pattern":"E:/assets/**/*.png"}},
                    {"access":"read","path":{"type":"special","value":{"kind":"project_roots","subpath":"Assets"}}},
                    {"access":"deny","path":{"type":"special","value":{"kind":"root"}}}
                  ],
                  "globScanMaxDepth":3
                }
              }
            }
            """)!;
        var state = JsonNode.Parse("""{"id":"permission-thread","requests":[],"turns":[]}""")!;
        state["requests"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "permission-request", ["method"] = CodexPermissionRequests.Kind,
            ["params"] = parameters.DeepClone()
        });
        var prompt = CodexDesktopAdapter.ToView(state).Prompts.Single();
        check(prompt.Id == "permission-request" && prompt.Kind == CodexPermissionRequests.Kind &&
            prompt.Title.Contains("权限", StringComparison.Ordinal) && prompt.Detail.Contains(parameters["reason"]!.ToString(), StringComparison.Ordinal),
            "native permission requests project as a readable approval with the original request id and reason");
        check(prompt.Detail.Contains("网络", StringComparison.Ordinal) &&
            prompt.Detail.Contains("C:/reference", StringComparison.Ordinal) && prompt.Detail.Contains("E:/output", StringComparison.Ordinal),
            "approval details show requested network access and legacy read and write locations");
        check(prompt.Detail.Contains("E:/reports", StringComparison.Ordinal) &&
            prompt.Detail.Contains("E:/assets/**/*.png", StringComparison.Ordinal) && prompt.Detail.Contains("Assets", StringComparison.Ordinal),
            "approval details preserve explicit file paths, glob patterns and special project subpaths");
        check(JsonNode.DeepEquals(prompt.Payload, parameters),
            "readable projection retains the complete native parameters for the host-owned request");

        var granted = CodexPermissionRequests.BuildResponse(parameters, "accept");
        check(granted["scope"]?.ToString() == "turn" && granted.Count == 2 &&
            JsonNode.DeepEquals(granted["permissions"], parameters["permissions"]),
            "allow-once grants exactly the requested permission profile for the current turn");
        granted["permissions"]!["network"]!["enabled"] = false;
        check(parameters["permissions"]!["network"]!["enabled"]!.GetValue<bool>() &&
            prompt.Payload!["permissions"]!["network"]!["enabled"]!.GetValue<bool>(),
            "native response permissions are cloned without changing the pending request or its displayed payload");

        var injected = parameters.DeepClone();
        injected["scope"] = "session";
        injected["strictAutoReview"] = false;
        injected["response"] = JsonNode.Parse("""{"permissions":{"network":{"enabled":true},"fileSystem":{"write":["C:/"]}},"scope":"session"}""");
        var bounded = CodexPermissionRequests.BuildResponse(injected, "accept");
        check(bounded["scope"]?.ToString() == "turn" && bounded["strictAutoReview"] is null &&
            JsonNode.DeepEquals(bounded["permissions"], parameters["permissions"]),
            "extra response and scope fields cannot extend an allow-once decision to additional paths or the whole session");
        var sessionGrant = CodexPermissionRequests.BuildResponse(injected, "acceptForSession");
        check(sessionGrant["scope"]?.ToString() == "session" && sessionGrant.Count == 2 &&
            JsonNode.DeepEquals(sessionGrant["permissions"], parameters["permissions"]),
            "explicit session approval grants exactly the requested permission profile while ignoring injected response fields");
        sessionGrant["permissions"]!["fileSystem"]!["write"]!.AsArray().Add("C:/unrequested");
        check(parameters["permissions"]!["fileSystem"]!["write"]!.AsArray().Count == 1 &&
            prompt.Payload!["permissions"]!["fileSystem"]!["write"]!.AsArray().Count == 1 &&
            CodexPermissionRequests.BuildResponse(parameters, "accept")["scope"]?.ToString() == "turn",
            "session approval clones its permissions and does not change the source request or later allow-once decisions");
        var denied = CodexPermissionRequests.BuildResponse(parameters, "decline");
        check(denied["permissions"] is JsonObject { Count: 0 } && denied["scope"]?.ToString() == "turn",
            "rejecting a permission request returns an empty permission grant");

        bool invalid = new[] { "acceptForever", "always", "cancel", "", "injected" }
            .All(decision => Rejects(() => CodexPermissionRequests.BuildResponse(parameters, decision)));
        check(invalid, "unsupported permission decisions are rejected before preparing a native response");
        check(new JsonNode?[] { null, new JsonObject(), JsonNode.Parse("""{"permissions":true}"""), JsonNode.Parse("""{"permissions":[]}""") }
            .All(input => new[] { "accept", "acceptForSession" }
                .All(decision => Rejects(() => CodexPermissionRequests.BuildResponse(input, decision)))),
            "turn and session approval both reject a missing or malformed requested permission profile");
        check(CodexPermissionRequests.BuildResponse(null, "decline")["permissions"] is JsonObject { Count: 0 },
            "a malformed request can still be rejected without granting permissions");

        prompt.Payload!["permissions"]!["network"]!["enabled"] = false;
        check(state["requests"]![0]!["params"]!["permissions"]!["network"]!["enabled"]!.GetValue<bool>(),
            "client-facing prompt payload is detached from the native pending request");

        state["requests"] = JsonNode.Parse("""
            [
              {"id":"command","method":"item/commandExecution/requestApproval","params":{"command":"echo fixture"}},
              {"id":"file","method":"item/fileChange/requestApproval","params":{"reason":"fixture"}},
              {"id":"question","method":"item/tool/requestUserInput","params":{"questions":[{"id":"q","question":"fixture"}]}},
              {"id":"unknown","method":"future/approval","params":{"fixture":true}}
            ]
            """);
        var existing = CodexDesktopAdapter.ToView(state).Prompts;
        check(existing.Select(p => p.Kind).SequenceEqual(new[]
            { "item/commandExecution/requestApproval", "item/fileChange/requestApproval", "item/tool/requestUserInput", "future/approval" }) &&
            existing[0].Payload?["command"]?.ToString() == "echo fixture" && existing[3].Payload?["fixture"]?.GetValue<bool>() == true,
            "existing command, file, question and unknown request kinds retain their original projection");
    }

    private static bool Rejects(Func<JsonObject> action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }
}
