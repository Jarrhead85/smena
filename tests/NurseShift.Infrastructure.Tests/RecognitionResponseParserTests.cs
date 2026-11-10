using NurseShift.Core.Enums;
using NurseShift.Infrastructure.GigaChat;

namespace NurseShift.Infrastructure.Tests;

public sealed class RecognitionResponseParserTests
{
    private const string Valid = """
    {"schema_version":"1.0","assignments":[{"patient_name":"Тестовый Пациент 001","room":"T1","bed":"1","time":"06:00","procedure":"Тестовая процедура","dosage":null,"comment":null,"source_excerpt":"Тестовая строка","needs_review":false,"review_reason":null}],"unparsed_lines":[]}
    """;

    [Fact]
    public void Parse_ValidSyntheticResponse_ReturnsDraftData()
    {
        var result = RecognitionResponseParser.Parse(Valid);
        var assignment = Assert.Single(result.Assignments);
        Assert.Equal("06:00", assignment.Time);
        Assert.Null(assignment.Dosage);
    }

    [Fact]
    public void Parse_OuterCodeFence_IsAccepted() => Assert.Single(RecognitionResponseParser.Parse("```json\n" + Valid + "\n```").Assignments);

    [Theory]
    [InlineData("Текст перед JSON\n" + "{\"schema_version\":\"1.0\"}")]
    [InlineData("{\"schema_version\":\"2.0\",\"assignments\":[],\"unparsed_lines\":[]}")]
    [InlineData("{\"schema_version\":\"1.0\",\"assignments\":[],\"unparsed_lines\":[],\"extra\":true}")]
    public void Parse_UnexpectedContent_IsRejected(string content)
    {
        var error = Assert.Throws<RecognitionException>(() => RecognitionResponseParser.Parse(content));
        Assert.Equal(SafeErrorCode.ResponseInvalid, error.ErrorCode);
    }

    [Fact]
    public void Parse_InvalidTime_IsRejectedRatherThanGuessed()
    {
        var error = Assert.Throws<RecognitionException>(() => RecognitionResponseParser.Parse(Valid.Replace("06:00", "25:99")));
        Assert.Equal(SafeErrorCode.ResponseInvalid, error.ErrorCode);
    }

    [Fact]
    public void Parse_NullCriticalFields_ArePreservedForReview()
    {
        var content = Valid.Replace("\"Тестовый Пациент 001\"", "null").Replace("\"Тестовая процедура\"", "null");
        var assignment = Assert.Single(RecognitionResponseParser.Parse(content).Assignments);
        Assert.Null(assignment.PatientName);
        Assert.Null(assignment.Procedure);
    }
}
