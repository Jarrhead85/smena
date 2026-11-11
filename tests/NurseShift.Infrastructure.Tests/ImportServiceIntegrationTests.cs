using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;
using NurseShift.Core.Models;
using NurseShift.Infrastructure.Persistence;

namespace NurseShift.Infrastructure.Tests;

public sealed class ImportServiceIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nurseshift-import-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecretStore _secrets = new();
    private readonly MemorySettingsStore _settings = new();
    private readonly TestClock _clock = new();
    private readonly FakeReminderScheduler _scheduler = new();
    private readonly FakeRecognitionService _recognition = new();
    private readonly NurseShiftDbContextFactory _factory;
    private readonly ShiftService _shifts;
    private readonly ImportService _imports;

    public ImportServiceIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
        _secrets.SetAsync("gigachat.authorization_key", "synthetic-test-key", CancellationToken.None).GetAwaiter().GetResult();
        _factory = new NurseShiftDbContextFactory(Path.Combine(_directory, "test.db"), _secrets);
        _shifts = new ShiftService(_factory, _clock, _scheduler, _settings, new FakePhotoStore());
        _imports = new ImportService(_factory, _clock, _settings, _secrets, _recognition, _scheduler, new FakeImageProcessor(), new FakePhotoStore(), _directory);
        using var context = _factory.CreateAsync(CancellationToken.None).GetAwaiter().GetResult();
        context.Database.Migrate();
    }

    [Fact]
    public async Task Confirm_OnlyAfterReview_IsAtomicAndIdempotent()
    {
        var shiftId = await CreateShiftAsync();
        var session = await _imports.CreateAsync(shiftId, CancellationToken.None);
        await AddPageAsync(session.Id);
        Assert.Empty(_scheduler.Synchronized);
        var recognized = await _imports.RecognizeAsync(session.Id, true, null, CancellationToken.None);
        Assert.True(recognized.IsSuccess);
        var review = await _imports.GetAsync(session.Id, CancellationToken.None);
        Assert.Equal(ReviewState.Draft, Assert.Single(review!.Assignments).ReviewState);
        Assert.Empty(_scheduler.Synchronized);
        var confirmed = await _imports.ConfirmAsync(session.Id, review.Session.RowVersion, false, CancellationToken.None);
        Assert.True(confirmed.IsSuccess);
        var details = await _shifts.GetShiftDetailsAsync(shiftId, CancellationToken.None);
        Assert.Equal(ReviewState.Confirmed, Assert.Single(details!.Assignments).ReviewState);
        Assert.Single(_scheduler.Synchronized);
        var repeated = await _imports.ConfirmAsync(session.Id, review.Session.RowVersion, false, CancellationToken.None);
        Assert.True(repeated.IsSuccess);
        Assert.Single((await _shifts.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments);
        Assert.Single(_scheduler.Synchronized);
    }

    [Fact]
    public async Task MissingCriticalField_BlocksConfirmAndDoesNotSchedule()
    {
        _recognition.Result = _recognition.Result with { Assignments = [_recognition.Result.Assignments[0] with { PatientName = null, NeedsReview = true }] };
        var shiftId = await CreateShiftAsync();
        var session = await _imports.CreateAsync(shiftId, CancellationToken.None);
        await AddPageAsync(session.Id);
        await _imports.RecognizeAsync(session.Id, true, null, CancellationToken.None);
        var review = await _imports.GetAsync(session.Id, CancellationToken.None);
        var result = await _imports.ConfirmAsync(session.Id, review!.Session.RowVersion, false, CancellationToken.None);
        Assert.Equal(SafeErrorCode.Validation, result.ErrorCode);
        Assert.Empty((await _shifts.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments);
        Assert.Empty(_scheduler.Synchronized);
    }

    [Fact]
    public async Task UnparsedLine_MustBeExplicitlyExcluded()
    {
        _recognition.Result = _recognition.Result with { UnparsedLines = [new UnparsedLine("Нечитаемая синтетическая строка", "Тест")] };
        var shiftId = await CreateShiftAsync();
        var session = await _imports.CreateAsync(shiftId, CancellationToken.None);
        var imageId = await AddPageAsync(session.Id);
        await _imports.RecognizeAsync(session.Id, true, null, CancellationToken.None);
        var review = await _imports.GetAsync(session.Id, CancellationToken.None);
        Assert.Equal(SafeErrorCode.UnparsedLinesRemain, (await _imports.ConfirmAsync(session.Id, review!.Session.RowVersion, false, CancellationToken.None)).ErrorCode);
        await _imports.ExcludeUnparsedLineAsync(imageId, 0, CancellationToken.None);
        review = await _imports.GetAsync(session.Id, CancellationToken.None);
        Assert.True((await _imports.ConfirmAsync(session.Id, review!.Session.RowVersion, false, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task ManualDraft_IsIncludedInConfirmTransaction()
    {
        _recognition.Result = _recognition.Result with { Assignments = [] };
        var shiftId = await CreateShiftAsync();
        var session = await _imports.CreateAsync(shiftId, CancellationToken.None);
        await AddPageAsync(session.Id);
        await _imports.RecognizeAsync(session.Id, true, null, CancellationToken.None);
        var added = await _imports.AddDraftAsync(session.Id, null, null, CancellationToken.None);
        Assert.True(added.IsSuccess);
        var review = await _imports.GetAsync(session.Id, CancellationToken.None);
        var draft = Assert.Single(review!.Assignments);
        Assert.Equal(draft.Id, added.EntityId);
        draft.PatientName = "Тестовый Пациент 002";
        draft.Procedure = "Тестовая ручная процедура";
        draft.DueLocal = new DateTime(2026, 11, 10, 23, 30, 0);
        Assert.True((await _imports.UpdateDraftAsync(draft, CancellationToken.None)).IsSuccess);
        review = await _imports.GetAsync(session.Id, CancellationToken.None);

        var result = await _imports.ConfirmAsync(session.Id, review!.Session.RowVersion, false, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var assignment = Assert.Single((await _shifts.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments);
        Assert.Equal(ReviewState.Confirmed, assignment.ReviewState);
        Assert.Equal(assignment.Id, Assert.Single(_scheduler.Synchronized));
    }

    [Fact]
    public async Task ReplacePage_ReplacesPrivateBytesAndResetsState()
    {
        var shiftId = await CreateShiftAsync();
        var session = await _imports.CreateAsync(shiftId, CancellationToken.None);
        var imageId = await AddPageAsync(session.Id);
        await using var replacement = new MemoryStream([5, 6, 7, 8, 9]);

        var result = await _imports.ReplacePageAsync(imageId, new PickedImage(replacement, "replacement.jpg", 5), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var page = Assert.Single((await _imports.GetAsync(session.Id, CancellationToken.None))!.Pages);
        Assert.Equal(ImageProcessingState.Pending, page.ProcessingState);
        Assert.Equal([5, 6, 7, 8, 9], await File.ReadAllBytesAsync(page.LocalPath));
    }

    [Fact]
    public async Task GetIncomplete_ReturnsLatestSessionWithSqliteDates()
    {
        var shiftId = await CreateShiftAsync();
        var older = await _imports.CreateAsync(shiftId, CancellationToken.None);
        var newer = new ImportSession
        {
            Id = Guid.NewGuid(), ShiftId = shiftId, Status = ImportStatus.Preparing,
            CreatedAtUtc = _clock.UtcNow.AddMinutes(1), UpdatedAtUtc = _clock.UtcNow.AddMinutes(1), RowVersion = 1
        };
        await using (var context = await _factory.CreateAsync(CancellationToken.None))
        {
            context.ImportSessions.Add(newer);
            await context.SaveChangesAsync(CancellationToken.None);
        }

        var result = await _imports.GetIncompleteAsync(shiftId, CancellationToken.None);

        Assert.Equal(newer.Id, result!.Session.Id);
        Assert.NotEqual(older.Id, result.Session.Id);
    }

    [Fact]
    public async Task Cancel_RemovesManualDraftAndDoesNotChangePlan()
    {
        _recognition.Result = _recognition.Result with { Assignments = [] };
        var shiftId = await CreateShiftAsync();
        var session = await _imports.CreateAsync(shiftId, CancellationToken.None);
        await AddPageAsync(session.Id);
        await _imports.RecognizeAsync(session.Id, true, null, CancellationToken.None);
        await _imports.AddDraftAsync(session.Id, null, null, CancellationToken.None);

        var result = await _imports.CancelAsync(session.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty((await _imports.GetAsync(session.Id, CancellationToken.None))!.Assignments);
        Assert.Empty((await _shifts.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments);
        Assert.Empty(_scheduler.Synchronized);
    }

    [Theory]
    [InlineData(ImageProcessingState.Pending)]
    [InlineData(ImageProcessingState.Uploading)]
    [InlineData(ImageProcessingState.Recognizing)]
    [InlineData(ImageProcessingState.Failed)]
    public async Task IncompleteOrFailedPage_BlocksConfirmAndDoesNotSchedule(ImageProcessingState state)
    {
        var shiftId = await CreateShiftAsync();
        var session = await _imports.CreateAsync(shiftId, CancellationToken.None);
        var imageId = await AddPageAsync(session.Id);
        await _imports.RecognizeAsync(session.Id, true, null, CancellationToken.None);
        var review = await _imports.GetAsync(session.Id, CancellationToken.None);
        await using (var context = await _factory.CreateAsync(CancellationToken.None))
        {
            var page = await context.SourceImages.SingleAsync(item => item.Id == imageId);
            page.ProcessingState = state;
            await context.SaveChangesAsync(CancellationToken.None);
        }

        var result = await _imports.ConfirmAsync(session.Id, review!.Session.RowVersion, false, CancellationToken.None);

        Assert.Equal(SafeErrorCode.PagesNotReady, result.ErrorCode);
        Assert.Empty((await _shifts.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments);
        Assert.Empty(_scheduler.Synchronized);
    }

    [Fact]
    public async Task ExcludePage_RemovesItsDraftsAndRequiresExplicitEmptyConfirmation()
    {
        _recognition.Result = _recognition.Result with { UnparsedLines = [new UnparsedLine("Синтетическая нечитаемая строка", "Тест")] };
        var shiftId = await CreateShiftAsync();
        var session = await _imports.CreateAsync(shiftId, CancellationToken.None);
        var imageId = await AddPageAsync(session.Id);
        await _imports.RecognizeAsync(session.Id, true, null, CancellationToken.None);
        var review = await _imports.GetAsync(session.Id, CancellationToken.None);

        var excluded = await _imports.ExcludePageAsync(imageId, CancellationToken.None);

        Assert.True(excluded.IsSuccess);
        review = await _imports.GetAsync(session.Id, CancellationToken.None);
        Assert.Equal(ImageProcessingState.Excluded, Assert.Single(review!.Pages).ProcessingState);
        Assert.Empty(review.Assignments);
        Assert.Equal(SafeErrorCode.Validation, (await _imports.ExcludeUnparsedLineAsync(imageId, 0, CancellationToken.None)).ErrorCode);
        Assert.Equal(SafeErrorCode.ConfirmationRequired,
            (await _imports.ConfirmAsync(session.Id, review.Session.RowVersion, false, CancellationToken.None)).ErrorCode);
        Assert.Empty(_scheduler.Synchronized);
        Assert.True((await _imports.ConfirmAsync(session.Id, review.Session.RowVersion, true, CancellationToken.None)).IsSuccess);
        Assert.Empty((await _shifts.GetShiftDetailsAsync(shiftId, CancellationToken.None))!.Assignments);
        Assert.Empty(_scheduler.Synchronized);
    }

    [Fact]
    public async Task EmptyRecognition_RequiresSeparateExplicitConfirmation()
    {
        _recognition.Result = _recognition.Result with { Assignments = [] };
        var shiftId = await CreateShiftAsync();
        var session = await _imports.CreateAsync(shiftId, CancellationToken.None);
        await AddPageAsync(session.Id);
        await _imports.RecognizeAsync(session.Id, true, null, CancellationToken.None);
        var review = await _imports.GetAsync(session.Id, CancellationToken.None);

        var blocked = await _imports.ConfirmAsync(session.Id, review!.Session.RowVersion, false, CancellationToken.None);

        Assert.Equal(SafeErrorCode.ConfirmationRequired, blocked.ErrorCode);
        Assert.Equal(ImportStatus.Review, (await _imports.GetAsync(session.Id, CancellationToken.None))!.Session.Status);
        Assert.Empty(_scheduler.Synchronized);
        Assert.True((await _imports.ConfirmAsync(session.Id, review.Session.RowVersion, true, CancellationToken.None)).IsSuccess);
        Assert.Empty(_scheduler.Synchronized);
    }

    private async Task<Guid> CreateShiftAsync()
    {
        await _shifts.CreateShiftAsync(new DateTime(2026, 11, 10, 22, 0, 0), new DateTime(2026, 11, 11, 6, 0, 0), "Europe/Moscow", CancellationToken.None);
        return (await _shifts.GetActiveShiftAsync(CancellationToken.None))!.Id;
    }

    private async Task<Guid> AddPageAsync(Guid sessionId)
    {
        await using var stream = new MemoryStream([1, 2, 3, 4]);
        var result = await _imports.AddPageAsync(sessionId, new PickedImage(stream, "synthetic.jpg", 4), CancellationToken.None);
        Assert.True(result.IsSuccess);
        return Assert.Single((await _imports.GetAsync(sessionId, CancellationToken.None))!.Pages).Id;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
