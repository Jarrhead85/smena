using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NurseShift.App.Resources.Strings;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;
using NurseShift.Core.Services;
using NurseShift.Infrastructure.Persistence;
using Application = Android.App.Application;

namespace NurseShift.App.Notifications;

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class ReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var pending = GoAsync();
        _ = ShowAsync(intent, pending);
    }

    private static async Task ShowAsync(Intent? intent, PendingResult pending)
    {
        NurseShiftDbContext? db = null;
        Reminder? reminder = null;
        try
        {
            if (intent?.Action != "ru.smena.app.action.REMINDER" || !Guid.TryParse(intent.GetStringExtra("reminder_id"), out var reminderId))
                return;
            using var scope = AndroidServices.Services?.CreateScope();
            if (scope is null)
                return;
            var factory = scope.ServiceProvider.GetRequiredService<INurseShiftDbContextFactory>();
            db = await factory.CreateAsync(CancellationToken.None);
            reminder = await db.Reminders.Include(item => item.Assignment).ThenInclude(item => item!.Shift)
                .SingleOrDefaultAsync(item => item.Id == reminderId);
            if (reminder?.Assignment?.Shift is null || reminder.ScheduleState != ScheduleState.Scheduled ||
                reminder.Assignment.ReviewState != ReviewState.Confirmed || reminder.Assignment.ExecutionStatus != ExecutionStatus.Pending ||
                reminder.Assignment.Shift.Status != ShiftStatus.Active)
                return;
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            var claimed = await db.Reminders.Where(item => item.Id == reminderId && item.ScheduleState == ScheduleState.Scheduled)
                .ExecuteUpdateAsync(item => item.SetProperty(r => r.ScheduleState, ScheduleState.Dispatching)
                    .SetProperty(r => r.UpdatedAtUtc, now));
            if (claimed == 0)
                return;
            reminder.ScheduleState = ScheduleState.Dispatching;
            reminder.UpdatedAtUtc = now;
            var settings = await scope.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync(CancellationToken.None);
            NotificationPresenter.Show(reminder, settings);
            reminder.ScheduleState = ScheduleState.Posted;
            reminder.SafeErrorCode = null;
            reminder.UpdatedAtUtc = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            if (db is not null && reminder is not null)
            {
                try
                {
                    reminder.ScheduleState = ScheduleState.Failed;
                    reminder.SafeErrorCode = SafeErrorCode.SchedulingFailed.ToString();
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                }
            }
        }
        finally
        {
            if (db is not null)
                await db.DisposeAsync();
            pending.Finish();
        }
    }
}

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class DoneReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var pending = GoAsync();
        _ = CompleteAsync(intent, pending);
    }

    private static async Task CompleteAsync(Intent? intent, PendingResult pending)
    {
        try
        {
            if (intent?.Action != "ru.smena.app.action.DONE" || !Guid.TryParse(intent.GetStringExtra("assignment_id"), out var id))
                return;
            using var scope = AndroidServices.Services?.CreateScope();
            if (scope is null)
                return;
            await scope.ServiceProvider.GetRequiredService<IShiftService>().SetAssignmentStatusAsync(id, ExecutionStatus.Done, CancellationToken.None);
        }
        catch (Exception)
        {
        }
        finally
        {
            pending.Finish();
        }
    }
}

[BroadcastReceiver(Enabled = true, Exported = false)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionTimeChanged, Intent.ActionTimezoneChanged, "android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED" })]
[IntentFilter(new[] { Intent.ActionPackageReplaced }, DataScheme = "package")]
public sealed class RescheduleReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var pending = GoAsync();
        _ = RescheduleAsync(intent, pending);
    }

    private static async Task RescheduleAsync(Intent? intent, PendingResult pending)
    {
        try
        {
            if (intent?.Action == Intent.ActionPackageReplaced && intent.Data?.SchemeSpecificPart != Application.Context.PackageName)
                return;
            using var scope = AndroidServices.Services?.CreateScope();
            if (scope is null)
                return;
            var factory = scope.ServiceProvider.GetRequiredService<INurseShiftDbContextFactory>();
            await using var db = await factory.CreateAsync(CancellationToken.None);
            var ids = await db.Reminders.Where(item => item.ScheduleState != ScheduleState.Cancelled).Select(item => item.Id).ToListAsync();
            await db.Reminders.Where(item => ids.Contains(item.Id)).ExecuteUpdateAsync(item => item.SetProperty(r => r.ScheduleState, ScheduleState.Planned));
            await scope.ServiceProvider.GetRequiredService<IReminderScheduler>().SynchronizeAllFutureAsync(CancellationToken.None);
        }
        catch (Exception)
        {
        }
        finally
        {
            pending.Finish();
        }
    }
}

internal static class NotificationPresenter
{
    public const string ChannelId = "shift_assignments";

    public static void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;
        var manager = (NotificationManager?)Application.Context.GetSystemService(Context.NotificationService);
        var channel = new NotificationChannel(ChannelId, UiStrings.Get("NotificationChannel"), NotificationImportance.High)
        {
            Description = UiStrings.Get("NotificationChannelDescription")
        };
        manager?.CreateNotificationChannel(channel);
    }

    public static void Show(Reminder reminder, AppSettings settings)
    {
        var assignment = reminder.Assignment!;
        var context = Application.Context;
        var openIntent = new Intent(context, typeof(MainActivity));
        openIntent.SetAction("ru.smena.app.action.OPEN_ASSIGNMENT");
        openIntent.PutExtra("assignment_id", assignment.Id.ToString("N"));
        openIntent.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        var open = PendingIntent.GetActivity(context, reminder.AndroidRequestCode, openIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var publicNotification = new NotificationCompat.Builder(context, ChannelId)
            .SetContentTitle(UiStrings.Get("PublicNotificationTitle"))
            .SetContentText(UiStrings.Get("PublicNotificationText"))
            .SetSmallIcon(Resource.Mipmap.appicon)
            .Build();
        var title = reminder.Kind == ReminderKind.Advance
            ? UiStrings.Format("AdvanceNotificationFormat", assignment.AdvanceLeadMinutes, assignment.Procedure)
            : $"{assignment.DueLocal:HH:mm} · {assignment.Procedure}";
        var location = string.Join(" · ", new[]
        {
            assignment.Room is null ? null : UiStrings.Format("RoomShortFormat", assignment.Room),
            assignment.Bed is null ? null : UiStrings.Format("BedShortFormat", assignment.Bed)
        }.Where(item => item is not null));
        var builder = new NotificationCompat.Builder(context, ChannelId)
            .SetContentTitle(title)
            .SetContentText($"{assignment.PatientName} {location}")
            .SetStyle(new NotificationCompat.BigTextStyle().BigText($"{assignment.PatientName}\n{location}"))
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentIntent(open)
            .SetAutoCancel(true)
            .SetVisibility((int)(settings.PrivateLockScreenNotifications ? NotificationVisibility.Private : NotificationVisibility.Public))
            .SetPublicVersion(publicNotification);
        if (reminder.Kind == ReminderKind.Due)
        {
            var doneIntent = new Intent(context, typeof(DoneReceiver));
            doneIntent.SetAction("ru.smena.app.action.DONE");
            doneIntent.SetData(Android.Net.Uri.Parse($"nurseshift://done/{assignment.Id:N}"));
            doneIntent.PutExtra("assignment_id", assignment.Id.ToString("N"));
            var done = PendingIntent.GetBroadcast(context, reminder.AndroidRequestCode, doneIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            builder.AddAction(0, UiStrings.Done, done);
        }
        NotificationManagerCompat.From(context).Notify(reminder.AndroidRequestCode, builder.Build());
    }

    public static void ShowTest()
    {
        var context = Application.Context;
        var notification = new NotificationCompat.Builder(context, ChannelId)
            .SetContentTitle(UiStrings.Get("TestNotificationTitle"))
            .SetContentText(UiStrings.Get("TestNotificationText"))
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetAutoCancel(true)
            .Build();
        NotificationManagerCompat.From(context).Notify(int.MinValue, notification);
    }
}
