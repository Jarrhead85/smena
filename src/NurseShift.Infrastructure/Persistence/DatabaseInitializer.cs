using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;

namespace NurseShift.Infrastructure.Persistence;

public sealed class DatabaseInitializer(INurseShiftDbContextFactory contextFactory)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        var processing = await context.ImportSessions.Where(item => item.Status == ImportStatus.Processing).ToListAsync(cancellationToken);
        foreach (var session in processing)
        {
            session.Status = ImportStatus.Review;
            session.RowVersion++;
        }
        var uncertainPages = await context.SourceImages.Where(item =>
            item.ProcessingState == ImageProcessingState.Uploading ||
            item.ProcessingState == ImageProcessingState.Recognizing).ToListAsync(cancellationToken);
        foreach (var page in uncertainPages)
        {
            page.ProcessingState = ImageProcessingState.Failed;
            page.SafeErrorCode = SafeErrorCode.Unknown;
        }
        await context.SaveChangesAsync(cancellationToken);
    }
}
