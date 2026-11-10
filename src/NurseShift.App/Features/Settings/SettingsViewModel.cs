using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NurseShift.App.Features.Common;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;

namespace NurseShift.App.Features.Settings;

public sealed partial class SettingsViewModel(
    ISettingsStore settingsStore,
    ISecretStore secretStore,
    IRecognitionService recognitionService,
    IReminderScheduler reminderScheduler,
    IDataCleanupService cleanupService,
    IAppLockService appLockService) : ViewModelBase
{
    [ObservableProperty] private string authorizationKey = string.Empty;
    [ObservableProperty] private string keyState = string.Empty;
    [ObservableProperty] private string model = string.Empty;
    [ObservableProperty] private string scope = "GIGACHAT_API_PERS";
    [ObservableProperty] private int leadMinutes = 10;
    [ObservableProperty] private int themeIndex;
    [ObservableProperty] private int retentionDays = 30;
    [ObservableProperty] private bool retainPhotos;
    [ObservableProperty] private bool privateNotifications = true;
    [ObservableProperty] private bool lockWithDeviceCredential;
    [ObservableProperty] private string? successMessage;

    public async Task LoadAsync()
    {
        var settings = await settingsStore.GetAsync(CancellationToken.None);
        Model = settings.Model;
        Scope = settings.Scope;
        LeadMinutes = settings.AdvanceLeadMinutes;
        ThemeIndex = (int)settings.Theme;
        RetentionDays = settings.HistoryRetentionDays ?? -1;
        RetainPhotos = settings.RetainPhotos;
        PrivateNotifications = settings.PrivateLockScreenNotifications;
        LockWithDeviceCredential = settings.LockWithDeviceCredential;
        KeyState = await secretStore.GetAsync("gigachat.authorization_key", CancellationToken.None) is null ? UiStrings.Get("KeyMissing") : UiStrings.Get("KeySaved");
        AuthorizationKey = string.Empty;
    }

    [RelayCommand]
    private async Task SaveKeyAsync()
    {
        SuccessMessage = null;
        if (string.IsNullOrWhiteSpace(AuthorizationKey))
        {
            Show(SafeErrorCode.Validation);
            return;
        }
        await secretStore.SetAsync("gigachat.authorization_key", AuthorizationKey.Trim(), CancellationToken.None);
        AuthorizationKey = string.Empty;
        KeyState = UiStrings.Get("KeySaved");
        SuccessMessage = UiStrings.Saved;
    }

    [RelayCommand]
    private async Task DeleteKeyAsync()
    {
        if (!await Shell.Current.DisplayAlert(UiStrings.DeleteKey, UiStrings.Get("DeleteKeyText"), UiStrings.DeleteKey, UiStrings.Cancel))
            return;
        await secretStore.RemoveAsync("gigachat.authorization_key", CancellationToken.None);
        KeyState = UiStrings.Get("KeyMissing");
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        SuccessMessage = null;
        if (LeadMinutes is < 0 or > 60 || RetentionDays is not (-1 or 7 or 30 or 90) || ThemeIndex is < 0 or > 2 ||
            Scope is not ("GIGACHAT_API_PERS" or "GIGACHAT_API_B2B" or "GIGACHAT_API_CORP"))
        {
            Show(SafeErrorCode.Validation);
            return;
        }
        if (LockWithDeviceCredential && !appLockService.IsDeviceSecure)
        {
            ErrorMessage = UiStrings.Get("DeviceCredentialUnavailable");
            return;
        }
        var settings = await settingsStore.GetAsync(CancellationToken.None);
        await settingsStore.SaveAsync(settings with
        {
            Model = Model.Trim(), Scope = Scope, AdvanceLeadMinutes = LeadMinutes,
            Theme = (ThemePreference)ThemeIndex, HistoryRetentionDays = RetentionDays < 0 ? null : RetentionDays,
            RetainPhotos = RetainPhotos, PrivateLockScreenNotifications = PrivateNotifications,
            LockWithDeviceCredential = LockWithDeviceCredential
        }, CancellationToken.None);
        App.ApplyTheme((ThemePreference)ThemeIndex);
        SuccessMessage = UiStrings.Saved;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        SuccessMessage = null;
        try
        {
            await SaveSettingsAsync();
            var result = await recognitionService.TestConnectionAsync(CancellationToken.None);
            Show(result);
            if (result.IsSuccess)
                SuccessMessage = UiStrings.ConnectionOk;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenReminderSettingsAsync()
    {
        await reminderScheduler.RequestNotificationPermissionAsync(CancellationToken.None);
        await reminderScheduler.OpenExactAlarmSettingsAsync(CancellationToken.None);
        await reminderScheduler.SynchronizeAllFutureAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        if (!await Shell.Current.DisplayAlert(UiStrings.DeleteAll, UiStrings.Get("DeleteAllText"), UiStrings.DeleteAll, UiStrings.Cancel))
            return;
        await cleanupService.DeleteAllAsync(CancellationToken.None);
        Application.Current!.Windows[0].Page = new ContentPage { Content = new Label { Text = UiStrings.Get("DataDeleted"), Padding = 24, VerticalOptions = LayoutOptions.Center } };
    }
}
