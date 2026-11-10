using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NurseShift.App.Features.Common;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;

namespace NurseShift.App.Features.History;

public sealed class ShiftListItem(Core.Models.Shift shift)
{
    public Guid Id { get; } = shift.Id;
    public string Dates => $"{shift.StartLocal:dd MMMM yyyy, HH:mm} — {shift.EndLocal:dd MMMM yyyy, HH:mm}";
    public string Stats => UiStrings.Format("HistoryStatsFormat", shift.Assignments.Count, shift.Assignments.Count(item => item.ExecutionStatus == ExecutionStatus.Done), shift.Assignments.Count(item => item.ExecutionStatus == ExecutionStatus.Skipped));
}

public sealed partial class HistoryViewModel(IShiftService shiftService) : ViewModelBase
{
    public ObservableCollection<ShiftListItem> Shifts { get; } = [];
    [ObservableProperty] private bool isEmpty;

    public async Task LoadAsync()
    {
        Shifts.Clear();
        var shifts = await shiftService.GetHistoryAsync(CancellationToken.None);
        foreach (var shift in shifts)
            Shifts.Add(new ShiftListItem(shift));
        IsEmpty = Shifts.Count == 0;
    }

    [RelayCommand] private async Task OpenAsync(ShiftListItem item) => await Shell.Current.GoToAsync("history-detail", new Dictionary<string, object> { ["ShiftId"] = item.Id });
}

[QueryProperty(nameof(ShiftId), "ShiftId")]
public sealed partial class HistoryDetailViewModel(IShiftService shiftService) : ViewModelBase
{
    [ObservableProperty] private Guid shiftId;
    [ObservableProperty] private string dates = string.Empty;
    [ObservableProperty] private string stats = string.Empty;
    public ObservableCollection<Core.Models.Assignment> Assignments { get; } = [];

    public async Task LoadAsync()
    {
        var details = await shiftService.GetShiftDetailsAsync(ShiftId, CancellationToken.None);
        if (details is null)
        {
            Show(SafeErrorCode.ShiftNotFound);
            return;
        }
        Dates = $"{details.Shift.StartLocal:dd MMMM yyyy, HH:mm} — {details.Shift.EndLocal:dd MMMM yyyy, HH:mm}";
        Stats = UiStrings.Format("AssignmentCountFormat", details.Assignments.Count);
        Assignments.Clear();
        foreach (var assignment in details.Assignments)
            Assignments.Add(assignment);
    }

    [RelayCommand]
    private async Task ReactivateAsync()
    {
        var result = await shiftService.ReactivateShiftAsync(ShiftId, CancellationToken.None);
        Show(result);
        if (result.IsSuccess)
            await Shell.Current.GoToAsync("//shift");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!await Shell.Current.DisplayAlert(UiStrings.Get("DeleteShiftTitle"), UiStrings.Get("DeleteShiftText"), UiStrings.Delete, UiStrings.Cancel))
            return;
        var result = await shiftService.DeleteShiftAsync(ShiftId, CancellationToken.None);
        Show(result);
        if (result.IsSuccess)
            await Shell.Current.GoToAsync("..");
    }
}
