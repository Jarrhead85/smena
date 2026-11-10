using CommunityToolkit.Mvvm.Input;
using NurseShift.App.Features.Common;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;

namespace NurseShift.App.Features.Onboarding;

public sealed partial class WelcomeViewModel(IServiceProvider services, ISettingsStore settingsStore, IShiftService shiftService, IClock clock) : ViewModelBase
{
    [RelayCommand]
    private async Task StartAsync()
    {
        var settings = await settingsStore.GetAsync(CancellationToken.None);
        await settingsStore.SaveAsync(settings with { OnboardingCompleted = true }, CancellationToken.None);
        Application.Current!.Windows[0].Page = services.GetRequiredService<AppShell>();
    }

    [RelayCommand]
    private async Task TestShiftAsync()
    {
        var now = clock.UtcNow.ToLocalTime();
        var result = await shiftService.CreateShiftAsync(now.AddMinutes(-15).LocalDateTime, now.AddHours(8).LocalDateTime, TimeZoneInfo.Local.Id, CancellationToken.None);
        Show(result);
        if (!result.IsSuccess)
            return;
        var shift = await shiftService.GetActiveShiftAsync(CancellationToken.None);
        for (var index = 1; index <= 3; index++)
        {
            await shiftService.AddAssignmentAsync(shift!.Id, new AssignmentInput(UiStrings.Format("SyntheticPatientFormat", index), UiStrings.Format("SyntheticProcedureFormat", index), now.AddMinutes(index * 20).LocalDateTime, "T-1", index.ToString(), null, UiStrings.Get("SyntheticComment"), 10), CancellationToken.None);
        }
        await StartAsync();
    }
}
