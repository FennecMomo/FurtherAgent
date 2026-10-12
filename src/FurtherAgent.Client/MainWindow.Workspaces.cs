using System.Windows;
using System.Windows.Input;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public partial class MainWindow
{
    private IReadOnlyList<WorkspaceInfo> workspaces = [];
    private IReadOnlyList<WorkspaceGroup> workspaceGroups = [];
    private WorkspaceDirectories? browsedDirectory;
    private int browseVersion;
    private bool workspaceBusy;
    private string? workspaceRequestId;

    private async Task LoadWorkspacesAsync(RemoteDevice connection)
    {
        var result = Json.Read<List<WorkspaceInfo>>(await connection.Peer.CallAsync("workspaces/list", null, responseTimeout: TimeSpan.FromSeconds(45)));
        if (remote == connection) workspaces = result;
    }
    private void FillWorkspacePicker(string? selectedPath)
    {
        var choices = workspaces.Concat(sessions.Where(s => !string.IsNullOrWhiteSpace(s.Cwd)).Select(s => new WorkspaceInfo("", WorkspaceGroup.FolderName(s.Cwd), s.Cwd)))
            .DistinctBy(w => WorkspaceGroup.Key(w.Path)).OrderBy(w => w.Name).ToArray();
        NewWorkspace.ItemsSource = choices;
        NewWorkspace.SelectedItem = choices.FirstOrDefault(w => WorkspaceGroup.Key(w.Path) == WorkspaceGroup.Key(selectedPath ?? "")) ?? choices.FirstOrDefault();
    }
    private async void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (actionBusy || workspaceBusy || remote is null || agent?.CanWorkspaces != true) return;
        string path = (NewOverlay.IsVisible ? (NewWorkspace.SelectedItem as WorkspaceInfo)?.Path : null)
            ?? (SessionList.SelectedItem as WorkspaceGroup)?.Path ?? sessions.FirstOrDefault(s => s.Id == selectedId)?.Cwd ?? "";
        CloseOverlays(); WorkspaceOverlay.Visibility = Visibility.Visible; WorkspaceError.Text = ""; WorkspaceFolderName.Clear();
        workspaceRequestId = Guid.NewGuid().ToString("N");
        await BrowseWorkspaceAsync(path);
    }
    private async Task BrowseWorkspaceAsync(string path)
    {
        if (remote is null || actionBusy || workspaceBusy) return;
        var connection = remote; int version = ++browseVersion;
        WorkspaceError.Text = "正在读取远端目录…"; AddWorkspaceButton.IsEnabled = false;
        try
        {
            var result = Json.Read<WorkspaceDirectories>(await connection.Peer.CallAsync("workspaces/browse", new { path }));
            if (remote != connection || version != browseVersion) return;
            browsedDirectory = result; WorkspacePath.Text = result.Path; WorkspaceFolders.ItemsSource = result.Directories; WorkspaceError.Text = "";
        }
        catch (Exception ex) { if (remote == connection && version == browseVersion) WorkspaceError.Text = ex.Message; }
        finally { if (version == browseVersion) AddWorkspaceButton.IsEnabled = true; }
    }
    private async void BrowsePath_Click(object sender, RoutedEventArgs e) => await BrowseWorkspaceAsync(WorkspacePath.Text.Trim());
    private async void ParentDirectory_Click(object sender, RoutedEventArgs e) => await BrowseWorkspaceAsync(browsedDirectory?.Parent ?? "");
    private async void RootDirectories_Click(object sender, RoutedEventArgs e) => await BrowseWorkspaceAsync("");
    private async void Directory_DoubleClick(object sender, MouseButtonEventArgs e)
    { if (WorkspaceFolders.SelectedItem is WorkspaceDirectory directory) await BrowseWorkspaceAsync(directory.Path); }
    private async void OpenDirectory_Click(object sender, RoutedEventArgs e)
    { if (WorkspaceFolders.SelectedItem is WorkspaceDirectory directory) await BrowseWorkspaceAsync(directory.Path); }
    private async void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (remote is null || actionBusy || workspaceBusy) return;
        var connection = remote; string path = WorkspacePath.Text.Trim(), folder = WorkspaceFolderName.Text;
        if (path.Length == 0) { WorkspaceError.Text = "请先打开远端目录。"; return; }
        actionBusy = workspaceBusy = true; WorkspaceError.Text = "正在添加工作空间…"; AddWorkspaceButton.IsEnabled = false;
        WorkspacePath.IsEnabled = WorkspaceFolderName.IsEnabled = SessionList.IsEnabled = false;
        try
        {
            var result = Json.Read<WorkspaceInfo>(await connection.Peer.CallAsync("workspaces/add", new { id = workspaceRequestId, path, folder }, responseTimeout: TimeSpan.FromSeconds(90)));
            if (remote != connection) return;
            workspaces = workspaces.Where(w => WorkspaceGroup.Key(w.Path) != WorkspaceGroup.Key(result.Path)).Append(result).ToArray();
            SearchBox.Clear(); Filter(); FillWorkspacePicker(result.Path);
            CloseOverlays(); NewOverlay.Visibility = NewWorkspacePanel.Visibility = Visibility.Visible;
            NewHint.Text = "工作空间已添加，可以在这里创建第一段会话。"; NewError.Text = "";
            createRequestId = Guid.NewGuid().ToString("N"); CreateButton.IsEnabled = NewTitle.IsEnabled = NewWorkspace.IsEnabled = true;
            StatusBar.Text = "已添加远端工作空间：" + result.Path;
        }
        catch (Exception ex) { WorkspaceError.Text = ex.Message; }
        finally
        {
            actionBusy = workspaceBusy = false; AddWorkspaceButton.IsEnabled = true;
            WorkspacePath.IsEnabled = WorkspaceFolderName.IsEnabled = SessionList.IsEnabled = true;
        }
    }
}
