using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;

namespace NurseShift.Infrastructure.Persistence;

public sealed class DataCleanupService(
    INurseShiftDbContextFactory contextFactory,
    IShiftService shiftService,
    IClock clock,
    ISecretStore secretStore,
    ISettingsStore settingsStore,
    string storageRoot) : IDataCleanupService
{
    public async Task CleanupHistoryAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var settings = await settingsStore.GetAsync(cancellationToken);
        var completedShifts = await context.Shifts.Where(item => item.Status == ShiftStatus.Completed).ToListAsync(cancellationToken);
        foreach (var shift in completedShifts)
        {
            var expiration = settings.HistoryRetentionDays is int days
                ? (shift.CompletedAtUtc ?? shift.UpdatedAtUtc).AddDays(days)
                : (DateTimeOffset?)null;
            if (shift.HistoryExpiresAtUtc == expiration)
                continue;
            shift.HistoryExpiresAtUtc = expiration;
            shift.RowVersion++;
        }
        await context.SaveChangesAsync(cancellationToken);
        var ids = completedShifts.Where(item => item.HistoryExpiresAtUtc != null && item.HistoryExpiresAtUtc <= clock.UtcNow)
            .Select(item => item.Id).ToArray();
        foreach (var id in ids)
            await shiftService.DeleteShiftAsync(id, cancellationToken);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        await using (var context = await contextFactory.CreateAsync(cancellationToken))
        {
            foreach (var shift in await context.Shifts.AsNoTracking().Select(item => item.Id).ToListAsync(cancellationToken))
                await shiftService.DeleteShiftAsync(shift, cancellationToken);
            await context.Database.EnsureDeletedAsync(cancellationToken);
        }
        if (Directory.Exists(storageRoot))
            Directory.Delete(storageRoot, true);
        await secretStore.RemoveAsync("gigachat.authorization_key", cancellationToken);
        await secretStore.RemoveAsync(NurseShiftDbContextFactory.DatabaseKeyName, cancellationToken);
        await secretStore.RemoveAsync("photos.encryption_key", cancellationToken);
        await settingsStore.SaveAsync(Core.Models.AppSettings.Default, cancellationToken);
    }
}
