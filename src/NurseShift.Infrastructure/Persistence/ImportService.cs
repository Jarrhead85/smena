using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;
using NurseShift.Core.Services;
using NurseShift.Infrastructure.GigaChat;

namespace NurseShift.Infrastructure.Persistence;

public sealed class ImportService(
    INurseShiftDbContextFactory contextFactory,
    IClock clock,
    ISettingsStore settingsStore,
    ISecretStore secretStore,
    IRecognitionService recognitionService,
    IReminderScheduler reminderScheduler,
    IImageProcessor imageProcessor,
    IPhotoRetentionStore photoRetentionStore,
    string storageRoot) : IImportService
{
    private const int HardImageLimit = 15 * 1024 * 1024;
    private readonly ShiftTimeService _timeService = new();
    private readonly ReminderPlanner _reminderPlanner = new();
    private readonly ImportValidator _validator = new();

    public async Task<ImportDetails?> GetIncompleteAsync(Guid shiftId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var sessions = await context.ImportSessions.AsNoTracking().Where(item => item.ShiftId == shiftId &&
            (item.Status == ImportStatus.Preparing || item.Status == ImportStatus.Processing || item.Status == ImportStatus.Review))
            .ToListAsync(cancellationToken);
        var session = sessions.OrderByDescending(item => item.UpdatedAtUtc).FirstOrDefault();
        return session is null ? null : await LoadDetailsAsync(context, session.Id, cancellationToken);
    }

    public async Task<ImportDetails?> GetAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        return await LoadDetailsAsync(context, sessionId, cancellationToken);
    }

    public async Task<ImportSession> CreateAsync(Guid shiftId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var existing = await context.ImportSessions.FirstOrDefaultAsync(item => item.ShiftId == shiftId &&
            (item.Status == ImportStatus.Preparing || item.Status == ImportStatus.Processing || item.Status == ImportStatus.Review), cancellationToken);
        if (existing is not null)
            return existing;
        var shiftExists = await context.Shifts.AnyAsync(item => item.Id == shiftId && item.Status == ShiftStatus.Active, cancellationToken);
        if (!shiftExists)
            throw new InvalidOperationException(SafeErrorCode.ShiftNotActive.ToString());
        var now = clock.UtcNow;
        var session = new ImportSession { Id = Guid.NewGuid(), ShiftId = shiftId, Status = ImportStatus.Preparing, CreatedAtUtc = now, UpdatedAtUtc = now, RowVersion = 1 };
        context.ImportSessions.Add(session);
        await context.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<OperationResult> AddPageAsync(Guid sessionId, PickedImage image, CancellationToken cancellationToken)
    {
        if (image.Length > HardImageLimit)
            return new OperationResult(SafeErrorCode.ImageTooLarge);
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var session = await context.ImportSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null || session.Status is ImportStatus.Confirmed or ImportStatus.Cancelled)
            return new OperationResult(SafeErrorCode.ImportNotFound);
        Directory.CreateDirectory(storageRoot);
        var id = Guid.NewGuid();
        var path = Path.Combine(storageRoot, $"{id:N}.jpg");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                var total = 0L;
                while (true)
                {
                    var read = await image.Content.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > HardImageLimit)
                        return new OperationResult(SafeErrorCode.ImageTooLarge);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            var settings = await settingsStore.GetAsync(cancellationToken);
            var nextOrder = await context.SourceImages.Where(item => item.ImportSessionId == sessionId).MaxAsync(item => (int?)item.SortOrder, cancellationToken) ?? -1;
            context.SourceImages.Add(new SourceImage
            {
                Id = id,
                ImportSessionId = sessionId,
                SortOrder = nextOrder + 1,
                LocalPath = path,
                ProcessingState = ImageProcessingState.Pending,
                RetainLocal = settings.RetainPhotos,
                CreatedAtUtc = clock.UtcNow
            });
            session.Status = ImportStatus.Preparing;
            session.UpdatedAtUtc = clock.UtcNow;
            session.RowVersion++;
            await context.SaveChangesAsync(cancellationToken);
            return OperationResult.Success();
        }
        finally
        {
            await image.Content.DisposeAsync();
            if (File.Exists(path) && !await context.SourceImages.AsNoTracking().AnyAsync(item => item.Id == id, CancellationToken.None))
                File.Delete(path);
        }
    }

    public async Task<OperationResult> ReplacePageAsync(Guid imageId, PickedImage image, CancellationToken cancellationToken)
    {
        if (image.Length > HardImageLimit)
        {
            await image.Content.DisposeAsync();
            return new OperationResult(SafeErrorCode.ImageTooLarge);
        }
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var page = await context.SourceImages.Include(item => item.ImportSession)
            .SingleOrDefaultAsync(item => item.Id == imageId, cancellationToken);
        if (page?.ImportSession is null || page.ImportSession.Status is ImportStatus.Confirmed or ImportStatus.Cancelled ||
            page.ProcessingState is ImageProcessingState.Ready or ImageProcessingState.Uploading or ImageProcessingState.Recognizing)
        {
            await image.Content.DisposeAsync();
            return new OperationResult(SafeErrorCode.ImportNotFound);
        }
        EnsurePrivatePath(page.LocalPath);
        var replacementPath = page.LocalPath + ".replacement";
        try
        {
            await using (var output = new FileStream(replacementPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                var total = 0L;
                while (true)
                {
                    var read = await image.Content.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > HardImageLimit)
                        return new OperationResult(SafeErrorCode.ImageTooLarge);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            File.Move(replacementPath, page.LocalPath, true);
            page.ProcessingState = ImageProcessingState.Pending;
            page.SafeErrorCode = null;
            page.UnparsedLinesJson = null;
            await context.SaveChangesAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (IOException)
        {
            return new OperationResult(SafeErrorCode.StorageUnavailable);
        }
        finally
        {
            await image.Content.DisposeAsync();
            if (File.Exists(replacementPath))
                File.Delete(replacementPath);
        }
    }

    public async Task<OperationResult> RotatePageAsync(Guid imageId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var page = await context.SourceImages.SingleOrDefaultAsync(item => item.Id == imageId, cancellationToken);
        if (page is null)
            return new OperationResult(SafeErrorCode.ImportNotFound);
        EnsurePrivatePath(page.LocalPath);
        await imageProcessor.RotateAsync(page.LocalPath, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> MovePageAsync(Guid imageId, int offset, CancellationToken cancellationToken)
    {
        if (offset == 0)
            return OperationResult.Success();
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var page = await context.SourceImages.SingleOrDefaultAsync(item => item.Id == imageId, cancellationToken);
        if (page is null)
            return new OperationResult(SafeErrorCode.ImportNotFound);
        var pages = await context.SourceImages.Where(item => item.ImportSessionId == page.ImportSessionId)
            .OrderBy(item => item.SortOrder).ToListAsync(cancellationToken);
        var index = pages.FindIndex(item => item.Id == imageId);
        var target = Math.Clamp(index + offset, 0, pages.Count - 1);
        if (index == target)
            return OperationResult.Success();
        pages.RemoveAt(index);
        pages.Insert(target, page);
        for (var i = 0; i < pages.Count; i++)
            pages[i].SortOrder = i;
        await context.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeletePageAsync(Guid imageId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var page = await context.SourceImages.SingleOrDefaultAsync(item => item.Id == imageId, cancellationToken);
        if (page is null)
            return new OperationResult(SafeErrorCode.ImportNotFound);
        if (page.ImportSessionId != Guid.Empty && await context.ImportSessions.AnyAsync(item => item.Id == page.ImportSessionId && item.Status == ImportStatus.Confirmed, cancellationToken))
            return new OperationResult(SafeErrorCode.ImportNotFound);
        context.SourceImages.Remove(page);
        await context.SaveChangesAsync(cancellationToken);
        await photoRetentionStore.FinalizeAsync(page, false, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RecognizeAsync(Guid sessionId, bool consentConfirmed, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        var key = await secretStore.GetAsync("gigachat.authorization_key", cancellationToken);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(settings.Model))
            return new OperationResult(SafeErrorCode.Unauthorized);
        var consentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        if (settings.ConsentKeyHash != consentHash && !consentConfirmed)
            return new OperationResult(SafeErrorCode.ConfirmationRequired);
        if (settings.ConsentKeyHash != consentHash)
        {
            settings = settings with { ConsentKeyHash = consentHash };
            await settingsStore.SaveAsync(settings, cancellationToken);
        }

        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var session = await context.ImportSessions.Include(item => item.Shift).SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session?.Shift is null || session.Shift.Status != ShiftStatus.Active)
            return new OperationResult(SafeErrorCode.ShiftNotActive);
        var pages = await context.SourceImages.Where(item => item.ImportSessionId == sessionId &&
            (item.ProcessingState == ImageProcessingState.Pending || item.ProcessingState == ImageProcessingState.Failed))
            .OrderBy(item => item.SortOrder).ToListAsync(cancellationToken);
        if (pages.Count == 0 && session.Status != ImportStatus.Review)
            return new OperationResult(SafeErrorCode.Validation);
        session.Status = ImportStatus.Processing;
        session.UpdatedAtUtc = clock.UtcNow;
        session.RowVersion++;
        await context.SaveChangesAsync(cancellationToken);

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var page = pages[pageIndex];
            if (cancellationToken.IsCancellationRequested)
            {
                session.Status = ImportStatus.Review;
                await context.SaveChangesAsync(CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
            }
            try
            {
                page.ProcessingState = ImageProcessingState.Uploading;
                page.SafeErrorCode = null;
                progress?.Report(new ImportProgress(pageIndex + 1, pages.Count, ImageProcessingState.Uploading));
                await context.SaveChangesAsync(cancellationToken);
                EnsurePrivatePath(page.LocalPath);
                var request = new RecognitionRequest(page.Id,
                    _ => new ValueTask<Stream>(new FileStream(page.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true)),
                    session.Shift, settings.Model, settings.Scope);
                page.ProcessingState = ImageProcessingState.Recognizing;
                await context.SaveChangesAsync(cancellationToken);
                var result = await recognitionService.RecognizeAsync(request, cancellationToken);
                foreach (var recognized in result.Assignments)
                {
                    var timeResult = _timeService.ResolveRecognizedTime(session.Shift, recognized.Time, out var dueLocal, out var dueAtUtc);
                    var now = clock.UtcNow;
                    context.Assignments.Add(new Assignment
                    {
                        Id = Guid.NewGuid(),
                        ShiftId = session.ShiftId,
                        SourceImageId = page.Id,
                        PatientName = recognized.PatientName?.Trim(),
                        Room = recognized.Room?.Trim(),
                        Bed = recognized.Bed?.Trim(),
                        Procedure = recognized.Procedure?.Trim(),
                        Dosage = recognized.Dosage?.Trim(),
                        Comment = recognized.Comment?.Trim(),
                        SourceExcerpt = recognized.SourceExcerpt,
                        DueLocal = dueLocal,
                        DueAtUtc = timeResult.IsSuccess ? dueAtUtc : null,
                        ReviewState = ReviewState.Draft,
                        ExecutionStatus = ExecutionStatus.Pending,
                        NeedsReview = recognized.NeedsReview || !timeResult.IsSuccess,
                        ReviewReason = timeResult.IsSuccess ? recognized.ReviewReason : timeResult.ErrorCode.ToString(),
                        AdvanceLeadMinutes = settings.AdvanceLeadMinutes,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        RowVersion = 1
                    });
                }
                page.UnparsedLinesJson = JsonSerializer.Serialize(result.UnparsedLines);
                page.RemoteDeletePending = result.RemoteDeletePending;
                page.ProcessingState = ImageProcessingState.Ready;
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                page.ProcessingState = ImageProcessingState.Failed;
                page.SafeErrorCode = SafeErrorCode.Unknown;
                session.Status = ImportStatus.Review;
                await context.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (RecognitionException exception)
            {
                page.ProcessingState = ImageProcessingState.Failed;
                page.SafeErrorCode = exception.ErrorCode;
                await context.SaveChangesAsync(cancellationToken);
            }
        }
        session.Status = ImportStatus.Review;
        session.UpdatedAtUtc = clock.UtcNow;
        session.RowVersion++;
        await context.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> AddDraftAsync(Guid sessionId, Guid? sourceImageId, string? sourceExcerpt, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var session = await context.ImportSessions.SingleOrDefaultAsync(item => item.Id == sessionId && item.Status == ImportStatus.Review, cancellationToken);
        if (session is null)
            return new OperationResult(SafeErrorCode.ImportNotFound);
        if (sourceImageId.HasValue && !await context.SourceImages.AnyAsync(item => item.Id == sourceImageId &&
            item.ImportSessionId == sessionId && item.ProcessingState != ImageProcessingState.Excluded, cancellationToken))
            return new OperationResult(SafeErrorCode.Validation);
        var settings = await settingsStore.GetAsync(cancellationToken);
        var now = clock.UtcNow;
        context.Assignments.Add(new Assignment
        {
            Id = Guid.NewGuid(), ShiftId = session.ShiftId, SourceImageId = sourceImageId, SourceExcerpt = sourceExcerpt,
            ReviewState = ReviewState.Draft, ExecutionStatus = ExecutionStatus.Pending, NeedsReview = true,
            ReviewReason = SafeErrorCode.Validation.ToString(), AdvanceLeadMinutes = settings.AdvanceLeadMinutes,
            CreatedAtUtc = now, UpdatedAtUtc = now, RowVersion = 1
        });
        await context.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> UpdateDraftAsync(Assignment draft, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var assignment = await context.Assignments.Include(item => item.Shift)
            .SingleOrDefaultAsync(item => item.Id == draft.Id && item.ReviewState == ReviewState.Draft, cancellationToken);
        if (assignment?.Shift is null)
            return new OperationResult(SafeErrorCode.AssignmentNotFound);
        if (assignment.RowVersion != draft.RowVersion)
            return new OperationResult(SafeErrorCode.ConcurrentUpdate);
        assignment.PatientId = draft.PatientId;
        assignment.PatientName = string.IsNullOrWhiteSpace(draft.PatientName) ? null : draft.PatientName.Trim();
        assignment.Procedure = string.IsNullOrWhiteSpace(draft.Procedure) ? null : draft.Procedure.Trim();
        assignment.Room = string.IsNullOrWhiteSpace(draft.Room) ? null : draft.Room.Trim();
        assignment.Bed = string.IsNullOrWhiteSpace(draft.Bed) ? null : draft.Bed.Trim();
        assignment.Dosage = string.IsNullOrWhiteSpace(draft.Dosage) ? null : draft.Dosage.Trim();
        assignment.Comment = string.IsNullOrWhiteSpace(draft.Comment) ? null : draft.Comment.Trim();
        assignment.DueLocal = draft.DueLocal;
        if (draft.DueLocal is null)
        {
            assignment.DueAtUtc = null;
            assignment.ReviewReason = SafeErrorCode.InvalidTime.ToString();
        }
        else
        {
            var timeResult = _timeService.ResolveDueTime(assignment.Shift, draft.DueLocal.Value, out var dueAtUtc);
            assignment.DueAtUtc = timeResult.IsSuccess ? dueAtUtc : null;
            assignment.ReviewReason = timeResult.IsSuccess ? null : timeResult.ErrorCode.ToString();
        }
        assignment.NeedsReview = false;
        assignment.UpdatedAtUtc = clock.UtcNow;
        assignment.RowVersion++;
        await context.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteDraftAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var draft = await context.Assignments.SingleOrDefaultAsync(item => item.Id == assignmentId && item.ReviewState == ReviewState.Draft, cancellationToken);
        if (draft is null)
            return new OperationResult(SafeErrorCode.AssignmentNotFound);
        context.Assignments.Remove(draft);
        await context.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ExcludeUnparsedLineAsync(Guid imageId, int index, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var page = await context.SourceImages.SingleOrDefaultAsync(item => item.Id == imageId, cancellationToken);
        if (page is null)
            return new OperationResult(SafeErrorCode.ImportNotFound);
        var lines = ImportValidator.ReadUnparsedLines(page).ToList();
        if (index < 0 || index >= lines.Count)
            return new OperationResult(SafeErrorCode.Validation);
        lines[index] = lines[index] with { Excluded = true };
        page.UnparsedLinesJson = JsonSerializer.Serialize(lines);
        await context.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ExcludePageAsync(Guid imageId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var page = await context.SourceImages.Include(item => item.ImportSession)
            .SingleOrDefaultAsync(item => item.Id == imageId, cancellationToken);
        if (page?.ImportSession is null || page.ImportSession.Status != ImportStatus.Review)
            return new OperationResult(SafeErrorCode.ImportNotFound);
        if (page.ProcessingState == ImageProcessingState.Excluded)
            return OperationResult.Success();
        if (page.ProcessingState is ImageProcessingState.Uploading or ImageProcessingState.Recognizing)
            return new OperationResult(SafeErrorCode.PagesNotReady);
        var drafts = await context.Assignments.Where(item => item.SourceImageId == imageId && item.ReviewState == ReviewState.Draft)
            .ToListAsync(cancellationToken);
        context.Assignments.RemoveRange(drafts);
        page.ProcessingState = ImageProcessingState.Excluded;
        page.SafeErrorCode = null;
        page.ImportSession.UpdatedAtUtc = clock.UtcNow;
        page.ImportSession.RowVersion++;
        await context.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ConfirmAsync(Guid sessionId, long expectedRowVersion, bool confirmedEmpty, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var session = await context.ImportSessions.Include(item => item.Shift).SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session?.Shift is null)
            return new OperationResult(SafeErrorCode.ImportNotFound);
        if (session.Status == ImportStatus.Confirmed)
            return OperationResult.Success();
        if (session.RowVersion != expectedRowVersion)
            return new OperationResult(SafeErrorCode.ConcurrentUpdate);
        var pages = await context.SourceImages.Where(item => item.ImportSessionId == sessionId).ToListAsync(cancellationToken);
        var pageIds = pages.Select(item => item.Id).ToArray();
        var drafts = await context.Assignments.Include(item => item.Reminders)
            .Where(item => item.ShiftId == session.ShiftId && item.ReviewState == ReviewState.Draft &&
                (item.SourceImageId == null || pageIds.Contains(item.SourceImageId.Value)))
            .ToListAsync(cancellationToken);
        var validation = _validator.Validate(session, pages, drafts, confirmedEmpty);
        if (!validation.IsSuccess)
            return validation;

        var now = clock.UtcNow;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var draft in drafts)
        {
            var timeResult = _timeService.ResolveDueTime(session.Shift, draft.DueLocal!.Value, out var dueAtUtc);
            if (!timeResult.IsSuccess)
                return timeResult;
            Patient? patient;
            if (draft.PatientId.HasValue)
            {
                patient = await context.Patients.SingleOrDefaultAsync(item => item.Id == draft.PatientId &&
                    item.FullName == draft.PatientName!.Trim() && context.Assignments.Any(assignment => assignment.PatientId == draft.PatientId &&
                        assignment.Room == draft.Room && assignment.Bed == draft.Bed), cancellationToken);
                if (patient is null)
                    return new OperationResult(SafeErrorCode.Validation);
                patient.LastUsedAtUtc = now;
            }
            else
            {
                patient = new Patient { Id = Guid.NewGuid(), FullName = draft.PatientName!.Trim(), CreatedAtUtc = now, LastUsedAtUtc = now };
                context.Patients.Add(patient);
            }
            draft.PatientId = patient.Id;
            draft.DueAtUtc = dueAtUtc;
            draft.ReviewState = ReviewState.Confirmed;
            draft.ExecutionStatus = ExecutionStatus.Pending;
            draft.UpdatedAtUtc = now;
            draft.RowVersion++;
            context.Reminders.AddRange(_reminderPlanner.CreateFuture(draft, now));
        }
        session.Status = ImportStatus.Confirmed;
        session.UpdatedAtUtc = now;
        session.RowVersion++;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new OperationResult(SafeErrorCode.ConcurrentUpdate);
        }
        foreach (var draft in drafts)
            await reminderScheduler.SynchronizeAssignmentAsync(draft.Id, cancellationToken);
        foreach (var page in pages)
            await photoRetentionStore.FinalizeAsync(page, page.RetainLocal, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CancelAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var session = await context.ImportSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null)
            return new OperationResult(SafeErrorCode.ImportNotFound);
        if (session.Status == ImportStatus.Confirmed)
            return new OperationResult(SafeErrorCode.ImportNotFound);
        var pages = await context.SourceImages.Where(item => item.ImportSessionId == sessionId).ToListAsync(cancellationToken);
        var pageIds = pages.Select(item => item.Id).ToArray();
        var drafts = await context.Assignments.Where(item => item.ShiftId == session.ShiftId && item.ReviewState == ReviewState.Draft &&
            (item.SourceImageId == null || pageIds.Contains(item.SourceImageId.Value))).ToListAsync(cancellationToken);
        context.Assignments.RemoveRange(drafts);
        session.Status = ImportStatus.Cancelled;
        session.UpdatedAtUtc = clock.UtcNow;
        session.RowVersion++;
        foreach (var page in pages)
            page.ProcessingState = ImageProcessingState.Cancelled;
        await context.SaveChangesAsync(cancellationToken);
        foreach (var page in pages)
            await photoRetentionStore.FinalizeAsync(page, false, cancellationToken);
        return OperationResult.Success();
    }

    private static async Task<ImportDetails?> LoadDetailsAsync(NurseShiftDbContext context, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await context.ImportSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null)
            return null;
        var pages = await context.SourceImages.AsNoTracking().Where(item => item.ImportSessionId == sessionId).OrderBy(item => item.SortOrder).ToListAsync(cancellationToken);
        var pageIds = pages.Select(item => item.Id).ToArray();
        var assignments = await context.Assignments.AsNoTracking().Where(item => item.ShiftId == session.ShiftId && item.ReviewState == ReviewState.Draft &&
                (item.SourceImageId == null || pageIds.Contains(item.SourceImageId.Value)))
            .OrderBy(item => item.DueLocal).ToListAsync(cancellationToken);
        return new ImportDetails(session, pages, assignments);
    }

    private void EnsurePrivatePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.Ordinal))
            throw new IOException(SafeErrorCode.StorageUnavailable.ToString());
    }
}
