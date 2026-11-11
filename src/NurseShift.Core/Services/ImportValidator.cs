using System.Text.Json;
using NurseShift.Core.Enums;
using NurseShift.Core.Models;

namespace NurseShift.Core.Services;

public sealed class ImportValidator
{
    private readonly ShiftTimeService _timeService = new();

    public OperationResult Validate(ImportSession session, IReadOnlyList<SourceImage> pages, IReadOnlyList<Assignment> assignments, bool confirmedEmpty)
    {
        if (session.Status == ImportStatus.Confirmed)
            return OperationResult.Success();
        if (session.Status != ImportStatus.Review || session.Shift?.Status != ShiftStatus.Active)
            return new OperationResult(SafeErrorCode.ShiftNotActive);
        if (pages.Any(item => item.ProcessingState is ImageProcessingState.Pending or ImageProcessingState.Uploading or
            ImageProcessingState.Recognizing or ImageProcessingState.Failed or ImageProcessingState.Cancelled))
            return new OperationResult(SafeErrorCode.PagesNotReady);
        if (assignments.Count == 0 && !confirmedEmpty)
            return new OperationResult(SafeErrorCode.ConfirmationRequired);

        foreach (var assignment in assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.PatientName) ||
                string.IsNullOrWhiteSpace(assignment.Procedure) ||
                assignment.DueLocal is null)
                return new OperationResult(SafeErrorCode.Validation);
            if (!_timeService.ResolveDueTime(session.Shift, assignment.DueLocal.Value, out _).IsSuccess)
                return new OperationResult(SafeErrorCode.TimeOutsideShift);
        }

        foreach (var page in pages)
        {
            if (page.ProcessingState == ImageProcessingState.Excluded)
                continue;
            foreach (var line in ReadUnparsedLines(page))
            {
                if (!line.Excluded)
                    return new OperationResult(SafeErrorCode.UnparsedLinesRemain);
            }
        }
        return OperationResult.Success();
    }

    public static IReadOnlyList<UnparsedLine> ReadUnparsedLines(SourceImage page)
    {
        if (string.IsNullOrWhiteSpace(page.UnparsedLinesJson))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<UnparsedLine>>(page.UnparsedLinesJson) ?? [];
        }
        catch (JsonException)
        {
            return [new UnparsedLine(null, "InvalidStoredUnparsedLines")];
        }
    }
}
