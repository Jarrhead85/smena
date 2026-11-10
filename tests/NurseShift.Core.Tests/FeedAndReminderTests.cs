using NurseShift.Core.Enums;
using NurseShift.Core.Models;
using NurseShift.Core.Services;

namespace NurseShift.Core.Tests;

public sealed class FeedAndReminderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-6, FeedSection.Overdue)]
    [InlineData(-5, FeedSection.Now)]
    [InlineData(5, FeedSection.Now)]
    [InlineData(6, FeedSection.Soon)]
    [InlineData(60, FeedSection.Soon)]
    [InlineData(61, FeedSection.Later)]
    public void Group_UsesExactBoundaries(int offsetMinutes, FeedSection expected)
    {
        var groups = new FeedService().Group([AssignmentAt(Now.AddMinutes(offsetMinutes))], Now);
        Assert.Equal(expected, Assert.Single(groups).Section);
    }

    [Fact]
    public void OverduePending_IsNotAutomaticallySkipped()
    {
        var assignment = AssignmentAt(Now.AddHours(-2));
        var groups = new FeedService().Group([assignment], Now);
        Assert.Equal(FeedSection.Overdue, Assert.Single(groups).Section);
        Assert.Equal(ExecutionStatus.Pending, assignment.ExecutionStatus);
    }

    [Fact]
    public void ReminderPlanner_CreatesAdvanceAndDueOnlyInFuture()
    {
        var assignment = AssignmentAt(Now.AddMinutes(30));
        assignment.ReviewState = ReviewState.Confirmed;
        var reminders = new ReminderPlanner().CreateFuture(assignment, Now);
        Assert.Equal([ReminderKind.Advance, ReminderKind.Due], reminders.Select(item => item.Kind));
    }

    [Fact]
    public void ReminderPlanner_ZeroLead_CreatesOnlyDue()
    {
        var assignment = AssignmentAt(Now.AddMinutes(30));
        assignment.ReviewState = ReviewState.Confirmed;
        assignment.AdvanceLeadMinutes = 0;
        Assert.Equal(ReminderKind.Due, Assert.Single(new ReminderPlanner().CreateFuture(assignment, Now)).Kind);
    }

    [Fact]
    public void ReminderPlanner_DraftOrPast_CreatesNothing()
    {
        var draft = AssignmentAt(Now.AddMinutes(30));
        draft.ReviewState = ReviewState.Draft;
        Assert.Empty(new ReminderPlanner().CreateFuture(draft, Now));
        draft.ReviewState = ReviewState.Confirmed;
        draft.DueAtUtc = Now.AddMinutes(-1);
        Assert.Empty(new ReminderPlanner().CreateFuture(draft, Now));
    }

    [Fact]
    public void ReminderPlanner_RequestCodesAreStablePerKind()
    {
        var id = Guid.NewGuid();
        Assert.Equal(ReminderPlanner.StableRequestCode(id, ReminderKind.Due), ReminderPlanner.StableRequestCode(id, ReminderKind.Due));
        Assert.NotEqual(ReminderPlanner.StableRequestCode(id, ReminderKind.Advance), ReminderPlanner.StableRequestCode(id, ReminderKind.Due));
    }

    private static Assignment AssignmentAt(DateTimeOffset due) => new()
    {
        Id = Guid.NewGuid(), DueAtUtc = due, DueLocal = due.LocalDateTime, Procedure = "Тестовая процедура", PatientName = "Тестовый Пациент 001",
        ReviewState = ReviewState.Confirmed, ExecutionStatus = ExecutionStatus.Pending, AdvanceLeadMinutes = 10
    };
}
