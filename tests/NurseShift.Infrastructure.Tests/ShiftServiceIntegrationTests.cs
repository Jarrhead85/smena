using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;
using NurseShift.Core.Models;
using NurseShift.Infrastructure.Persistence;

namespace NurseShift.Infrastructure.Tests;

public sealed class ShiftServiceIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nurseshift-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecretStore _secrets = new();
    private readonly MemorySettingsStore _settings = new();
    private readonly TestClock _clock = new();
    private readonly FakeReminderScheduler _scheduler = new();
    private readonly NurseShiftDbContextFactory _factory;
    private readonly ShiftService _service;

    public ShiftServiceIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new NurseShiftDbContextFactory(Path.Combine(_directory, "test.db"), _secrets);
        _service = new ShiftService(_factory, _clock, _scheduler, _settings, new FakePhotoStore());
        using var context = _factory.CreateAsync(CancellationToken.None).GetAwaiter().GetResult();
        context.Database.Migrate();
    }

    [Fact]
    public async Task ManualAssignment_PersistsAcrossContextsAndSchedulesAfterCommit()
    {
        var shiftId = await CreateShiftAsync();
        var due = new DateTime(2026, 11, 10, 23, 30, 0);
        var result = await _service.AddAssignmentAsync(shiftId, Input(due), CancellationToken.None);
        Assert.True(result.IsSuccess);
        var details = await _service.GetShiftDetailsAsync(shiftId, CancellationToken.None);
        var assignment = Assert.Single(details!.Assignments);
        Assert.Equal(due, assignment.DueLocal);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 20, 30, 0, TimeSpan.Zero), assignment.DueAtUtc);
        Assert.Equal(assignment.Id, Assert.Single(_scheduler.Synchronized));
    }

    [Fact]
    public async Task FinalAssignment_ClinicalEditRequiresConfirmationAndResetsStatus()
    {
        var shiftId = await CreateShiftAsync();
        await _service.AddAssignmentAsync(shiftId, Input(new DateTime(2026, 11, 10, 23, 30, 0)), CancellationToken.None);
        var assignment = (await _service.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments.Single();
        await _service.SetAssignmentStatusAsync(assignment.Id, ExecutionStatus.Done, CancellationToken.None);
        assignment = (await _service.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments.Single();
        var changed = Input(new DateTime(2026, 11, 11, 0, 0, 0));
        var denied = await _service.EditAssignmentAsync(assignment.Id, changed, assignment.RowVersion, false, CancellationToken.None);
        Assert.Equal(SafeErrorCode.ConfirmationRequired, denied.ErrorCode);
        var allowed = await _service.EditAssignmentAsync(assignment.Id, changed, assignment.RowVersion, true, CancellationToken.None);
        Assert.True(allowed.IsSuccess);
        var updated = (await _service.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments.Single();
        Assert.Equal(ExecutionStatus.Pending, updated.ExecutionStatus);
        Assert.Null(updated.StatusChangedAtUtc);
    }

    [Fact]
    public async Task CompleteShift_WithPendingNeedsConfirmationAndCancelsReminders()
    {
        var shiftId = await CreateShiftAsync();
        await _service.AddAssignmentAsync(shiftId, Input(new DateTime(2026, 11, 10, 23, 30, 0)), CancellationToken.None);
        Assert.Equal(SafeErrorCode.ConfirmationRequired, (await _service.CompleteShiftAsync(shiftId, false, CancellationToken.None)).ErrorCode);
        Assert.True((await _service.CompleteShiftAsync(shiftId, true, CancellationToken.None)).IsSuccess);
        var shift = await _service.GetActiveShiftAsync(CancellationToken.None);
        Assert.Null(shift);
        Assert.Single(_scheduler.Cancelled);
        Assert.Single(await _service.GetHistoryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReactivateShift_RecreatesFutureReminders()
    {
        var shiftId = await CreateShiftAsync();
        await _service.AddAssignmentAsync(shiftId, Input(new DateTime(2026, 11, 10, 23, 30, 0)), CancellationToken.None);
        await _service.CompleteShiftAsync(shiftId, true, CancellationToken.None);
        _scheduler.Synchronized.Clear();

        var result = await _service.ReactivateShiftAsync(shiftId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var details = await _service.GetShiftDetailsAsync(shiftId, CancellationToken.None);
        var assignment = Assert.Single(details!.Assignments);
        Assert.Equal(2, assignment.Reminders.Count);
        Assert.Equal(assignment.Id, Assert.Single(_scheduler.Synchronized));
    }

    [Fact]
    public async Task DatabaseFile_IsNotPlainSqlite()
    {
        await CreateShiftAsync();
        var header = await File.ReadAllBytesAsync(Path.Combine(_directory, "test.db"));
        Assert.False(System.Text.Encoding.ASCII.GetString(header.AsSpan(0, Math.Min(16, header.Length))) == "SQLite format 3\0");
    }

    [Fact]
    public async Task ExactPatientSuggestion_LinksOnlyAfterExplicitSelection()
    {
        var shiftId = await CreateShiftAsync();
        await _service.AddAssignmentAsync(shiftId, Input(new DateTime(2026, 11, 10, 23, 30, 0)), CancellationToken.None);
        var matches = await _service.FindPatientSuggestionsAsync("Тестовый Пациент 001", "T1", "1", CancellationToken.None);
        var patient = Assert.Single(matches);

        var selectedInput = Input(new DateTime(2026, 11, 11, 0, 30, 0)) with { PatientId = patient.Id };
        var result = await _service.AddAssignmentAsync(shiftId, selectedInput, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var assignments = (await _service.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments;
        Assert.Equal(2, assignments.Count);
        Assert.Single(assignments.Select(item => item.PatientId).Distinct());
        await using var context = await _factory.CreateAsync(CancellationToken.None);
        Assert.Single(context.Patients);
    }

    [Fact]
    public async Task EditAssignment_CancelsOldSystemEventsBeforeSchedulingReplacement()
    {
        var shiftId = await CreateShiftAsync();
        await _service.AddAssignmentAsync(shiftId, Input(new DateTime(2026, 11, 10, 23, 30, 0)), CancellationToken.None);
        var assignment = (await _service.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments.Single();
        _scheduler.Events.Clear();

        var result = await _service.EditAssignmentAsync(assignment.Id,
            Input(new DateTime(2026, 11, 11, 0, 0, 0)) with { AdvanceLeadMinutes = 0 },
            assignment.RowVersion, false, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "Cancel:" + assignment.Id, "Synchronize:" + assignment.Id }, _scheduler.Events);
    }

    [Fact]
    public async Task Initialize_InterruptedFutureNotification_ReturnsToPlanned()
    {
        var reminderId = await CreateInterruptedReminderAsync();
        _clock.UtcNow = new DateTimeOffset(2026, 11, 10, 19, 0, 0, TimeSpan.Zero);

        await new DatabaseInitializer(_factory, _clock).InitializeAsync(CancellationToken.None);

        await using var verification = await _factory.CreateAsync(CancellationToken.None);
        var reminder = await verification.Reminders.SingleAsync(item => item.Id == reminderId);
        Assert.Equal(ScheduleState.Planned, reminder.ScheduleState);
        Assert.Null(reminder.SafeErrorCode);
    }

    [Fact]
    public async Task Initialize_InterruptedRecentNotification_RequestsSingleRecovery()
    {
        var reminderId = await CreateInterruptedReminderAsync();
        await using (var context = await _factory.CreateAsync(CancellationToken.None))
            _clock.UtcNow = (await context.Reminders.SingleAsync(item => item.Id == reminderId)).ScheduledAtUtc.AddMinutes(5);

        await new DatabaseInitializer(_factory, _clock).InitializeAsync(CancellationToken.None);

        await using var verification = await _factory.CreateAsync(CancellationToken.None);
        var reminder = await verification.Reminders.SingleAsync(item => item.Id == reminderId);
        Assert.Equal(ScheduleState.RecoveryPending, reminder.ScheduleState);
        Assert.Null(reminder.SafeErrorCode);
    }

    [Fact]
    public async Task Initialize_InterruptedExpiredNotification_MarksFailureInsteadOfMassDelivery()
    {
        var reminderId = await CreateInterruptedReminderAsync();
        await using (var context = await _factory.CreateAsync(CancellationToken.None))
            _clock.UtcNow = (await context.Reminders.SingleAsync(item => item.Id == reminderId)).ScheduledAtUtc.AddMinutes(16);

        await new DatabaseInitializer(_factory, _clock).InitializeAsync(CancellationToken.None);

        await using var verification = await _factory.CreateAsync(CancellationToken.None);
        var reminder = await verification.Reminders.SingleAsync(item => item.Id == reminderId);
        Assert.Equal(ScheduleState.Failed, reminder.ScheduleState);
        Assert.Equal(SafeErrorCode.SchedulingFailed.ToString(), reminder.SafeErrorCode);
    }

    private async Task<Guid> CreateInterruptedReminderAsync()
    {
        var shiftId = await CreateShiftAsync();
        await _service.AddAssignmentAsync(shiftId, Input(new DateTime(2026, 11, 10, 23, 30, 0)), CancellationToken.None);
        var reminderId = (await _service.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments.Single().Reminders.First().Id;
        await using var context = await _factory.CreateAsync(CancellationToken.None);
        var reminder = await context.Reminders.SingleAsync(item => item.Id == reminderId);
        reminder.ScheduleState = ScheduleState.Dispatching;
        await context.SaveChangesAsync(CancellationToken.None);
        return reminderId;
    }

    private async Task<Guid> CreateShiftAsync()
    {
        var result = await _service.CreateShiftAsync(new DateTime(2026, 11, 10, 22, 0, 0), new DateTime(2026, 11, 11, 6, 0, 0), "Europe/Moscow", CancellationToken.None);
        Assert.True(result.IsSuccess);
        return (await _service.GetActiveShiftAsync(CancellationToken.None))!.Id;
    }

    private static AssignmentInput Input(DateTime due) => new("Тестовый Пациент 001", "Тестовая процедура", due, "T1", "1", null, null, 10);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
