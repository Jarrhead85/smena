using Android.App;
using Android.Runtime;
using NurseShift.App.Notifications;

namespace NurseShift.App;

[Application]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp()
    {
        var app = MauiProgram.CreateMauiApp();
        AndroidServices.Services = app.Services;
        return app;
    }

    public override void OnCreate()
    {
        base.OnCreate();
        NotificationPresenter.EnsureChannel();
    }
}
