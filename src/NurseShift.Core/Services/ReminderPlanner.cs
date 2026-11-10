using NurseShift.Core.Enums;
using NurseShift.Core.Models;

namespace NurseShift.Core.Services;

public sealed class ReminderPlanner
{
    public IReadOnlyList<Reminder> CreateFuture(Assignment assignment, DateTimeOffset now)
    {
        if (assignment.ReviewState != ReviewState.Confirmed ||
            assignment.ExecutionStatus != ExecutionStatus.Pending ||
            assignment.DueAtUtc is null)
            return [];

        var reminders = new List<Reminder>();
        if (assignment.AdvanceLeadMinutes > 0)
        {
            var advanceAt = assignment.DueAtUtc.Value.AddMinutes(-assignment.AdvanceLeadMinutes);
            if (advanceAt > now)
                reminders.Add(Create(assignment, ReminderKind.Advance, advanceAt, now));
        }
        if (assignment.DueAtUtc.Value > now)
            reminders.Add(Create(assignment, ReminderKind.Due, assignment.DueAtUtc.Value, now));
        return reminders;
    }

    private static Reminder Create(Assignment assignment, ReminderKind kind, DateTimeOffset scheduledAt, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        AssignmentId = assignment.Id,
        Kind = kind,
        ScheduledAtUtc = scheduledAt,
        AndroidRequestCode = StableRequestCode(assignment.Id, kind),
        ScheduleState = ScheduleState.Planned,
        UpdatedAtUtc = now
    };

    public static int StableRequestCode(Guid assignmentId, ReminderKind kind)
    {
        var bytes = assignmentId.ToByteArray();
        var value = bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24;
        return (value & 0x3FFFFFFF) | ((int)kind << 30);
    }
}
