using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;

namespace NurseShift.Infrastructure.Tests;

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 10, 19, 0, 0, TimeSpan.Zero);
}

internal sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
    public Task SetAsync(string key, string value, CancellationToken cancellationToken) { _values[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key, CancellationToken cancellationToken) { _values.Remove(key); return Task.CompletedTask; }
}

internal sealed class MemorySettingsStore : ISettingsStore
{
    public AppSettings Value { get; set; } = AppSettings.Default with { Model = "test-vision-model" };
    public Task<AppSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) { Value = settings; return Task.CompletedTask; }
}

internal sealed class FakeReminderScheduler : IReminderScheduler
{
    public List<Guid> Synchronized { get; } = [];
    public List<Guid> Cancelled { get; } = [];
    public Task<OperationResult> SynchronizeAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken) { Synchronized.Add(assignmentId); return Task.FromResult(OperationResult.Success()); }
    public Task<OperationResult> SynchronizeAllFutureAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());
    public Task CancelForAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken) { Cancelled.Add(assignmentId); return Task.CompletedTask; }
    public Task<ReminderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => Task.FromResult(new ReminderAvailability(true, true));
    public Task RequestNotificationPermissionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OpenExactAlarmSettingsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakePhotoStore : IPhotoRetentionStore
{
    public Task FinalizeAsync(SourceImage image, bool retain, CancellationToken cancellationToken)
    {
        if (!retain && File.Exists(image.LocalPath))
            File.Delete(image.LocalPath);
        return Task.CompletedTask;
    }
}

internal sealed class FakeImageProcessor : IImageProcessor
{
    public Task RotateAsync(string privatePath, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakeRecognitionService : IRecognitionService
{
    public RecognitionResult Result { get; set; } = new([
        new RecognizedAssignment("Тестовый Пациент 001", "T1", "1", "23:30", "Тестовая процедура", null, null, "Синтетическая строка", false, null)
    ], [], false);
    public Task<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken cancellationToken) => Task.FromResult(Result);
    public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["test-vision-model"]);
    public Task<OperationResult> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());
}
