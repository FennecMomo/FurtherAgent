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
        model.IsVisible = !settings.RequireModelSelection;
        provider.IsEnabled = settings.CanChangeModel && !settings.RequireModelSelection;
        var reasoning = new Picker { Title = "思考强度", IsVisible = settings.CanChangeReasoning };
        void UpdateReasoning(string? preferred)
        {
            var selected = catalog.SelectedIndex >= 0 ? settings.Models[catalog.SelectedIndex] : null;
            var efforts = selected?.ReasoningEfforts ?? [];
            reasoning.ItemsSource = efforts; reasoning.IsEnabled = efforts.Count > 0;
            reasoning.SelectedItem = preferred is not null && efforts.Contains(preferred) ? preferred : selected?.DefaultReasoningEffort;
            if (reasoning.SelectedIndex < 0 && efforts.Count > 0) reasoning.SelectedIndex = 0;
        }
        catalog.SelectedIndexChanged += (_, _) =>
        {
            if (catalog.SelectedIndex < 0) return;
            var item = settings.Models[catalog.SelectedIndex]; model.Text = item.Id; provider.Text = item.Provider;
            UpdateReasoning(reasoning.SelectedItem as string);
        };
        catalog.SelectedIndex = settings.Models.FindIndex(m => m.Id == settings.Model);
        UpdateReasoning(settings.ReasoningEffort);
        var note = Ui.Text(settings.Detail, 13);
        Button? save = null;
        save = Ui.Button("应用到此会话", async () =>
        {
            save!.IsEnabled = false;
            try
            {
                if (settings.RequireModelSelection && catalog.SelectedIndex < 0) throw new InvalidOperationException("请先选择远端可用模型。");
                await session.ConfigureAsync(model.Text ?? "", provider.Text ?? "", mode.SelectedIndex == 1 ? "plan" : "default", reasoning.IsEnabled ? reasoning.SelectedItem as string : null);
                await Navigation.PopAsync();
            }
            catch (Exception ex) { await DisplayAlertAsync("设置未完成", ex.Message, "知道了"); }
            finally { save.IsEnabled = true; }
        }, true);
        var stack = new VerticalStackLayout { Padding = 24, Spacing = 18,
            Children = { Ui.Text("由远端 Agent 决定可用能力", 21, true), note, catalog, model, provider, reasoning, mode,
                Ui.Text(settings.Usage ?? "此接入端暂不提供套餐限额。", 13),
                Ui.Button("刷新模型与限额", async () =>
                {
                    try
                    {
                        var refreshed = new ModelPage(session, await session.GetSettingsAsync());
                        Navigation.InsertPageBefore(refreshed, this); await Navigation.PopAsync();
                    }
                    catch (Exception ex) { await DisplayAlertAsync("读取失败", ex.Message, "知道了"); }
                }), save } };
        foreach (var label in stack.Children.OfType<Label>()) Ui.TextTheme(label);
        Content = new ScrollView { Content = stack };
    }
}
