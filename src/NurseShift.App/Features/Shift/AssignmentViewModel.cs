using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NurseShift.App.Features.Common;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;

namespace NurseShift.App.Features.Shift;

[QueryProperty(nameof(AssignmentId), "AssignmentId")]
[QueryProperty(nameof(ShiftId), "ShiftId")]
[QueryProperty(nameof(SessionId), "SessionId")]
[QueryProperty(nameof(DraftId), "DraftId")]
public sealed partial class AssignmentViewModel(IShiftService shiftService, IImportService importService, ISettingsStore settingsStore, IClock clock) : ViewModelBase
{
    private Assignment? _existing;
    private ImportDetails? _import;
    private Guid? _selectedPatientId;

    [ObservableProperty] private Guid assignmentId;
    [ObservableProperty] private Guid shiftId;
    [ObservableProperty] private Guid sessionId;
    [ObservableProperty] private Guid draftId;
    [ObservableProperty] private string patientName = string.Empty;
    [ObservableProperty] private string procedure = string.Empty;
    [ObservableProperty] private string room = string.Empty;
    [ObservableProperty] private string bed = string.Empty;
    [ObservableProperty] private string dosage = string.Empty;
    [ObservableProperty] private string comment = string.Empty;
    [ObservableProperty] private DateTime dueDate = clock.UtcNow.ToLocalTime().Date;
    [ObservableProperty] private TimeSpan dueTime = clock.UtcNow.ToLocalTime().AddMinutes(15).TimeOfDay;
    [ObservableProperty] private int leadMinutes = 10;
    [ObservableProperty] private bool showStatusActions;
    [ObservableProperty] private bool isFinal;
    [ObservableProperty] private string statusText = UiStrings.Pending;
    [ObservableProperty] private string? sourceExcerpt;
    [ObservableProperty] private bool hasPatientSuggestions;
    [ObservableProperty] private Patient? selectedPatient;

    public ObservableCollection<Patient> PatientSuggestions { get; } = [];

    public async Task InitializeAsync()
    {
        var settings = await settingsStore.GetAsync(CancellationToken.None);
        LeadMinutes = settings.AdvanceLeadMinutes;
        if (AssignmentId != Guid.Empty)
        {
            var active = await shiftService.GetActiveShiftAsync(CancellationToken.None);
            if (active is null)
                return;
            var details = await shiftService.GetShiftDetailsAsync(active.Id, CancellationToken.None);
            _existing = details?.Assignments.SingleOrDefault(item => item.Id == AssignmentId);
            ShiftId = active.Id;
        }
        else if (DraftId != Guid.Empty)
        {
            _import = await importService.GetAsync(SessionId, CancellationToken.None);
            _existing = _import?.Assignments.SingleOrDefault(item => item.Id == DraftId);
            ShiftId = _import?.Session.ShiftId ?? Guid.Empty;
        }
        if (_existing is not null)
        {
            PatientName = _existing.PatientName ?? string.Empty;
            Procedure = _existing.Procedure ?? string.Empty;
            Room = _existing.Room ?? string.Empty;
            Bed = _existing.Bed ?? string.Empty;
            Dosage = _existing.Dosage ?? string.Empty;
            Comment = _existing.Comment ?? string.Empty;
            DueDate = _existing.DueLocal?.Date ?? DueDate;
            DueTime = _existing.DueLocal?.TimeOfDay ?? DueTime;
            LeadMinutes = _existing.AdvanceLeadMinutes;
            SourceExcerpt = _existing.SourceExcerpt;
            IsFinal = _existing.ExecutionStatus != ExecutionStatus.Pending;
            StatusText = _existing.ExecutionStatus switch { ExecutionStatus.Done => UiStrings.Done, ExecutionStatus.Skipped => UiStrings.Skipped, _ => UiStrings.Pending };
            ShowStatusActions = true;
            _selectedPatientId = _existing.PatientId;
        }
    }

    [RelayCommand]
    private async Task FindPatientMatchesAsync()
    {
        _selectedPatientId = null;
        SelectedPatient = null;
        PatientSuggestions.Clear();
        var matches = await shiftService.FindPatientSuggestionsAsync(PatientName,
            string.IsNullOrWhiteSpace(Room) ? null : Room.Trim(),
            string.IsNullOrWhiteSpace(Bed) ? null : Bed.Trim(), CancellationToken.None);
        foreach (var match in matches)
            PatientSuggestions.Add(match);
        HasPatientSuggestions = PatientSuggestions.Count > 0;
    }

    partial void OnSelectedPatientChanged(Patient? value)
    {
        if (value is null)
            return;
        PatientName = value.FullName;
        _selectedPatientId = value.Id;
    }

    partial void OnPatientNameChanged(string value) => _selectedPatientId = null;
    partial void OnRoomChanged(string value) => _selectedPatientId = null;
    partial void OnBedChanged(string value) => _selectedPatientId = null;

    [RelayCommand]
    private async Task SaveAsync() => await SaveInternalAsync();

    private async Task<bool> SaveInternalAsync()
    {
        if (IsBusy)
            return false;
        IsBusy = true;
        try
        {
            var input = new AssignmentInput(PatientName, Procedure, DueDate.Date.Add(DueTime), Room, Bed, Dosage, Comment, LeadMinutes, _selectedPatientId);
            OperationResult result;
            if (_existing is null)
                result = await shiftService.AddAssignmentAsync(ShiftId, input, CancellationToken.None);
            else if (_existing.ReviewState == ReviewState.Draft)
            {
                CopyInput(_existing, input);
                result = await importService.UpdateDraftAsync(_existing, CancellationToken.None);
            }
            else
            {
                result = await shiftService.EditAssignmentAsync(_existing.Id, input, _existing.RowVersion, false, CancellationToken.None);
                if (result.ErrorCode == SafeErrorCode.ConfirmationRequired)
                {
                    var confirm = await Shell.Current.DisplayAlert(UiStrings.Save, UiStrings.Get("ClinicalEditConfirmation"), UiStrings.Save, UiStrings.Cancel);
                    if (confirm)
                        result = await shiftService.EditAssignmentAsync(_existing.Id, input, _existing.RowVersion, true, CancellationToken.None);
                }
            }
            Show(result);
            if (result.IsSuccess)
                await Shell.Current.GoToAsync("..");
            return result.IsSuccess;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        var choice = await Shell.Current.DisplayActionSheet(UiStrings.Get("SaveChangesPrompt"), UiStrings.Cancel, UiStrings.Get("DiscardChanges"), UiStrings.Save);
        if (choice == UiStrings.Save)
            await SaveInternalAsync();
        else if (choice == UiStrings.Get("DiscardChanges"))
            await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task MarkDoneAsync() => await ChangeStatusAsync(ExecutionStatus.Done);

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (await Shell.Current.DisplayAlert(UiStrings.Skip, UiStrings.Get("SkipConfirmation"), UiStrings.Skip, UiStrings.Cancel))
            await ChangeStatusAsync(ExecutionStatus.Skipped);
    }

    [RelayCommand] private async Task ReturnPendingAsync() => await ChangeStatusAsync(ExecutionStatus.Pending);

    private async Task ChangeStatusAsync(ExecutionStatus status)
    {
        if (_existing is null || _existing.ReviewState == ReviewState.Draft)
            return;
        var result = await shiftService.SetAssignmentStatusAsync(_existing.Id, status, CancellationToken.None);
        Show(result);
        if (result.IsSuccess)
            await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_existing is null || !await Shell.Current.DisplayAlert(UiStrings.Get("DeleteAssignmentTitle"), UiStrings.Get("DeleteAssignmentText"), UiStrings.Delete, UiStrings.Cancel))
            return;
        var result = _existing.ReviewState == ReviewState.Draft
            ? await importService.DeleteDraftAsync(_existing.Id, CancellationToken.None)
            : await shiftService.DeleteAssignmentAsync(_existing.Id, CancellationToken.None);
        Show(result);
        if (result.IsSuccess)
            await Shell.Current.GoToAsync("..");
    }

    private static void CopyInput(Assignment assignment, AssignmentInput input)
    {
        assignment.PatientId = input.PatientId;
        assignment.PatientName = input.PatientName;
        assignment.Procedure = input.Procedure;
        assignment.DueLocal = input.DueLocal;
        assignment.Room = input.Room;
        assignment.Bed = input.Bed;
        assignment.Dosage = input.Dosage;
        assignment.Comment = input.Comment;
        assignment.AdvanceLeadMinutes = input.AdvanceLeadMinutes;
    }
}
