namespace FurtherAgent.AndroidApp;

internal static class Ui
{
    public static readonly Color Green = Color.FromArgb("#183F34");
    public static readonly Color Muted = Color.FromArgb("#718278");
    public static Label Text(string text, double size = 14, bool bold = false) => new()
    { Text = text, FontSize = size, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None, TextColor = Green };
    public static Button Button(string text, Func<Task> click, bool primary = false)
    {
        var button = new Button { Text = text, FontSize = 13, CornerRadius = 12, HeightRequest = 44,
            Padding = new Thickness(12, 0), BackgroundColor = primary ? Green : Color.FromArgb("#E7EEE8"),
            TextColor = primary ? Colors.White : Green };
        button.Clicked += async (_, _) => await click();
        return button;
    }
    public static void Theme(Page page)
    {
        page.SetAppThemeColor(Page.BackgroundColorProperty, Color.FromArgb("#F6F8F5"), Color.FromArgb("#101C17"));
        if (page is ContentPage content) content.SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.All);
    }
    public static void TextTheme(Label label) => label.SetAppThemeColor(Label.TextColorProperty, Green, Color.FromArgb("#E4ECE6"));
}
