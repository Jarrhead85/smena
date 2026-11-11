using NurseShift.Core.Enums;

namespace NurseShift.Core.Models;

public sealed record OperationResult(SafeErrorCode ErrorCode = SafeErrorCode.None, Guid? EntityId = null)
{
    public bool IsSuccess => ErrorCode == SafeErrorCode.None;
    public static OperationResult Success() => new();
    public static OperationResult Success(Guid entityId) => new(SafeErrorCode.None, entityId);
}

public sealed record AssignmentInput(
    string? PatientName,
    string? Procedure,
    DateTime? DueLocal,
    string? Room,
    string? Bed,
    string? Dosage,
    string? Comment,
    int AdvanceLeadMinutes,
    Guid? PatientId = null);

public sealed record ShiftDetails(Shift Shift, IReadOnlyList<Assignment> Assignments, bool ReminderSyncFailed);

public sealed record ImportDetails(
    ImportSession Session,
    IReadOnlyList<SourceImage> Pages,
    IReadOnlyList<Assignment> Assignments);

public sealed record ImportProgress(int CurrentPage, int TotalPages, ImageProcessingState State);

public sealed record UnparsedLine(string? Text, string Reason, bool Excluded = false);

public sealed record RecognizedAssignment(
    string? PatientName,
    string? Room,
    string? Bed,
    string? Time,
    string? Procedure,
    string? Dosage,
    string? Comment,
    string? SourceExcerpt,
    bool NeedsReview,
    string? ReviewReason);

public sealed record RecognitionResult(
    IReadOnlyList<RecognizedAssignment> Assignments,
    IReadOnlyList<UnparsedLine> UnparsedLines,
    bool RemoteDeletePending);

public sealed record RecognitionRequest(
    Guid SourceImageId,
    Func<CancellationToken, ValueTask<Stream>> OpenImage,
    Shift Shift,
    string Model,
    string Scope);

public sealed record PickedImage(Stream Content, string FileName, long? Length = null);

public sealed record AppSettings(
    string Model,
    string Scope,
    int AdvanceLeadMinutes,
    ThemePreference Theme,
    int? HistoryRetentionDays,
    bool RetainPhotos,
    bool PrivateLockScreenNotifications,
    bool LockWithDeviceCredential,
    bool OnboardingCompleted,
    string? ConsentKeyHash)
{
    public static AppSettings Default => new(
        string.Empty,
        "GIGACHAT_API_PERS",
        10,
        ThemePreference.System,
        30,
        false,
        true,
        false,
        false,
        null);
}

public sealed record ReminderAvailability(bool NotificationsAllowed, bool NotificationChannelEnabled, bool ExactAlarmsAllowed)
{
    public bool IsAvailable => NotificationsAllowed && NotificationChannelEnabled && ExactAlarmsAllowed;
}
