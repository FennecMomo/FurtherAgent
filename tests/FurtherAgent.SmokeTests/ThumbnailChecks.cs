using FurtherAgent.Core;
internal static class ThumbnailChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"fa-thumbnail-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store=new FileTransferStore(root);
            var workspace=store.List("thumbnail-test");
            string path=Path.Combine(workspace.Outbox,"picture.png");
            var pixels=new byte[1600*1200*3]; new Random(12).NextBytes(pixels);
            using(var stream=File.Create(path)) new StbImageWriteSharp.ImageWriter().WritePng(pixels,1600,1200,StbImageWriteSharp.ColorComponents.RedGreenBlue,stream);
            var request=Json.Node(new { session="thumbnail-test",id="outbox/picture.png" });
            var result=Json.Read<FileThumbnail>(await store.HandleAsync("files/thumbnail",request,default));
            byte[] jpeg=Convert.FromBase64String(result.Data);
            var image=StbImageSharp.ImageResult.FromMemory(jpeg);
            check(image.Width<=320 && image.Height<=240 && jpeg.Length<=65536,"thumbnail is a bounded compressed JPEG, not original image");
            check(jpeg.Length<new FileInfo(path).Length/20,"thumbnail saves more than 95% for large image fixture");
            check((await store.HandleAsync("files/thumbnail",request,default))!.ToJsonString()==Json.Node(result).ToJsonString(),"unchanged thumbnail uses stable cache and version");
            var files=store.List("thumbnail-test").Files;
            check(files.Single().Version==result.Version,"output metadata carries thumbnail cache version");
            bool rejected=false;
            try { await store.HandleAsync("files/thumbnail",Json.Node(new { session="thumbnail-test",id="../../picture.png" }),default); } catch { rejected=true; }
            check(rejected,"thumbnail cannot bypass file workspace boundary");
            string wrong=Path.Combine(workspace.Outbox,"broken.png");await File.WriteAllTextAsync(wrong,"not an image");
            rejected=false;try{ImageThumbnail.Create(wrong,"1");}catch{rejected=true;}
            check(rejected,"invalid image fails without returning original data");
            File.Delete(path);File.Delete(wrong);
            Directory.Delete(workspace.Outbox);Directory.Delete(Path.Combine(Path.GetDirectoryName(workspace.Outbox)!,"inbox"));Directory.Delete(Path.GetDirectoryName(workspace.Outbox)!);
            Console.WriteLine($"Thumbnail fixture: original > 5 MB; JPEG {jpeg.Length:N0} bytes, {image.Width}x{image.Height}");
        }
        finally { if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); }
    }
}
