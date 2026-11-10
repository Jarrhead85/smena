using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;
using NurseShift.Core.Services;
using NurseShift.Infrastructure.Persistence;
using Application = Android.App.Application;

namespace NurseShift.App.Notifications;

public sealed class AndroidReminderScheduler(INurseShiftDbContextFactory contextFactory, IClock clock) : IReminderScheduler
{
    private const string ReminderAction = "ru.smena.app.action.REMINDER";
    private readonly Context _context = Application.Context;

    public Task<ReminderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var notifications = NotificationManagerCompat.From(_context).AreNotificationsEnabled();
        var alarmManager = (AlarmManager?)_context.GetSystemService(Context.AlarmService);
        var exact = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            exact = alarmManager?.CanScheduleExactAlarms() == true;
        return Task.FromResult(new ReminderAvailability(notifications, exact));
    }

    public async Task<OperationResult> SynchronizeAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var assignment = await context.Assignments.Include(item => item.Shift).Include(item => item.Reminders)
            .SingleOrDefaultAsync(item => item.Id == assignmentId, cancellationToken);
        if (assignment is null)
            return new OperationResult(SafeErrorCode.AssignmentNotFound);
        if (assignment.ReviewState != ReviewState.Confirmed || assignment.ExecutionStatus != ExecutionStatus.Pending || assignment.Shift?.Status != ShiftStatus.Active)
        {
            await CancelForAssignmentAsync(assignmentId, cancellationToken);
            return OperationResult.Success();
        }
        var availability = await GetAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable)
        {
            await MarkFailedAsync(context, assignment.Reminders, SafeErrorCode.SchedulingDenied, cancellationToken);
            return new OperationResult(SafeErrorCode.SchedulingDenied);
        }

        try
        {
            var alarmManager = (AlarmManager?)_context.GetSystemService(Context.AlarmService) ?? throw new InvalidOperationException();
            foreach (var reminder in assignment.Reminders.Where(item => item.ScheduledAtUtc > clock.UtcNow &&
                         item.ScheduleState is ScheduleState.Planned or ScheduleState.Failed or ScheduleState.Scheduled))
            {
                Cancel(alarmManager, assignmentId, reminder.Kind);
                var pendingIntent = CreateReminderIntent(reminder, PendingIntentFlags.UpdateCurrent);
                alarmManager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, reminder.ScheduledAtUtc.ToUnixTimeMilliseconds(), pendingIntent);
                reminder.ScheduleState = ScheduleState.Scheduled;
                reminder.SafeErrorCode = null;
                reminder.UpdatedAtUtc = clock.UtcNow;
            }
            await context.SaveChangesAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await MarkFailedAsync(context, assignment.Reminders, SafeErrorCode.SchedulingFailed, cancellationToken);
            return new OperationResult(SafeErrorCode.SchedulingFailed);
        }
    }

    public async Task<OperationResult> SynchronizeAllFutureAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var candidates = await context.Assignments.AsNoTracking().Where(item => item.ReviewState == ReviewState.Confirmed &&
            item.ExecutionStatus == ExecutionStatus.Pending && item.Shift.Status == ShiftStatus.Active)
            .Select(item => new { item.Id, item.DueAtUtc }).ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        var ids = candidates.Where(item => item.DueAtUtc > now).Select(item => item.Id);
        foreach (var id in ids)
            await SynchronizeAssignmentAsync(id, cancellationToken);
        return OperationResult.Success();
    }

    public Task CancelForAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var alarmManager = (AlarmManager?)_context.GetSystemService(Context.AlarmService);
        if (alarmManager is not null)
        {
            Cancel(alarmManager, assignmentId, ReminderKind.Advance);
            Cancel(alarmManager, assignmentId, ReminderKind.Due);
        }
        NotificationManagerCompat.From(_context).Cancel(ReminderPlanner.StableRequestCode(assignmentId, ReminderKind.Advance));
        NotificationManagerCompat.From(_context).Cancel(ReminderPlanner.StableRequestCode(assignmentId, ReminderKind.Due));
        return Task.CompletedTask;
    }

    public async Task RequestNotificationPermissionAsync(CancellationToken cancellationToken)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            await Permissions.RequestAsync<Permissions.PostNotifications>();
    }

    public Task OpenExactAlarmSettingsAsync(CancellationToken cancellationToken)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
        {
            var intent = new Intent(Android.Provider.Settings.ActionRequestScheduleExactAlarm, Android.Net.Uri.Parse("package:" + _context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
        }
        return Task.CompletedTask;
    }

    private PendingIntent CreateReminderIntent(Reminder reminder, PendingIntentFlags flags)
    {
        var intent = new Intent(_context, typeof(ReminderReceiver));
        intent.SetAction(ReminderAction);
        intent.SetData(Android.Net.Uri.Parse($"nurseshift://assignment/{reminder.AssignmentId:N}/{(int)reminder.Kind}"));
        intent.PutExtra("reminder_id", reminder.Id.ToString("N"));
        return PendingIntent.GetBroadcast(_context, reminder.AndroidRequestCode, intent, flags | PendingIntentFlags.Immutable)!;
    }

    private void Cancel(AlarmManager alarmManager, Guid assignmentId, ReminderKind kind)
    {
        var requestCode = ReminderPlanner.StableRequestCode(assignmentId, kind);
        var intent = new Intent(_context, typeof(ReminderReceiver));
        intent.SetAction(ReminderAction);
        intent.SetData(Android.Net.Uri.Parse($"nurseshift://assignment/{assignmentId:N}/{(int)kind}"));
        var pending = PendingIntent.GetBroadcast(_context, requestCode, intent, PendingIntentFlags.NoCreate | PendingIntentFlags.Immutable);
        if (pending is not null)
        {
            alarmManager.Cancel(pending);
            pending.Cancel();
        }
        pending = PendingIntent.GetBroadcast(_context, requestCode, CreateReminderLookupIntent(assignmentId, kind), PendingIntentFlags.NoCreate | PendingIntentFlags.Immutable);
        if (pending is not null)
        {
            alarmManager.Cancel(pending);
            pending.Cancel();
        }
    }

    private Intent CreateReminderLookupIntent(Guid assignmentId, ReminderKind kind)
    {
        var intent = new Intent(_context, typeof(ReminderReceiver));
        intent.SetAction(ReminderAction);
        intent.SetData(Android.Net.Uri.Parse($"nurseshift://assignment/{assignmentId:N}/{(int)kind}"));
        return intent;
    }

    private async Task MarkFailedAsync(NurseShiftDbContext context, IEnumerable<Reminder> reminders, SafeErrorCode code, CancellationToken cancellationToken)
    {
        foreach (var reminder in reminders)
        {
            reminder.ScheduleState = ScheduleState.Failed;
            reminder.SafeErrorCode = code.ToString();
            reminder.UpdatedAtUtc = clock.UtcNow;
        }
        await context.SaveChangesAsync(cancellationToken);
    }
}
