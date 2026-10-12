using FurtherAgent.Core;

namespace FurtherAgent.Client;

public sealed class WorkspaceGroup
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public List<WorkspaceSession> Sessions { get; init; } = [];
    public bool IsExpanded { get; set; } = true;
    public bool IsSelected { get; set; }
    public string Heading => $"{Name} ({Sessions.Count})";
    public static string Key(string path)
    {
        if (path.Length > 1 && (path[1] == ':' || path.StartsWith("\\\\"))) return path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
        return path.TrimEnd('/');
    }
    public static string FolderName(string path) => string.IsNullOrEmpty(path) ? "工作空间未知" : path.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() is { Length: > 0 } name ? name : path;
    public static IReadOnlyList<WorkspaceGroup> Build(IEnumerable<SessionInfo> sessions, IEnumerable<WorkspaceInfo> workspaces, string search, string? selected,
        IReadOnlyList<WorkspaceGroup>? previous = null)
    {
        var groups = new Dictionary<string, WorkspaceGroup>();
        foreach (var workspace in workspaces)
            groups.TryAdd(Key(workspace.Path), new() { Name = workspace.Name, Path = workspace.Path });
        foreach (var session in sessions.OrderByDescending(s => s.SortTime).ThenBy(s => s.Id))
        {
            string key = Key(session.Cwd);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new() { Name = FolderName(session.Cwd), Path = session.Cwd };
            group.Sessions.Add(new(session) { IsSelected = session.Id == selected });
        }
        var result = new List<WorkspaceGroup>(); search = search.Trim();
        foreach (var (key, group) in groups)
        {
            bool match = group.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || group.Path.Contains(search, StringComparison.OrdinalIgnoreCase);
            var rows = match ? group.Sessions : group.Sessions.Where(s => s.Info.Title.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!match && rows.Count == 0) continue;
            var old = previous?.FirstOrDefault(g => Key(g.Path) == key);
            result.Add(new() { Name = group.Name, Path = group.Path, Sessions = rows, IsExpanded = search.Length > 0 || (old?.IsExpanded ?? true) });
        }
        return result.OrderByDescending(g => g.Sessions.FirstOrDefault()?.Info.SortTime ?? DateTimeOffset.MinValue).ThenBy(g => g.Name).ToArray();
    }
}
public sealed class WorkspaceSession(SessionInfo info)
{
    public SessionInfo Info { get; } = info;
    public bool IsSelected { get; set; }
    public bool IsExpanded { get; set; }
}
