using NurseShift.Core.Enums;
using NurseShift.Core.Models;

namespace NurseShift.Core.Services;

public sealed class ShiftTimeService
{
    public OperationResult ValidateShift(DateTime startLocal, DateTime endLocal, string timeZoneId, out Shift? shift)
    {
        shift = null;
        if (endLocal <= startLocal || endLocal - startLocal > TimeSpan.FromHours(24))
            return new OperationResult(SafeErrorCode.Validation);

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            if (zone.IsInvalidTime(startLocal) || zone.IsInvalidTime(endLocal) ||
                zone.IsAmbiguousTime(startLocal) || zone.IsAmbiguousTime(endLocal))
                return new OperationResult(SafeErrorCode.AmbiguousTime);

            shift = new Shift
            {
                Id = Guid.NewGuid(),
                StartLocal = DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified),
                EndLocal = DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified),
                StartAtUtc = new DateTimeOffset(startLocal, zone.GetUtcOffset(startLocal)).ToUniversalTime(),
                EndAtUtc = new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal)).ToUniversalTime(),
                TimeZoneId = zone.Id,
                Status = ShiftStatus.Active
            };
            return OperationResult.Success();
        }
        catch (TimeZoneNotFoundException)
        {
            return new OperationResult(SafeErrorCode.InvalidTime);
        }
        catch (InvalidTimeZoneException)
        {
            return new OperationResult(SafeErrorCode.InvalidTime);
        }
    }

    public OperationResult ResolveDueTime(Shift shift, DateTime local, out DateTimeOffset dueAtUtc)
    {
        dueAtUtc = default;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (local < shift.StartLocal || local > shift.EndLocal)
            return new OperationResult(SafeErrorCode.TimeOutsideShift);

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(shift.TimeZoneId);
            if (zone.IsInvalidTime(local))
                return new OperationResult(SafeErrorCode.InvalidTime);
            if (zone.IsAmbiguousTime(local))
                return new OperationResult(SafeErrorCode.AmbiguousTime);

            dueAtUtc = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
            if (dueAtUtc < shift.StartAtUtc || dueAtUtc > shift.EndAtUtc)
                return new OperationResult(SafeErrorCode.TimeOutsideShift);
            return OperationResult.Success();
        }
        catch (TimeZoneNotFoundException)
        {
            return new OperationResult(SafeErrorCode.InvalidTime);
        }
        catch (InvalidTimeZoneException)
        {
            return new OperationResult(SafeErrorCode.InvalidTime);
        }
    }

    public OperationResult ResolveRecognizedTime(Shift shift, string? value, out DateTime? dueLocal, out DateTimeOffset dueAtUtc)
    {
        dueLocal = null;
        dueAtUtc = default;
        if (!TimeOnly.TryParseExact(value, "HH:mm", out var time))
            return new OperationResult(SafeErrorCode.InvalidTime);

        var candidates = new[] { shift.StartLocal.Date.Add(time.ToTimeSpan()), shift.StartLocal.Date.AddDays(1).Add(time.ToTimeSpan()) }
            .Where(candidate => candidate >= shift.StartLocal && candidate <= shift.EndLocal)
            .ToArray();
        if (candidates.Length != 1)
            return new OperationResult(candidates.Length == 0 ? SafeErrorCode.TimeOutsideShift : SafeErrorCode.AmbiguousTime);

        var result = ResolveDueTime(shift, candidates[0], out dueAtUtc);
        if (result.IsSuccess)
            dueLocal = candidates[0];
        return result;
    }
}
