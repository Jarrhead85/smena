using NurseShift.Core.Enums;
using NurseShift.Core.Models;

namespace NurseShift.Core.Interfaces;

public interface IShiftService
{
    Task<Shift?> GetActiveShiftAsync(CancellationToken cancellationToken);
    Task<ShiftDetails?> GetShiftDetailsAsync(Guid shiftId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Shift>> GetHistoryAsync(CancellationToken cancellationToken);
    Task<OperationResult> CreateShiftAsync(DateTime startLocal, DateTime endLocal, string timeZoneId, CancellationToken cancellationToken);
    Task<OperationResult> AddAssignmentAsync(Guid shiftId, AssignmentInput input, CancellationToken cancellationToken);
    Task<OperationResult> EditAssignmentAsync(Guid assignmentId, AssignmentInput input, long expectedRowVersion, bool confirmedReset, CancellationToken cancellationToken);
    Task<OperationResult> DeleteAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken);
    Task<OperationResult> SetAssignmentStatusAsync(Guid assignmentId, ExecutionStatus status, CancellationToken cancellationToken);
    Task<OperationResult> CompleteShiftAsync(Guid shiftId, bool confirmed, CancellationToken cancellationToken);
    Task<OperationResult> ReactivateShiftAsync(Guid shiftId, CancellationToken cancellationToken);
    Task<OperationResult> DeleteShiftAsync(Guid shiftId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Patient>> FindPatientSuggestionsAsync(string fullName, string? room, string? bed, CancellationToken cancellationToken);
}

public interface IImportService
{
    Task<ImportDetails?> GetIncompleteAsync(Guid shiftId, CancellationToken cancellationToken);
    Task<ImportDetails?> GetAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<ImportSession> CreateAsync(Guid shiftId, CancellationToken cancellationToken);
    Task<OperationResult> AddPageAsync(Guid sessionId, PickedImage image, CancellationToken cancellationToken);
    Task<OperationResult> ReplacePageAsync(Guid imageId, PickedImage image, CancellationToken cancellationToken);
    Task<OperationResult> RotatePageAsync(Guid imageId, CancellationToken cancellationToken);
    Task<OperationResult> MovePageAsync(Guid imageId, int offset, CancellationToken cancellationToken);
    Task<OperationResult> DeletePageAsync(Guid imageId, CancellationToken cancellationToken);
    Task<OperationResult> RecognizeAsync(Guid sessionId, bool consentConfirmed, IProgress<ImportProgress>? progress, CancellationToken cancellationToken);
    Task<OperationResult> AddDraftAsync(Guid sessionId, Guid? sourceImageId, string? sourceExcerpt, CancellationToken cancellationToken);
    Task<OperationResult> UpdateDraftAsync(Assignment draft, CancellationToken cancellationToken);
    Task<OperationResult> DeleteDraftAsync(Guid assignmentId, CancellationToken cancellationToken);
    Task<OperationResult> ExcludeUnparsedLineAsync(Guid imageId, int index, CancellationToken cancellationToken);
    Task<OperationResult> ExcludePageAsync(Guid imageId, CancellationToken cancellationToken);
    Task<OperationResult> ConfirmAsync(Guid sessionId, long expectedRowVersion, bool confirmedEmpty, CancellationToken cancellationToken);
    Task<OperationResult> CancelAsync(Guid sessionId, CancellationToken cancellationToken);
}

public interface IReminderScheduler
{
    Task<OperationResult> SynchronizeAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken);
    Task<OperationResult> SynchronizeAllFutureAsync(CancellationToken cancellationToken);
    Task CancelForAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken);
    Task<ReminderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);
    Task RequestNotificationPermissionAsync(CancellationToken cancellationToken);
    Task OpenNotificationSettingsAsync(CancellationToken cancellationToken);
    Task OpenExactAlarmSettingsAsync(CancellationToken cancellationToken);
    Task<OperationResult> ShowTestNotificationAsync(CancellationToken cancellationToken);
}

public interface IAppLockService
{
    bool IsDeviceSecure { get; }
    Task<bool> AuthenticateAsync(CancellationToken cancellationToken);
}

public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
    Task RemoveAsync(string key, CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<AppSettings> GetAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public interface IRecognitionService
{
    Task<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken);
    Task<OperationResult> TestConnectionAsync(CancellationToken cancellationToken);
}

public interface IImagePicker
{
    Task<PickedImage?> CaptureAsync(CancellationToken cancellationToken);
    Task<PickedImage?> PickAsync(CancellationToken cancellationToken);
}

public interface IImageProcessor
{
    Task RotateAsync(string privatePath, CancellationToken cancellationToken);
}

public interface IPhotoRetentionStore
{
    Task FinalizeAsync(SourceImage image, bool retain, CancellationToken cancellationToken);
}

public interface IPhotoAccess
{
    Task<byte[]> ReadAsync(Guid imageId, CancellationToken cancellationToken);
}

public interface IDataCleanupService
{
    Task CleanupHistoryAsync(CancellationToken cancellationToken);
    Task DeleteAllAsync(CancellationToken cancellationToken);
}
