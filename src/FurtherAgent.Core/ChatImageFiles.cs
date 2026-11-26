using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FurtherAgent.Core;

/// <summary>Registers explicit Markdown images from a Codex conversation's own workspace.</summary>
internal sealed partial class ChatImageFiles
{
    private const int MaxImages = 200;
    internal const string Prefix = "image/";
    private ImageIndex? current;

    internal SessionView Project(SessionView view)
    {
        var workspace = LocalPath(view.Cwd);
        if (workspace is null || Path.TrimEndingDirectorySeparator(workspace) == Path.GetPathRoot(workspace))
        {
            Volatile.Write(ref current, null);
            return view;
        }

        var files = new Dictionary<string, RemoteFile>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var messages = view.Messages.ToArray();
        for (int i = messages.Length - 1; i >= 0; i--)
        {
            var entry = messages[i];
            if (ChatPresentation.Kind(entry) != "assistant" || !entry.Text.Contains("![", StringComparison.Ordinal)) continue;
            var text = MarkdownTokens().Replace(entry.Text, ProjectImage);
            if (text != entry.Text) messages[i] = entry with { Text = text };
        }

        Volatile.Write(ref current, new ImageIndex(view.Id, paths));
        return files.Count == 0 ? view : view with { Messages = messages.ToList(), OutputFiles = [.. view.OutputFiles ?? [], .. files.Values] };

        string ProjectImage(Match token)
        {
            if (!token.Groups["path"].Success) return token.Value;
            var path = LocalPath(Uri.UnescapeDataString(token.Groups["path"].Value.Trim().Trim('<', '>')));
            if (path is null || !ImageThumbnail.Supported(path)) return token.Value;
            string relative = Path.GetRelativePath(workspace, path);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathRooted(relative)) return token.Value;

            // Existing outbox entries still use their normal IDs. This index only maps the explicit workspace reference.
            string key = OperatingSystem.IsWindows() ? relative.ToUpperInvariant() : relative;
            string id = Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            if (!files.ContainsKey(id))
            {
                if (files.Count >= MaxImages || ReadableImage(path) is not { } info) return token.Value;
                files.Add(id, new RemoteFile(id, info.Name, info.Length, Version: FileTransferStore.VersionOf(info)));
                paths.Add(id, path);
            }
            return $"![{token.Groups["alt"].Value}](attachment:{Uri.EscapeDataString(id)})";
        }
    }

    internal string? Resolve(string session, string id)
    {
        var index = Volatile.Read(ref current);
        if (index is null || index.Session != session || !index.Paths.TryGetValue(id, out var path)) return null;
        return ReadableImage(path) is null ? null : path;
    }

    private static string? LocalPath(string path)
    {
        try
        {
            // Codex Desktop uses Markdown links such as /D:/project/validation/image.png.
            if (OperatingSystem.IsWindows())
            {
                path = path.Replace('\\', '/');
                if (path is ['/', '/', '?', '/', var extendedDrive, ':', '/', ..] && char.IsAsciiLetter(extendedDrive))
                    path = path[4..];
                if (path is ['/', var drive, ':', '/', ..] && char.IsAsciiLetter(drive)) path = path[1..];
                if (path.Length < 3 || path[1] != ':' || path[2] != '/' || path.IndexOf(':', 2) >= 0) return null;
            }
            if (path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
                !Path.IsPathFullyQualified(path)) return null;
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException or IOException) { return null; }
    }

    private static FileInfo? ReadableImage(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > FileTransferStore.MaxFileBytes ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            // Check the directory chain too, including the workspace itself.
            for (var dir = file.Directory; dir is not null; dir = dir.Parent)
                if ((File.GetAttributes(dir.FullName) & FileAttributes.ReparsePoint) != 0) return null;
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Match inline images while consuming code spans and fenced blocks without modification.
    [GeneratedRegex(@"(?m)^[ \t]{0,3}(?<fence>`{3,}|~{3,})[^\r\n]*(?:\r?\n)[\s\S]*?(?:^[ \t]{0,3}\k<fence>[`~]*[ \t]*\r?$|\z)|(?<ticks>`+)[^\r\n]*?\k<ticks>(?!`)|!\[(?<alt>[^\]\r\n]*)\]\((?<path><[^>\r\n]+>|[^()\r\n]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownTokens();

    private sealed record ImageIndex(string Session, Dictionary<string, string> Paths);
}
