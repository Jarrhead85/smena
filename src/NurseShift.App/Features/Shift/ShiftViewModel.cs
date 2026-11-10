using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NurseShift.App.Features.Common;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;
using NurseShift.Core.Services;

namespace NurseShift.App.Features.Shift;

public sealed partial class AssignmentRow(Assignment assignment, DateTimeOffset now) : ObservableObject
{
    public Assignment Assignment { get; } = assignment;
    public Guid Id => Assignment.Id;
    public string Time => Assignment.DueLocal?.ToString("HH:mm") ?? "—";
    public string Patient => Assignment.PatientName ?? string.Empty;
    public string Procedure => Assignment.Procedure ?? string.Empty;
    public string Location => string.Join(" · ", new[] { Assignment.Room is null ? null : UiStrings.Format("RoomShortFormat", Assignment.Room), Assignment.Bed is null ? null : UiStrings.Format("BedShortFormat", Assignment.Bed) }.Where(item => item is not null));
    public string? Dosage => Assignment.Dosage;
    public string? Comment => Assignment.Comment;
    public bool HasDosage => !string.IsNullOrWhiteSpace(Assignment.Dosage);
    public bool HasComment => !string.IsNullOrWhiteSpace(Assignment.Comment);
    public bool IsPending => Assignment.ExecutionStatus == ExecutionStatus.Pending;
    public string Status => Assignment.ExecutionStatus switch
    {
        ExecutionStatus.Done => UiStrings.Done,
        ExecutionStatus.Skipped => UiStrings.Skipped,
        _ when FeedService.IsOverdue(Assignment, now) => UiStrings.Overdue,
        _ => UiStrings.Pending
    };
}

public sealed class AssignmentGroup : ObservableCollection<AssignmentRow>
{
    private readonly FeedSection _section;
    public AssignmentGroup(FeedSection section, IEnumerable<AssignmentRow> rows) : base(rows) => _section = section;
    public IReadOnlyList<AssignmentRow> Rows => this.ToArray();
    public string Title => _section switch
    {
        FeedSection.Overdue => UiStrings.Overdue,
        FeedSection.Now => UiStrings.Now,
        FeedSection.Soon => UiStrings.Soon,
        FeedSection.Later => UiStrings.Later,
        _ => UiStrings.Completed
    };
}

public sealed partial class ShiftViewModel : ViewModelBase
{
    private readonly IShiftService _shiftService;
    private readonly IImportService _importService;
    private readonly IReminderScheduler _reminderScheduler;
    private readonly IClock _clock;
    private readonly FeedService _feedService = new();
    private Core.Models.Shift? _shift;

    public ShiftViewModel(IShiftService shiftService, IImportService importService, IReminderScheduler reminderScheduler, IClock clock)
    {
        _shiftService = shiftService;
        _importService = importService;
        _reminderScheduler = reminderScheduler;
        _clock = clock;
    }

    public ObservableCollection<AssignmentGroup> Groups { get; } = [];
    [ObservableProperty] private bool hasShift;
    [ObservableProperty] private bool hasNoShift = true;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool hasIncompleteImport;
    [ObservableProperty] private bool hasReminderWarning;
    [ObservableProperty] private bool hasTimeZoneWarning;
    [ObservableProperty] private string shiftInterval = string.Empty;
    [ObservableProperty] private string progress = string.Empty;
    [ObservableProperty] private string currentTime = string.Empty;
    [ObservableProperty] private AssignmentRow? focusAssignment;
    [ObservableProperty] private bool hasFocus;
    private Guid _importId;
    private ImportStatus _importStatus;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            _shift = await _shiftService.GetActiveShiftAsync(cancellationToken);
            HasShift = _shift is not null;
            HasNoShift = _shift is null;
            Groups.Clear();
            FocusAssignment = null;
            HasFocus = false;
            IsEmpty = true;
            HasIncompleteImport = false;
            HasReminderWarning = false;
            HasTimeZoneWarning = false;
            if (_shift is null)
                return;
            ShiftInterval = $"{_shift.StartLocal:dd MMMM, HH:mm} — {_shift.EndLocal:dd MMMM, HH:mm} · {_shift.TimeZoneId}";
            var details = await _shiftService.GetShiftDetailsAsync(_shift.Id, cancellationToken);
            var now = _clock.UtcNow;
            CurrentTime = UiStrings.Now + ": " + TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(_shift.TimeZoneId)).ToString("HH:mm");
            var assignments = details?.Assignments ?? [];
            IsEmpty = assignments.Count == 0;
            var done = assignments.Count(item => item.ExecutionStatus == ExecutionStatus.Done);
            var skipped = assignments.Count(item => item.ExecutionStatus == ExecutionStatus.Skipped);
            var overdue = assignments.Count(item => FeedService.IsOverdue(item, now));
            Progress = UiStrings.Format("ProgressFormat", done, assignments.Count, skipped, overdue);
            var rows = _feedService.Group(assignments, now).Select(group => new AssignmentGroup(group.Section, group.Assignments.Select(item => new AssignmentRow(item, now)))).ToArray();
            foreach (var group in rows)
                Groups.Add(group);
            FocusAssignment = rows.Where(item => item.Title != UiStrings.Later).SelectMany(item => item.Rows).FirstOrDefault(item => item.IsPending);
            HasFocus = FocusAssignment is not null;
            var import = await _importService.GetIncompleteAsync(_shift.Id, cancellationToken);
            HasIncompleteImport = import is not null;
            _importId = import?.Session.Id ?? Guid.Empty;
            _importStatus = import?.Session.Status ?? ImportStatus.Preparing;
            var availability = await _reminderScheduler.GetAvailabilityAsync(cancellationToken);
            if (availability.IsAvailable && details?.ReminderSyncFailed == true)
            {
                await _reminderScheduler.SynchronizeAllFutureAsync(cancellationToken);
                details = await _shiftService.GetShiftDetailsAsync(_shift.Id, cancellationToken);
            }
            HasReminderWarning = details?.ReminderSyncFailed == true || !availability.IsAvailable;
            HasTimeZoneWarning = !string.Equals(TimeZoneInfo.Local.Id, _shift.TimeZoneId, StringComparison.Ordinal);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Show(SafeErrorCode.StorageUnavailable);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand] private async Task NewShiftAsync() => await Shell.Current.GoToAsync("new-shift");
    [RelayCommand] private async Task AddManualAsync() => await Shell.Current.GoToAsync("assignment", new Dictionary<string, object> { ["ShiftId"] = _shift!.Id });
    [RelayCommand] private async Task AddSheetAsync() => await Shell.Current.GoToAsync("import", new Dictionary<string, object> { ["ShiftId"] = _shift!.Id });
    [RelayCommand]
    private async Task ResumeImportAsync()
    {
        if (_importStatus == ImportStatus.Review)
            await Shell.Current.GoToAsync("review", new Dictionary<string, object> { ["SessionId"] = _importId });
        else if (_shift is not null)
            await Shell.Current.GoToAsync("import", new Dictionary<string, object> { ["ShiftId"] = _shift.Id });
    }
    [RelayCommand] private async Task DetailsAsync(AssignmentRow row) => await Shell.Current.GoToAsync("assignment", new Dictionary<string, object> { ["AssignmentId"] = row.Id });

    [RelayCommand]
    private async Task MarkDoneAsync(AssignmentRow row)
    {
        var result = await _shiftService.SetAssignmentStatusAsync(row.Id, ExecutionStatus.Done, CancellationToken.None);
        Show(result);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (_shift is null)
            return;
        var result = await _shiftService.CompleteShiftAsync(_shift.Id, false, CancellationToken.None);
        if (result.ErrorCode == SafeErrorCode.ConfirmationRequired)
        {
            var confirm = await Shell.Current.DisplayAlert(UiStrings.CompleteShift, UiStrings.Get("CompleteShiftConfirmation"), UiStrings.CompleteShift, UiStrings.Cancel);
            if (confirm)
                result = await _shiftService.CompleteShiftAsync(_shift.Id, true, CancellationToken.None);
        }
        Show(result);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ReminderSettingsAsync()
    {
        await _reminderScheduler.OpenExactAlarmSettingsAsync(CancellationToken.None);
        await Shell.Current.GoToAsync("//settings");
    }
}
