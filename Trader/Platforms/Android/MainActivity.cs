using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace Trader;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, WindowSoftInputMode = Android.Views.SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        Android.Views.View? content = FindViewById(Android.Resource.Id.Content);

        if (content is null)
        {
            return;
        }

        ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarsInsetsListener());
    }

    private sealed class SystemBarsInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(Android.Views.View v, WindowInsetsCompat insets)
        {
            AndroidX.Core.Graphics.Insets bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout() | WindowInsetsCompat.Type.Ime());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return WindowInsetsCompat.Consumed;
        }
    }
}
