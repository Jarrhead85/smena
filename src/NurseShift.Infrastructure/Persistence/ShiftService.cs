using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;
using NurseShift.Core.Services;

namespace NurseShift.Infrastructure.Persistence;

public sealed class ShiftService(
    INurseShiftDbContextFactory contextFactory,
    IClock clock,
    IReminderScheduler reminderScheduler,
    ISettingsStore settingsStore,
    IPhotoRetentionStore photoRetentionStore) : IShiftService
{
    private readonly ShiftTimeService _timeService = new();
    private readonly ReminderPlanner _reminderPlanner = new();

    public async Task<Shift?> GetActiveShiftAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        return await context.Shifts.AsNoTracking().SingleOrDefaultAsync(item => item.Status == ShiftStatus.Active, cancellationToken);
    }

    public async Task<ShiftDetails?> GetShiftDetailsAsync(Guid shiftId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var shift = await context.Shifts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == shiftId, cancellationToken);
        if (shift is null)
            return null;
        var assignments = await context.Assignments.AsNoTracking().Include(item => item.Reminders)
            .Where(item => item.ShiftId == shiftId && item.ReviewState == ReviewState.Confirmed)
            .OrderBy(item => item.DueLocal).ToListAsync(cancellationToken);
        return new ShiftDetails(shift, assignments, assignments.SelectMany(item => item.Reminders)
            .Any(item => item.ScheduleState == ScheduleState.Failed));
    }

    public async Task<IReadOnlyList<Shift>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        return await context.Shifts.AsNoTracking().Include(item => item.Assignments)
            .Where(item => item.Status == ShiftStatus.Completed)
            .OrderByDescending(item => item.EndLocal).ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> CreateShiftAsync(DateTime startLocal, DateTime endLocal, string timeZoneId, CancellationToken cancellationToken)
    {
        var validation = _timeService.ValidateShift(startLocal, endLocal, timeZoneId, out var shift);
        if (!validation.IsSuccess || shift is null)
            return validation;

        await using var context = await contextFactory.CreateAsync(cancellationToken);
        if (await context.Shifts.AnyAsync(item => item.Status == ShiftStatus.Active, cancellationToken))
            return new OperationResult(SafeErrorCode.ActiveShiftExists);
        shift.CreatedAtUtc = clock.UtcNow;
        shift.UpdatedAtUtc = clock.UtcNow;
        shift.RowVersion = 1;
        context.Shifts.Add(shift);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (DbUpdateException)
        {
            return new OperationResult(SafeErrorCode.ActiveShiftExists);
        }
    }

    public async Task<OperationResult> AddAssignmentAsync(Guid shiftId, AssignmentInput input, CancellationToken cancellationToken)
    {
        var inputError = ValidateInput(input);
        if (inputError is not null)
            return inputError;
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var shift = await context.Shifts.SingleOrDefaultAsync(item => item.Id == shiftId, cancellationToken);
        if (shift is null)
            return new OperationResult(SafeErrorCode.ShiftNotFound);
        if (shift.Status != ShiftStatus.Active)
            return new OperationResult(SafeErrorCode.ShiftNotActive);
        var timeResult = _timeService.ResolveDueTime(shift, input.DueLocal!.Value, out var dueAtUtc);
        if (!timeResult.IsSuccess)
            return timeResult;

        var now = clock.UtcNow;
        Patient patient;
        if (input.PatientId.HasValue)
        {
            var selectedPatient = await FindExactPatientAsync(context, input, cancellationToken);
            if (selectedPatient is null)
                return new OperationResult(SafeErrorCode.Validation);
            patient = selectedPatient;
            patient.LastUsedAtUtc = now;
        }
        else
        {
            patient = new Patient { Id = Guid.NewGuid(), FullName = input.PatientName!.Trim(), CreatedAtUtc = now, LastUsedAtUtc = now };
        }
        var assignment = CreateAssignment(shiftId, input, dueAtUtc, now, patient.Id);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!input.PatientId.HasValue)
            context.Patients.Add(patient);
        context.Assignments.Add(assignment);
        context.Reminders.AddRange(_reminderPlanner.CreateFuture(assignment, now));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await SynchronizeAfterCommitAsync(assignment.Id, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> EditAssignmentAsync(Guid assignmentId, AssignmentInput input, long expectedRowVersion, bool confirmedReset, CancellationToken cancellationToken)
    {
        var inputError = ValidateInput(input);
        if (inputError is not null)
            return inputError;
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var assignment = await context.Assignments.Include(item => item.Shift).Include(item => item.Reminders)
            .SingleOrDefaultAsync(item => item.Id == assignmentId && item.ReviewState == ReviewState.Confirmed, cancellationToken);
        if (assignment?.Shift is null)
            return new OperationResult(SafeErrorCode.AssignmentNotFound);
        if (assignment.Shift.Status != ShiftStatus.Active)
            return new OperationResult(SafeErrorCode.ShiftNotActive);
        if (assignment.RowVersion != expectedRowVersion)
            return new OperationResult(SafeErrorCode.ConcurrentUpdate);

        var clinicalChange = !string.Equals(assignment.PatientName, input.PatientName?.Trim(), StringComparison.Ordinal) ||
                             !string.Equals(assignment.Procedure, input.Procedure?.Trim(), StringComparison.Ordinal) ||
                             assignment.DueLocal != input.DueLocal;
        if (clinicalChange && assignment.ExecutionStatus != ExecutionStatus.Pending && !confirmedReset)
            return new OperationResult(SafeErrorCode.ConfirmationRequired);
        var timeResult = _timeService.ResolveDueTime(assignment.Shift, input.DueLocal!.Value, out var dueAtUtc);
        if (!timeResult.IsSuccess)
            return timeResult;

        var now = clock.UtcNow;
        assignment.PatientName = input.PatientName!.Trim();
        assignment.Procedure = input.Procedure!.Trim();
        assignment.Room = Normalize(input.Room);
        assignment.Bed = Normalize(input.Bed);
        assignment.Dosage = Normalize(input.Dosage);
        assignment.Comment = Normalize(input.Comment);
        assignment.DueLocal = DateTime.SpecifyKind(input.DueLocal.Value, DateTimeKind.Unspecified);
        assignment.DueAtUtc = dueAtUtc;
        assignment.AdvanceLeadMinutes = input.AdvanceLeadMinutes;
        assignment.UpdatedAtUtc = now;
        assignment.RowVersion++;
        if (clinicalChange && assignment.ExecutionStatus != ExecutionStatus.Pending)
        {
            assignment.ExecutionStatus = ExecutionStatus.Pending;
            assignment.StatusChangedAtUtc = null;
        }
        if (assignment.PatientId is null || clinicalChange)
        {
            if (input.PatientId.HasValue)
            {
                var selectedPatient = await FindExactPatientAsync(context, input, cancellationToken);
                if (selectedPatient is null)
                    return new OperationResult(SafeErrorCode.Validation);
                selectedPatient.LastUsedAtUtc = now;
                assignment.PatientId = selectedPatient.Id;
            }
            else
            {
                var patient = new Patient { Id = Guid.NewGuid(), FullName = assignment.PatientName, CreatedAtUtc = now, LastUsedAtUtc = now };
                context.Patients.Add(patient);
                assignment.PatientId = patient.Id;
            }
        }

        context.Reminders.RemoveRange(assignment.Reminders);
        context.Reminders.AddRange(_reminderPlanner.CreateFuture(assignment, now));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await reminderScheduler.CancelForAssignmentAsync(assignment.Id, cancellationToken);
            await SynchronizeAfterCommitAsync(assignment.Id, cancellationToken);
            return OperationResult.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new OperationResult(SafeErrorCode.ConcurrentUpdate);
        }
    }

    public async Task<OperationResult> DeleteAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var assignment = await context.Assignments.Include(item => item.Shift)
            .SingleOrDefaultAsync(item => item.Id == assignmentId, cancellationToken);
        if (assignment?.Shift is null)
            return new OperationResult(SafeErrorCode.AssignmentNotFound);
        if (assignment.Shift.Status != ShiftStatus.Active)
            return new OperationResult(SafeErrorCode.ShiftNotActive);
        context.Assignments.Remove(assignment);
        await context.SaveChangesAsync(cancellationToken);
        await reminderScheduler.CancelForAssignmentAsync(assignmentId, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> SetAssignmentStatusAsync(Guid assignmentId, ExecutionStatus status, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var assignment = await context.Assignments.Include(item => item.Shift).Include(item => item.Reminders)
            .SingleOrDefaultAsync(item => item.Id == assignmentId && item.ReviewState == ReviewState.Confirmed, cancellationToken);
        if (assignment?.Shift is null)
            return new OperationResult(SafeErrorCode.AssignmentNotFound);
        if (assignment.Shift.Status != ShiftStatus.Active)
            return new OperationResult(SafeErrorCode.ShiftNotActive);
        if (assignment.ExecutionStatus == status)
            return OperationResult.Success();

        var now = clock.UtcNow;
        assignment.ExecutionStatus = status;
        assignment.StatusChangedAtUtc = status == ExecutionStatus.Pending ? null : now;
        assignment.UpdatedAtUtc = now;
        assignment.RowVersion++;
        context.Reminders.RemoveRange(assignment.Reminders);
        if (status == ExecutionStatus.Pending)
            context.Reminders.AddRange(_reminderPlanner.CreateFuture(assignment, now));
        await context.SaveChangesAsync(cancellationToken);
        if (status == ExecutionStatus.Pending)
            await SynchronizeAfterCommitAsync(assignmentId, cancellationToken);
        else
            await reminderScheduler.CancelForAssignmentAsync(assignmentId, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CompleteShiftAsync(Guid shiftId, bool confirmed, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var shift = await context.Shifts.Include(item => item.Assignments).SingleOrDefaultAsync(item => item.Id == shiftId, cancellationToken);
        if (shift is null)
            return new OperationResult(SafeErrorCode.ShiftNotFound);
        if (shift.Status != ShiftStatus.Active)
            return new OperationResult(SafeErrorCode.ShiftNotActive);
        if (!confirmed && shift.Assignments.Any(item => item.ReviewState == ReviewState.Confirmed && item.ExecutionStatus == ExecutionStatus.Pending))
            return new OperationResult(SafeErrorCode.ConfirmationRequired);

        var now = clock.UtcNow;
        var settings = await settingsStore.GetAsync(cancellationToken);
        shift.Status = ShiftStatus.Completed;
        shift.CompletedAtUtc = now;
        shift.UpdatedAtUtc = now;
        shift.HistoryExpiresAtUtc = settings.HistoryRetentionDays is int days ? now.AddDays(days) : null;
        shift.RowVersion++;
        var assignmentIds = shift.Assignments.Select(item => item.Id).ToArray();
        var reminders = await context.Reminders.Where(item => assignmentIds.Contains(item.AssignmentId)).ToListAsync(cancellationToken);
        context.Reminders.RemoveRange(reminders);
        await context.SaveChangesAsync(cancellationToken);
        foreach (var assignmentId in assignmentIds)
            await reminderScheduler.CancelForAssignmentAsync(assignmentId, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ReactivateShiftAsync(Guid shiftId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        if (await context.Shifts.AnyAsync(item => item.Status == ShiftStatus.Active, cancellationToken))
            return new OperationResult(SafeErrorCode.ActiveShiftExists);
        var shift = await context.Shifts.SingleOrDefaultAsync(item => item.Id == shiftId, cancellationToken);
        if (shift is null)
            return new OperationResult(SafeErrorCode.ShiftNotFound);
        if (shift.Status != ShiftStatus.Completed)
            return new OperationResult(SafeErrorCode.ShiftNotActive);
        var now = clock.UtcNow;
        var assignments = await context.Assignments.Include(item => item.Reminders)
            .Where(item => item.ShiftId == shiftId && item.ReviewState == ReviewState.Confirmed && item.ExecutionStatus == ExecutionStatus.Pending)
            .ToListAsync(cancellationToken);
        shift.Status = ShiftStatus.Active;
        shift.CompletedAtUtc = null;
        shift.HistoryExpiresAtUtc = null;
        shift.UpdatedAtUtc = now;
        shift.RowVersion++;
        foreach (var assignment in assignments)
        {
            context.Reminders.RemoveRange(assignment.Reminders);
            context.Reminders.AddRange(_reminderPlanner.CreateFuture(assignment, now));
        }
        await context.SaveChangesAsync(cancellationToken);
        foreach (var assignment in assignments)
            await SynchronizeAfterCommitAsync(assignment.Id, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteShiftAsync(Guid shiftId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var shift = await context.Shifts.Include(item => item.Assignments).SingleOrDefaultAsync(item => item.Id == shiftId, cancellationToken);
        if (shift is null)
            return new OperationResult(SafeErrorCode.ShiftNotFound);
        var pages = await context.SourceImages.Where(item => context.ImportSessions.Any(session => session.Id == item.ImportSessionId && session.ShiftId == shiftId)).ToListAsync(cancellationToken);
        context.Shifts.Remove(shift);
        await context.SaveChangesAsync(cancellationToken);
        foreach (var assignment in shift.Assignments)
            await reminderScheduler.CancelForAssignmentAsync(assignment.Id, cancellationToken);
        foreach (var page in pages)
            await photoRetentionStore.FinalizeAsync(page, false, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<IReadOnlyList<Patient>> FindPatientSuggestionsAsync(string fullName, string? room, string? bed, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var name = fullName.Trim();
        var patientIds = context.Assignments.AsNoTracking()
            .Where(item => item.PatientName == name && item.Room == room && item.Bed == bed && item.PatientId != null)
            .Select(item => item.PatientId!.Value).Distinct();
        var patients = await context.Patients.AsNoTracking().Where(item => patientIds.Contains(item.Id)).ToListAsync(cancellationToken);
        return patients.OrderByDescending(item => item.LastUsedAtUtc).ToList();
    }

    private Assignment CreateAssignment(Guid shiftId, AssignmentInput input, DateTimeOffset dueAtUtc, DateTimeOffset now, Guid patientId) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = shiftId,
        PatientId = patientId,
        PatientName = input.PatientName!.Trim(),
        Procedure = input.Procedure!.Trim(),
        DueLocal = DateTime.SpecifyKind(input.DueLocal!.Value, DateTimeKind.Unspecified),
        DueAtUtc = dueAtUtc,
        Room = Normalize(input.Room),
        Bed = Normalize(input.Bed),
        Dosage = Normalize(input.Dosage),
        Comment = Normalize(input.Comment),
        ReviewState = ReviewState.Confirmed,
        ExecutionStatus = ExecutionStatus.Pending,
        AdvanceLeadMinutes = input.AdvanceLeadMinutes,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        RowVersion = 1
    };

    private static OperationResult? ValidateInput(AssignmentInput input)
    {
        if (string.IsNullOrWhiteSpace(input.PatientName) || string.IsNullOrWhiteSpace(input.Procedure) || input.DueLocal is null)
            return new OperationResult(SafeErrorCode.Validation);
        if (input.PatientName.Trim().Length > 200 || input.Procedure.Trim().Length > 500 ||
            input.Room?.Length > 30 || input.Bed?.Length > 30 || input.Dosage?.Length > 500 || input.Comment?.Length > 2000)
            return new OperationResult(SafeErrorCode.Validation);
        if (input.AdvanceLeadMinutes is < 0 or > 60)
            return new OperationResult(SafeErrorCode.Validation);
        return null;
    }

    private static Task<Patient?> FindExactPatientAsync(NurseShiftDbContext context, AssignmentInput input, CancellationToken cancellationToken)
    {
        var name = input.PatientName!.Trim();
        var room = Normalize(input.Room);
        var bed = Normalize(input.Bed);
        return context.Patients.SingleOrDefaultAsync(item => item.Id == input.PatientId && item.FullName == name &&
            context.Assignments.Any(assignment => assignment.PatientId == input.PatientId && assignment.Room == room && assignment.Bed == bed), cancellationToken);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task SynchronizeAfterCommitAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        try
        {
            await reminderScheduler.SynchronizeAssignmentAsync(assignmentId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await using var context = await contextFactory.CreateAsync(CancellationToken.None);
            var reminders = await context.Reminders.Where(item => item.AssignmentId == assignmentId).ToListAsync(CancellationToken.None);
            foreach (var reminder in reminders)
            {
                reminder.ScheduleState = ScheduleState.Failed;
                reminder.SafeErrorCode = SafeErrorCode.SchedulingFailed.ToString();
                reminder.UpdatedAtUtc = clock.UtcNow;
            }
            await context.SaveChangesAsync(CancellationToken.None);
        }
    }
}
