using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;

namespace NurseShift.Infrastructure.Persistence;

public sealed class PhotoAccess(INurseShiftDbContextFactory contextFactory, string storageRoot) : IPhotoAccess
{
    public async Task<byte[]> ReadAsync(Guid imageId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var page = await context.SourceImages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == imageId, cancellationToken)
            ?? throw new IOException(SafeErrorCode.ImportNotFound.ToString());
        var path = Path.GetFullPath(page.LocalPath);
        var root = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
            throw new IOException(SafeErrorCode.ImageUnreadable.ToString());
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }
}
