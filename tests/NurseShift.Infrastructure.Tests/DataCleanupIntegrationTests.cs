using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Models;
using NurseShift.Infrastructure.Persistence;

namespace NurseShift.Infrastructure.Tests;

public sealed class DataCleanupIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nurseshift-cleanup-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecretStore _secrets = new();
    private readonly MemorySettingsStore _settings = new();
    private readonly TestClock _clock = new();
    private readonly NurseShiftDbContextFactory _factory;
    private readonly ShiftService _shifts;
    private readonly DataCleanupService _cleanup;

    public DataCleanupIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new NurseShiftDbContextFactory(Path.Combine(_directory, "test.db"), _secrets);
        _shifts = new ShiftService(_factory, _clock, new FakeReminderScheduler(), _settings, new FakePhotoStore());
        _cleanup = new DataCleanupService(_factory, _shifts, _clock, _secrets, _settings, _directory);
        using var context = _factory.CreateAsync(CancellationToken.None).GetAwaiter().GetResult();
        context.Database.Migrate();
    }

    [Fact]
    public async Task ShorterRetention_AppliesToExistingHistoryAtNextCleanup()
    {
        await CreateCompletedShiftAsync();
        _settings.Value = _settings.Value with { HistoryRetentionDays = 7 };
        _clock.UtcNow = _clock.UtcNow.AddDays(8);

        await _cleanup.CleanupHistoryAsync(CancellationToken.None);

        Assert.Empty(await _shifts.GetHistoryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ManualRetention_AppliesToExistingHistoryAtNextCleanup()
    {
        await CreateCompletedShiftAsync();
        _settings.Value = _settings.Value with { HistoryRetentionDays = null };
        _clock.UtcNow = _clock.UtcNow.AddDays(100);

        await _cleanup.CleanupHistoryAsync(CancellationToken.None);

        var shift = Assert.Single(await _shifts.GetHistoryAsync(CancellationToken.None));
        Assert.Null(shift.HistoryExpiresAtUtc);
    }

    private async Task CreateCompletedShiftAsync()
    {
        var result = await _shifts.CreateShiftAsync(new DateTime(2026, 11, 10, 22, 0, 0), new DateTime(2026, 11, 11, 6, 0, 0), "Europe/Moscow", CancellationToken.None);
        Assert.True(result.IsSuccess);
        var shift = await _shifts.GetActiveShiftAsync(CancellationToken.None);
        Assert.True((await _shifts.CompleteShiftAsync(shift!.Id, true, CancellationToken.None)).IsSuccess);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
