using System.Text.Json;
using NurseShift.Core.Enums;
using NurseShift.Core.Models;

namespace NurseShift.Infrastructure.GigaChat;

public sealed class RecognitionException(SafeErrorCode errorCode, TimeSpan? retryAfter = null) : Exception
{
    public SafeErrorCode ErrorCode { get; } = errorCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public static class RecognitionResponseParser
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly string[] AssignmentProperties = ["patient_name", "room", "bed", "time", "procedure", "dosage", "comment", "source_excerpt", "needs_review", "review_reason"];

    public static RecognitionResult Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || System.Text.Encoding.UTF8.GetByteCount(content) > MaxResponseBytes)
            throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        content = RemoveSingleFence(content.Trim());
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            var root = document.RootElement;
            RequireProperties(root, ["schema_version", "assignments", "unparsed_lines"]);
            if (root.GetProperty("schema_version").GetString() != "1.0")
                throw new RecognitionException(SafeErrorCode.ResponseInvalid);
            var assignmentsElement = root.GetProperty("assignments");
            if (assignmentsElement.ValueKind != JsonValueKind.Array || assignmentsElement.GetArrayLength() > 500)
                throw new RecognitionException(SafeErrorCode.ResponseInvalid);
            var assignments = new List<RecognizedAssignment>();
            foreach (var item in assignmentsElement.EnumerateArray())
            {
                RequireProperties(item, AssignmentProperties);
                var time = NullableString(item, "time", 5);
                if (time is not null && !TimeOnly.TryParseExact(time, "HH:mm", out _))
                    throw new RecognitionException(SafeErrorCode.ResponseInvalid);
                assignments.Add(new RecognizedAssignment(
                    NullableString(item, "patient_name", 200),
                    NullableString(item, "room", 30),
                    NullableString(item, "bed", 30),
                    time,
                    NullableString(item, "procedure", 500),
                    NullableString(item, "dosage", 500),
                    NullableString(item, "comment", 2000),
                    NullableString(item, "source_excerpt", 2000),
                    RequiredBoolean(item, "needs_review"),
                    NullableString(item, "review_reason", 500)));
            }
            var unparsedElement = root.GetProperty("unparsed_lines");
            if (unparsedElement.ValueKind != JsonValueKind.Array || unparsedElement.GetArrayLength() > 200)
                throw new RecognitionException(SafeErrorCode.ResponseInvalid);
            var lines = new List<UnparsedLine>();
            foreach (var item in unparsedElement.EnumerateArray())
            {
                RequireProperties(item, ["text", "reason"]);
                var reason = RequiredString(item, "reason", 500);
                lines.Add(new UnparsedLine(NullableString(item, "text", 2000), reason));
            }
            return new RecognitionResult(assignments, lines, false);
        }
        catch (RecognitionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        }
    }

    private static string RemoveSingleFence(string content)
    {
        if (!content.StartsWith("```", StringComparison.Ordinal))
            return content;
        var firstNewLine = content.IndexOf('\n');
        if (firstNewLine < 0 || !content.EndsWith("```", StringComparison.Ordinal))
            throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        var language = content[3..firstNewLine].Trim();
        if (language.Length > 0 && !language.Equals("json", StringComparison.OrdinalIgnoreCase))
            throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        return content[(firstNewLine + 1)..^3].Trim();
    }

    private static void RequireProperties(JsonElement element, IReadOnlyList<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!actual.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal))
                throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        }
        if (actual.Count != names.Count || names.Any(name => !actual.Contains(name)))
            throw new RecognitionException(SafeErrorCode.ResponseInvalid);
    }

    private static string? NullableString(JsonElement element, string name, int maxLength)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        var text = value.GetString();
        if (text?.Length > maxLength)
            throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        return text;
    }

    private static string RequiredString(JsonElement element, string name, int maxLength) =>
        NullableString(element, name, maxLength) ?? throw new RecognitionException(SafeErrorCode.ResponseInvalid);

    private static bool RequiredBoolean(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        return value.GetBoolean();
    }
}
