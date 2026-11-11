namespace NurseShift.Core.Enums;

public enum ShiftStatus { Active, Completed }
public enum ReviewState { Draft, Confirmed }
public enum ExecutionStatus { Pending, Done, Skipped }
public enum ImportStatus { Preparing, Processing, Review, Confirmed, Cancelled }
public enum ImageProcessingState { Pending, Uploading, Recognizing, Ready, Failed, Cancelled, Excluded }
public enum ReminderKind { Advance, Due }
public enum ScheduleState { Planned, Scheduled, Failed, Cancelled, Delivered, Dispatching, Posted, RecoveryPending }
public enum ThemePreference { System, Light, Dark }

public enum SafeErrorCode
{
    None,
    Validation,
    ConfirmationRequired,
    ActiveShiftExists,
    ShiftNotActive,
    ShiftNotFound,
    AssignmentNotFound,
    ImportNotFound,
    InvalidTime,
    TimeOutsideShift,
    AmbiguousTime,
    UnparsedLinesRemain,
    ConcurrentUpdate,
    NetworkUnavailable,
    Timeout,
    TlsValidationFailed,
    Unauthorized,
    PermissionDenied,
    RateLimited,
    ServiceUnavailable,
    ResponseInvalid,
    ImageTooLarge,
    ImageUnreadable,
    StorageUnavailable,
    SchedulingDenied,
    SchedulingFailed,
    Unknown,
    PagesNotReady
}
