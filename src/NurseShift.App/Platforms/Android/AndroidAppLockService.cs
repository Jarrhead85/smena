using Android.App;
using Android.Content;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Interfaces;

namespace NurseShift.App;

public sealed class AndroidAppLockService : IAppLockService
{
    internal const int RequestCode = 41027;
    private TaskCompletionSource<bool>? _completion;

    public bool IsDeviceSecure => GetKeyguardManager()?.IsKeyguardSecure == true;

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (_completion is { Task.IsCompleted: false })
            return await _completion.Task;
        if (Platform.CurrentActivity is not MainActivity activity)
            return false;
        var intent = GetKeyguardManager()?.CreateConfirmDeviceCredentialIntent(UiStrings.Get("UnlockApp"), UiStrings.Get("UnlockDescription"));
        if (intent is null)
            return false;
        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
        activity.StartActivityForResult(intent, RequestCode);
        return await _completion.Task;
    }

    internal void Complete(int requestCode, Result resultCode)
    {
        if (requestCode == RequestCode)
            _completion?.TrySetResult(resultCode == Result.Ok);
    }

    private static KeyguardManager? GetKeyguardManager() =>
        (KeyguardManager?)Platform.AppContext.GetSystemService(Context.KeyguardService);
}
