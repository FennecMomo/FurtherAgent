using FurtherAgent.Core;
using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

internal sealed class SessionsPage : ContentPage
{
    private readonly CollectionView list = new() { SelectionMode = SelectionMode.Single, EmptyView = "没有匹配的会话" };
    private readonly SearchBar search = new() { Placeholder = "搜索会话" };
    private readonly Label note = Ui.Text("", 12);
    private bool working;
    public SessionsPage(MainPage home, MobileSession session)
    {
        Title = "远端会话"; Ui.Theme(this);
        void Filter() => list.ItemsSource = session.Sessions.Where(s => s.Title.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase) || s.Cwd.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase) || s.Id.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.SortTime).ThenBy(s => s.Id).ToArray();
        async Task Run(Func<Task> action)
        {
            if (working) return; working = true; list.IsEnabled = false;
            try { await action(); Filter(); }
            catch (Exception ex) { await DisplayAlertAsync("会话操作未完成", ex.Message, "知道了"); }
            finally { working = false; list.IsEnabled = true; }
        }
        var actions = new HorizontalStackLayout { Spacing = 8,
            Children = { Ui.Button("刷新", () => Run(session.RefreshAsync)),
                Ui.Button("新建会话", () => Run(async () => { await session.CreateAsync(); await Navigation.PopAsync(); }), true) } };
        ((Button)actions.Children[1]).IsEnabled = session.Agent?.CanCreate == true;
        search.TextChanged += (_, _) => Filter();
        list.ItemTemplate = new DataTemplate(() =>
        {
            var title = Ui.Text("", 16, true); Ui.TextTheme(title); title.SetBinding(Label.TextProperty, nameof(SessionInfo.Title));
            var time = Ui.Text("", 11); Ui.TextTheme(time); time.SetBinding(Label.TextProperty, nameof(SessionInfo.DisplayTime));
            var workspace = Ui.Text("", 12); Ui.TextTheme(workspace); workspace.SetBinding(Label.TextProperty, nameof(SessionInfo.Workspace));
            return new VerticalStackLayout { Padding = new Thickness(18, 14), Spacing = 5, Children = { title, workspace, time, new BoxView { HeightRequest = 1, Color = Color.FromArgb("#DDE6DF"), Margin = new Thickness(0, 8, 0, 0) } } };
        });
        list.SelectionChanged += async (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is not SessionInfo selected) return;
            list.SelectedItem = null;
            await Run(async () => { await home.SelectAsync(selected.Id); await Navigation.PopAsync(); });
        };
        note.Text = session.DeviceName + " · " + session.Agent?.Name; Ui.TextTheme(note);
        var top = new VerticalStackLayout { Padding = new Thickness(18, 16), Spacing = 12, Children = { note, actions, search } };
        var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)] }; layout.Add(top); layout.Add(list, 0, 1); Content = layout;
        Filter();
        ToolbarItems.Add(new ToolbarItem("归档当前", null, async () => await Run(async () =>
        {
            if (session.View is null || session.Agent?.CanArchive != true) { await DisplayAlertAsync("归档不可用", "当前没有支持归档的会话。", "知道了"); return; }
            if (await DisplayAlertAsync("归档会话", session.View.Title, "归档", "取消")) await session.ArchiveAsync();
        })) { IconImageSource = Ui.Icon("归档当前") });
    }
}
