using FurtherAgent.Core;
using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

internal sealed class ModelPage : ContentPage
{
    public ModelPage(MobileSession session, AgentSettings settings)
    {
        Title = "模型与模式"; Ui.Theme(this);
        var model = new Entry { Text = settings.Model, Placeholder = "模型 ID", IsEnabled = settings.CanChangeModel };
        var provider = new Entry { Text = settings.Provider, Placeholder = "提供商（可选）", IsEnabled = settings.CanChangeModel };
        var mode = new Picker { Title = "执行模式", ItemsSource = new[] { "Build", "Plan" }, SelectedIndex = settings.Mode == "plan" ? 1 : 0, IsEnabled = settings.CanChangeMode };
        var catalog = new Picker { Title = "选择远端可用模型", ItemsSource = settings.Models.Select(m => m.Label + " · " + m.Provider).ToArray(), IsEnabled = settings.CanChangeModel };
        catalog.SelectedIndexChanged += (_, _) =>
        {
            if (catalog.SelectedIndex < 0) return;
            var item = settings.Models[catalog.SelectedIndex]; model.Text = item.Id; provider.Text = item.Provider;
        };
        var note = Ui.Text(settings.Detail, 13);
        Button? save = null;
        save = Ui.Button("应用到此会话", async () =>
        {
            save!.IsEnabled = false;
            try { await session.ConfigureAsync(model.Text ?? "", provider.Text ?? "", mode.SelectedIndex == 1 ? "plan" : "default"); await Navigation.PopAsync(); }
            catch (Exception ex) { await DisplayAlertAsync("设置未完成", ex.Message, "知道了"); }
            finally { save.IsEnabled = true; }
        }, true);
        var stack = new VerticalStackLayout { Padding = 24, Spacing = 18,
            Children = { Ui.Text("由远端 Agent 决定可用能力", 21, true), note, catalog, model, provider, mode, save } };
        foreach (var label in stack.Children.OfType<Label>()) Ui.TextTheme(label);
        Content = new ScrollView { Content = stack };
    }
}
