using NurseShift.Core.Enums;
using NurseShift.Core.Models;

namespace NurseShift.Core.Services;

public enum FeedSection { Overdue, Now, Soon, Later, Completed }

public sealed record FeedGroup(FeedSection Section, IReadOnlyList<Assignment> Assignments);

public sealed class FeedService
{
    public IReadOnlyList<FeedGroup> Group(IEnumerable<Assignment> assignments, DateTimeOffset now)
    {
        var source = assignments.Where(item => item.ReviewState == ReviewState.Confirmed).ToArray();
        var groups = new List<FeedGroup>();
        Add(FeedSection.Overdue, source.Where(item => IsPending(item) && item.DueAtUtc < now.AddMinutes(-5)).OrderBy(item => item.DueAtUtc));
        Add(FeedSection.Now, source.Where(item => IsPending(item) && item.DueAtUtc >= now.AddMinutes(-5) && item.DueAtUtc <= now.AddMinutes(5)).OrderBy(item => item.DueAtUtc));
        Add(FeedSection.Soon, source.Where(item => IsPending(item) && item.DueAtUtc > now.AddMinutes(5) && item.DueAtUtc <= now.AddMinutes(60)).OrderBy(item => item.DueAtUtc));
        Add(FeedSection.Later, source.Where(item => IsPending(item) && item.DueAtUtc > now.AddMinutes(60)).OrderBy(item => item.DueAtUtc));
        Add(FeedSection.Completed, source.Where(item => item.ExecutionStatus != ExecutionStatus.Pending).OrderByDescending(item => item.StatusChangedAtUtc));
        return groups;

        void Add(FeedSection section, IEnumerable<Assignment> items)
        {
            var list = items.ToArray();
            if (list.Length > 0)
                groups.Add(new FeedGroup(section, list));
        }
    }

    public static bool IsOverdue(Assignment assignment, DateTimeOffset now) =>
        assignment.ReviewState == ReviewState.Confirmed &&
        assignment.ExecutionStatus == ExecutionStatus.Pending &&
        assignment.DueAtUtc < now;

    private static bool IsPending(Assignment assignment) => assignment.ExecutionStatus == ExecutionStatus.Pending;
}
