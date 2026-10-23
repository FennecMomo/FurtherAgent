namespace FurtherAgent.Core;

// References resolve only against metadata supplied by the attached session.
public static class RemoteFileReferences
{
    public static RemoteFile? Resolve(IEnumerable<RemoteFile>? files, string reference)
    {
        if (files is null) return null;
        string value;
        try { value = Uri.UnescapeDataString(reference.Trim().Trim('"', '\'')); }
        catch (UriFormatException) { return null; }
        foreach (string prefix in new[] { "MEDIA:", "attachment:", "sandbox:" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value[prefix.Length..];
        value = value.Replace('\\', '/');
        // Never fetch URLs or read a path supplied in message text.
        if (value.Contains("://") || value.StartsWith("//") || value.Contains(':') && !(value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '/')) return null;
        var entries = files.ToArray();
        var exact = entries.FirstOrDefault(f => f.Id == value);
        if (exact is not null) return exact;
        string name = value.Split('/')[^1];
        var matches = entries.Where(f => f.Name == name || f.Id.Split('/')[^1] == name).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
