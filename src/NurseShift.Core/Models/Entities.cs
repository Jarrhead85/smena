using NurseShift.Core.Enums;

namespace NurseShift.Core.Models;

public sealed class Shift
{
    public Guid Id { get; set; }
    public DateTime StartLocal { get; set; }
    public DateTime EndLocal { get; set; }
    public DateTimeOffset StartAtUtc { get; set; }
    public DateTimeOffset EndAtUtc { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public ShiftStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? HistoryExpiresAtUtc { get; set; }
    public long RowVersion { get; set; }
    public List<Assignment> Assignments { get; set; } = [];
}

public sealed class Patient
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastUsedAtUtc { get; set; }
}

public sealed class Assignment
{
    public Guid Id { get; set; }
    public Guid ShiftId { get; set; }
    public Guid? PatientId { get; set; }
    public string? PatientName { get; set; }
    public string? Room { get; set; }
    public string? Bed { get; set; }
    public DateTime? DueLocal { get; set; }
    public DateTimeOffset? DueAtUtc { get; set; }
    public string? Procedure { get; set; }
    public string? Dosage { get; set; }
    public string? Comment { get; set; }
    public ReviewState ReviewState { get; set; }
    public ExecutionStatus ExecutionStatus { get; set; } = ExecutionStatus.Pending;
    public bool NeedsReview { get; set; }
    public string? ReviewReason { get; set; }
    public Guid? SourceImageId { get; set; }
    public string? SourceExcerpt { get; set; }
    public DateTimeOffset? StatusChangedAtUtc { get; set; }
    public int AdvanceLeadMinutes { get; set; } = 10;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long RowVersion { get; set; }
    public Shift? Shift { get; set; }
    public List<Reminder> Reminders { get; set; } = [];
}

public sealed class ImportSession
{
    public Guid Id { get; set; }
    public Guid ShiftId { get; set; }
    public ImportStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long RowVersion { get; set; }
    public Shift? Shift { get; set; }
    public List<SourceImage> Pages { get; set; } = [];
}

public sealed class SourceImage
{
    public Guid Id { get; set; }
    public Guid ImportSessionId { get; set; }
    public int SortOrder { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public ImageProcessingState ProcessingState { get; set; }
    public SafeErrorCode? SafeErrorCode { get; set; }
    public string? RemoteFileId { get; set; }
    public bool RemoteDeletePending { get; set; }
    public bool RetainLocal { get; set; }
    public string? UnparsedLinesJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public ImportSession? ImportSession { get; set; }
    public List<Assignment> Assignments { get; set; } = [];
}

public sealed class Reminder
{
    public Guid Id { get; set; }
    public Guid AssignmentId { get; set; }
    public ReminderKind Kind { get; set; }
    public DateTimeOffset ScheduledAtUtc { get; set; }
    public int AndroidRequestCode { get; set; }
    public ScheduleState ScheduleState { get; set; }
    public string? SafeErrorCode { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Assignment? Assignment { get; set; }
}
