using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Microsoft.Extensions.DependencyInjection;
using NurseShift.App.Services;

namespace NurseShift.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.Secure);
        HandleDeepLink(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleDeepLink(intent);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        AndroidServices.Services?.GetService<AndroidAppLockService>()?.Complete(requestCode, resultCode);
    }

    private void HandleDeepLink(Intent? intent)
    {
        if (intent?.Action == "ru.smena.app.action.OPEN_ASSIGNMENT" && Guid.TryParse(intent.GetStringExtra("assignment_id"), out var id))
            AndroidServices.Services?.GetRequiredService<DeepLinkService>().OpenAssignment(id);
    }
}
