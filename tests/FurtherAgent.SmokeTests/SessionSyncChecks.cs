using FurtherAgent.Core;
using System.Text;
using System.Text.Json.Nodes;
internal static class SessionSyncChecks
{
    public static void Run(Action<bool,string> check)
    {
        var encoder = new SessionSyncEncoder(); var decoder = new SessionSyncDecoder();
        SessionView view = new("one", "history", "", "active", "run", Enumerable.Range(0,250).Select(i=>new ChatEntry("assistant", new string('文',2000)+i)).ToList(), []);
        var first = encoder.Encode(view)!;
        check(Json.Node(decoder.Apply(first)).ToJsonString() == Json.Node(view).ToJsonString(), "sync initial full history round trip");
        check(encoder.Encode(view with { Messages=view.Messages.ToList() }) is null, "unchanged history produces zero notifications");
        long fullBytes=0, deltaBytes=0;
        for (int i=0;i<50;i++)
        {
            var updated=view with { Messages=[..view.Messages, new("assistant",new string('x',i+1))] };
            var delta=encoder.Encode(updated)!;
            fullBytes += Encoding.UTF8.GetByteCount(Json.Node(updated).ToJsonString());
            deltaBytes += Encoding.UTF8.GetByteCount(Json.Node(delta).ToJsonString());
            if(Json.Node(decoder.Apply(delta)).ToJsonString()!=Json.Node(updated).ToJsonString()) throw new Exception("Streaming delta mismatch");
        }
        check(deltaBytes < fullBytes/100, "streaming changes use less than 1% of repeated large history payload");
        view=view with { Messages=[new("assistant","history replaced")], Status="idle", OutputFiles=[new("outbox/result.png","result.png",70)] };
        check(decoder.Apply(encoder.Encode(view)!).OutputFiles?.Single().Name=="result.png", "history replacement and output file metadata survive delta");
        view=view with { Id="two", Messages=[] };
        check(decoder.Apply(encoder.Encode(view)!).Messages.Count==0,"session switch resets incremental baseline");
        bool rejected=false;
        try { decoder.Apply(new(99,88,0,view)); } catch(IOException) { rejected=true; }
        check(rejected,"out-of-order delta fails instead of corrupting conversation");
        check(new SessionSyncDecoder().Apply(new SessionSyncEncoder().Encode(view)!).Id=="two","reconnection begins with independent full baseline");
        Console.WriteLine($"Payload comparison: full={fullBytes:N0} bytes; delta={deltaBytes:N0} bytes (50 changes, excluding initial snapshot)");
    }

    public static void RunTextDeltas(Action<bool, string> check)
    {
        static SessionUpdate Wire(SessionUpdate update) => Json.Read<SessionUpdate>(JsonNode.Parse(Json.Node(update).ToJsonString()));
        static bool Equal(SessionView actual, SessionView expected) => Json.Node(actual).ToJsonString() == Json.Node(expected).ToJsonString();
        static int Bytes(SessionUpdate update) => Encoding.UTF8.GetByteCount(Json.Node(update).ToJsonString());
        var encoder = new SessionSyncEncoder(textDeltas: true);
        var decoder = new SessionSyncDecoder();
        var legacyEncoder = new SessionSyncEncoder();
        var legacyDecoder = new SessionSyncDecoder();
        SessionView view = new("stream", "Long reply", "", "active", "run",
            [new("user", "Please explain"), new("assistant", new string('文', 12000))], []);
        var initial = encoder.Encode(view)!;
        check(initial.BaseRevision == 0 && initial.Keep == 0 && initial.Append is null && Equal(decoder.Apply(Wire(initial)), view),
            "text sync starts with a full serialized snapshot");
        legacyDecoder.Apply(Wire(legacyEncoder.Encode(view)!));
        long legacyBytes = 0, textBytes = 0;
        bool everyAppend = true, everyRoundTrip = true, everyLegacyFull = true;
        for (int i = 0; i < 30; i++)
        {
            string suffix = $" 增加第{i}段😀\n";
            view = view with { Messages = [view.Messages[0], view.Messages[1] with { Text = view.Messages[1].Text + suffix }] };
            var delta = encoder.Encode(view)!;
            var legacy = legacyEncoder.Encode(view)!;
            everyAppend &= delta.Keep == 2 && delta.View.Messages.Count == 0 && delta.Append?.Text == suffix;
            everyLegacyFull &= legacy.Append is null && legacy.View.Messages.Single().Text == view.Messages[1].Text;
            everyRoundTrip &= Equal(decoder.Apply(Wire(delta)), view) && Equal(legacyDecoder.Apply(Wire(legacy)), view);
            legacyBytes += Bytes(legacy); textBytes += Bytes(delta);
        }
        check(everyAppend, "long reply growth sends only the new suffix including emoji and newline");
        check(everyLegacyFull && everyRoundTrip, "legacy message-tail packets and text packets reconstruct the same complete reply after JSON transport");
        check(textBytes < legacyBytes / 20, "long reply suffix packets use under 5% of legacy message-tail payload");
        check(encoder.Encode(view with { Messages = view.Messages.ToList() }) is null, "unchanged reconstructed reply produces no packet");

        view = view with { Status = "idle", StatusDetail = "finished", CanStop = false, Model = "model", OutputFiles = [new("outbox/result.png", "result.png", 42)] };
        var metadata = encoder.Encode(view)!;
        check(metadata.Append is null && metadata.View.Messages.Count == 0 && Equal(decoder.Apply(Wire(metadata)), view),
            "status and file metadata changes survive without retransmitting message text");
        var oldReply = view.Messages[1];
        view = view with { Messages = [view.Messages[0], oldReply with { Text = oldReply.Text + " final paragraph" }, new("assistant", "Follow-up")] };
        var appendedAndNew = encoder.Encode(view)!;
        check(appendedAndNew.Append is not null && appendedAndNew.View.Messages.Count == 1 && Equal(decoder.Apply(Wire(appendedAndNew)), view),
            "a text suffix and a new following message reconstruct in the correct order");

        void Fallback(ChatEntry replacement, string name)
        {
            view = view with { Messages = [view.Messages[0], replacement] };
            var update = encoder.Encode(view)!;
            check(update.Append is null && Equal(decoder.Apply(Wire(update)), view), name);
        }
        Fallback(new("assistant", "rewritten reply"), "reply rewrites and truncation fall back to a complete changed message");
        Fallback(new("assistant", "rewritten reply plus details", "new detail"), "changed detail is sent as a full message without dropping metadata");
        Fallback(new("system", "rewritten reply plus details extended", "new detail"), "role changes are not treated as assistant text appends");
        Fallback(new("system", "rewritten reply plus details extended with operation", "new detail", [new("tool", "tool output")]),
            "operation-bearing messages keep their complete operation data");

        view = view with { Messages = [new("assistant", "history window moved")], History = new("revision", 10, 11) };
        check(Equal(decoder.Apply(Wire(encoder.Encode(view)!)), view), "history-window replacement does not append text to the previous message");
        view = view with { Id = "other", Messages = [new("assistant", "Other session")] };
        var switched = encoder.Encode(view)!;
        check(switched.BaseRevision == 0 && switched.Append is null && Equal(decoder.Apply(Wire(switched)), view),
            "switching sessions resets the text baseline");
        view = view with { Messages = [new("assistant", view.Messages[0].Text + new string('x', 200))] };
        var reconnectEncoder = new SessionSyncEncoder(textDeltas: true);
        var reconnect = reconnectEncoder.Encode(view)!;
        var reconnectDecoder = new SessionSyncDecoder();
        check(reconnect.BaseRevision == 0 && reconnect.Append is null && Equal(reconnectDecoder.Apply(Wire(reconnect)), view),
            "reconnection restores the complete latest reply without relying on lost suffixes");
        view = view with { Messages = [new("assistant", view.Messages[0].Text + " next suffix")] };
        var valid = reconnectEncoder.Encode(view)!;
        bool rejected = false;
        try { reconnectDecoder.Apply(Wire(valid with { Append = valid.Append! with { Length = valid.Append!.Length + 1 } })); }
        catch (IOException) { rejected = true; }
        check(rejected && Equal(reconnectDecoder.Apply(Wire(valid)), view), "malformed suffix length is rejected without advancing the decoder baseline");
        Console.WriteLine($"Long reply payload: legacy={legacyBytes:N0} bytes; text delta={textBytes:N0} bytes; reduction={1d - (double)textBytes / legacyBytes:P1} (30 updates after baseline)");
    }

}
