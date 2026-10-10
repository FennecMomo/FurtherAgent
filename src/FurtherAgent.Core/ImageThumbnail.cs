namespace FurtherAgent.Core;

public record FileThumbnail(string Data, int Width, int Height, string Version, string MediaType = "image/jpeg");
public static class ImageThumbnail
{
    public const int MaxEncodedBytes = 64 * 1024;
    public static bool Supported(string name) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }.Contains(Path.GetExtension(name).ToLowerInvariant());
    public static FileThumbnail Create(string path, string version)
    {
        if (!Supported(path) || new FileInfo(path).Length > 20 * 1024 * 1024) throw new IOException("缩略图支持 20 MiB 以下的 PNG/JPEG/GIF/BMP；可手动下载原文件。");
        using var input = File.OpenRead(path);
        var info = StbImageSharp.ImageInfo.FromStream(input) ?? throw new IOException("无法识别图片。");
        if (info.Width <= 0 || info.Height <= 0 || info.Width > 16384 || info.Height > 16384 || (long)info.Width * info.Height > 40000000)
            throw new IOException("图片尺寸超过缩略图限制，请手动下载原文件。");
        input.Position = 0;
        var image = StbImageSharp.ImageResult.FromStream(input, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        double scale = Math.Min(1, Math.Min(320d / image.Width, 240d / image.Height));
        int width = Math.Max(1, (int)(image.Width * scale)), height = Math.Max(1, (int)(image.Height * scale));
        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int x0 = x * image.Width / width, x1 = Math.Max(x0 + 1, (x + 1) * image.Width / width);
            int y0 = y * image.Height / height, y1 = Math.Max(y0 + 1, (y + 1) * image.Height / height);
            long r = 0, g = 0, b = 0; int n = (x1-x0)*(y1-y0);
            for (int sy=y0;sy<y1;sy++) for(int sx=x0;sx<x1;sx++)
            {
                int i=(sy*image.Width+sx)*4, a=image.Data[i+3];
                r+=(image.Data[i]*a+255*(255-a))/255; g+=(image.Data[i+1]*a+255*(255-a))/255; b+=(image.Data[i+2]*a+255*(255-a))/255;
            }
            int target=(y*width+x)*3; rgb[target]=(byte)(r/n);rgb[target+1]=(byte)(g/n);rgb[target+2]=(byte)(b/n);
        }
        using var output = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WriteJpg(rgb, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlue, output, 65);
        if (output.Length > MaxEncodedBytes) throw new IOException("缩略图压缩结果超过限制，请手动下载原文件。");
        return new(Convert.ToBase64String(output.ToArray()), width, height, version);
    }
}
