using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace FurtherAgent.AndroidApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
    ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : MauiAppCompatActivity
{
}
