using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Services;

namespace NurseShift.Infrastructure.Persistence;

public sealed class DatabaseInitializer(INurseShiftDbContextFactory contextFactory, IClock clock)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        var processing = await context.ImportSessions.Where(item => item.Status == ImportStatus.Processing).ToListAsync(cancellationToken);
        foreach (var session in processing)
        {
            session.Status = ImportStatus.Review;
            session.RowVersion++;
        }
        var uncertainPages = await context.SourceImages.Where(item =>
            item.ProcessingState == ImageProcessingState.Uploading ||
            item.ProcessingState == ImageProcessingState.Recognizing).ToListAsync(cancellationToken);
        foreach (var page in uncertainPages)
        {
            page.ProcessingState = ImageProcessingState.Failed;
            page.SafeErrorCode = SafeErrorCode.Unknown;
        }
        var now = clock.UtcNow;
        var interrupted = await context.Reminders.Include(item => item.Assignment).ThenInclude(item => item!.Shift)
            .Where(item => item.ScheduleState == ScheduleState.Dispatching || item.ScheduleState == ScheduleState.RecoveryPending)
            .ToListAsync(cancellationToken);
        foreach (var reminder in interrupted)
        {
            var assignment = reminder.Assignment;
            if (assignment?.Shift is null || assignment.ReviewState != ReviewState.Confirmed ||
                assignment.ExecutionStatus != ExecutionStatus.Pending || assignment.Shift.Status != ShiftStatus.Active)
            {
                reminder.ScheduleState = ScheduleState.Cancelled;
                reminder.SafeErrorCode = null;
            }
            else if (reminder.ScheduledAtUtc > now)
            {
                reminder.ScheduleState = ScheduleState.Planned;
                reminder.SafeErrorCode = null;
            }
            else if (reminder.ScheduledAtUtc >= now - ReminderPlanner.RecoveryWindow)
            {
                reminder.ScheduleState = ScheduleState.RecoveryPending;
                reminder.SafeErrorCode = null;
            }
            else
            {
                reminder.ScheduleState = ScheduleState.Failed;
                reminder.SafeErrorCode = SafeErrorCode.SchedulingFailed.ToString();
            }
            reminder.UpdatedAtUtc = now;
        }
        await context.SaveChangesAsync(cancellationToken);
    }
}
