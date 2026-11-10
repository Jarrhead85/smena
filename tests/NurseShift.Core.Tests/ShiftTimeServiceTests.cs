using NurseShift.Core.Enums;
using NurseShift.Core.Services;

namespace NurseShift.Core.Tests;

public sealed class ShiftTimeServiceTests
{
    private readonly ShiftTimeService _service = new();
    private const string Zone = "Europe/Moscow";

    [Fact]
    public void CreateShift_Overnight_CalculatesUtcAndKeepsLocalDates()
    {
        var start = new DateTime(2026, 11, 10, 22, 0, 0);
        var end = new DateTime(2026, 11, 11, 6, 0, 0);
        var result = _service.ValidateShift(start, end, Zone, out var shift);
        Assert.True(result.IsSuccess);
        Assert.Equal(8, (shift!.EndAtUtc - shift.StartAtUtc).TotalHours);
        Assert.Equal(start, shift.StartLocal);
        Assert.Equal(end, shift.EndLocal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24 * 60 + 1)]
    public void CreateShift_InvalidDuration_ReturnsValidation(int durationMinutes)
    {
        var start = new DateTime(2026, 11, 10, 8, 0, 0);
        var result = _service.ValidateShift(start, start.AddMinutes(durationMinutes), Zone, out _);
        Assert.Equal(SafeErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public void ResolveRecognizedTime_AfterMidnight_SelectsNextDate()
    {
        var shift = OvernightShift();
        var result = _service.ResolveRecognizedTime(shift, "02:30", out var local, out var utc);
        Assert.True(result.IsSuccess);
        Assert.Equal(new DateTime(2026, 11, 11, 2, 30, 0), local);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 23, 30, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void ResolveRecognizedTime_OutsideShift_RequiresCorrection()
    {
        var result = _service.ResolveRecognizedTime(OvernightShift(), "12:00", out var local, out _);
        Assert.Equal(SafeErrorCode.TimeOutsideShift, result.ErrorCode);
        Assert.Null(local);
    }

    [Fact]
    public void ResolveRecognizedTime_NonStandardTime_DoesNotGuess()
    {
        var result = _service.ResolveRecognizedTime(OvernightShift(), "25:99", out var local, out _);
        Assert.Equal(SafeErrorCode.InvalidTime, result.ErrorCode);
        Assert.Null(local);
    }

    private static Core.Models.Shift OvernightShift()
    {
        new ShiftTimeService().ValidateShift(new DateTime(2026, 11, 10, 22, 0, 0), new DateTime(2026, 11, 11, 6, 0, 0), Zone, out var shift);
        return shift!;
    }
}
