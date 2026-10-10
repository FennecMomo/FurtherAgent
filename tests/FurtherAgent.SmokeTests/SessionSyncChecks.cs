using FurtherAgent.Core;
using System.Text;
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
}
