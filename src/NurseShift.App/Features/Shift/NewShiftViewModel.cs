using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NurseShift.App.Features.Common;
using NurseShift.Core.Interfaces;

namespace NurseShift.App.Features.Shift;

public sealed partial class NewShiftViewModel(IShiftService shiftService, IClock clock) : ViewModelBase
{
    private static readonly TimeZoneInfo DeviceZone = TimeZoneInfo.Local;

    [ObservableProperty] private DateTime startDate = Round(clock.UtcNow).LocalDateTime.Date;
    [ObservableProperty] private TimeSpan startTime = Round(clock.UtcNow).LocalDateTime.TimeOfDay;
    [ObservableProperty] private DateTime endDate = Round(clock.UtcNow).LocalDateTime.AddHours(8).Date;
    [ObservableProperty] private TimeSpan endTime = Round(clock.UtcNow).LocalDateTime.AddHours(8).TimeOfDay;
    [ObservableProperty] private string timeZoneId = DeviceZone.Id;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            var start = StartDate.Date.Add(StartTime);
            var end = EndDate.Date.Add(EndTime);
            var result = await shiftService.CreateShiftAsync(start, end, TimeZoneId, CancellationToken.None);
            Show(result);
            if (result.IsSuccess)
                await Shell.Current.GoToAsync("..");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand] private async Task CancelAsync() => await Shell.Current.GoToAsync("..");

    private static DateTimeOffset Round(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var minutes = (local.Minute + 7) / 15 * 15;
        return new DateTimeOffset(local.Date, local.Offset).AddHours(local.Hour).AddMinutes(minutes);
    }
}
