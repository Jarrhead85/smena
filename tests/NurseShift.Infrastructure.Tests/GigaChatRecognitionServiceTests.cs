using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;
using NurseShift.Core.Models;
using NurseShift.Infrastructure.GigaChat;
using NurseShift.Infrastructure.Persistence;

namespace NurseShift.Infrastructure.Tests;

public sealed class GigaChatRecognitionServiceTests : IDisposable
{
    private const string ValidRecognition = """
    {"schema_version":"1.0","assignments":[{"patient_name":"Тестовый Пациент 001","room":"T1","bed":"1","time":"23:30","procedure":"Тестовая процедура","dosage":null,"comment":null,"source_excerpt":"Синтетическая строка","needs_review":false,"review_reason":null}],"unparsed_lines":[]}
    """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nurseshift-http-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecretStore _secrets = new();
    private readonly MemorySettingsStore _settings = new();
    private readonly NurseShiftDbContextFactory _factory;

    public GigaChatRecognitionServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _secrets.SetAsync("gigachat.authorization_key", "synthetic-test-key", CancellationToken.None).GetAwaiter().GetResult();
        _factory = new NurseShiftDbContextFactory(Path.Combine(_directory, "test.db"), _secrets);
        using var context = _factory.CreateAsync(CancellationToken.None).GetAwaiter().GetResult();
        context.Database.Migrate();
    }

    [Fact]
    public async Task Recognize_UploadsAttachmentDeletesFileAndCachesOAuthToken()
    {
        var oauthCount = 0;
        var uploadCount = 0;
        var chatCount = 0;
        var deleteCount = 0;
        using var handler = new RecordingHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth", StringComparison.Ordinal))
            {
                oauthCount++;
                return TokenResponse("synthetic-token");
            }
            if (path.EndsWith("/files", StringComparison.Ordinal))
            {
                uploadCount++;
                var multipart = await request.Content!.ReadAsStringAsync();
                Assert.Contains("purpose", multipart, StringComparison.Ordinal);
                Assert.Contains("general", multipart, StringComparison.Ordinal);
                Assert.Contains("image/jpeg", multipart, StringComparison.Ordinal);
                return Json("{\"id\":\"synthetic-remote-file\"}");
            }
            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                chatCount++;
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var user = payload.RootElement.GetProperty("messages")[1];
                Assert.Equal("synthetic-remote-file", user.GetProperty("attachments")[0].GetString());
                return ChatResponse(ValidRecognition);
            }
            if (path.EndsWith("/files/synthetic-remote-file/delete", StringComparison.Ordinal))
            {
                deleteCount++;
                return Json("{\"deleted\":true}");
            }
            if (path.EndsWith("/models", StringComparison.Ordinal))
                return Json("{\"data\":[{\"id\":\"test-vision-model\"}]}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = CreateService(handler);

        var result = await service.RecognizeAsync(Request(Guid.NewGuid()), CancellationToken.None);
        await service.GetModelsAsync(CancellationToken.None);

        Assert.False(result.RemoteDeletePending);
        Assert.Single(result.Assignments);
        Assert.Equal(1, oauthCount);
        Assert.Equal(1, uploadCount);
        Assert.Equal(1, chatCount);
        Assert.Equal(1, deleteCount);
        Assert.DoesNotContain(handler.Requests, item => item.Contains("synthetic-test-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidModelJson_IsRetriedOnceAndRemoteFileIsDeleted()
    {
        var chatCount = 0;
        var deleteCount = 0;
        using var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth", StringComparison.Ordinal))
                return Task.FromResult(TokenResponse("synthetic-token"));
            if (path.EndsWith("/files", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"id\":\"synthetic-remote-file\"}"));
            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
                return Task.FromResult(ChatResponse(chatCount++ == 0 ? "not-json" : ValidRecognition));
            if (path.EndsWith("/delete", StringComparison.Ordinal))
            {
                deleteCount++;
                return Task.FromResult(Json("{\"deleted\":true}"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var result = await CreateService(handler).RecognizeAsync(Request(Guid.NewGuid()), CancellationToken.None);

        Assert.Single(result.Assignments);
        Assert.Equal(2, chatCount);
        Assert.Equal(1, deleteCount);
    }

    [Fact]
    public async Task CancellationAfterUpload_StillAttemptsRemoteDelete()
    {
        using var cancellation = new CancellationTokenSource();
        var deleteCount = 0;
        using var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth", StringComparison.Ordinal))
                return Task.FromResult(TokenResponse("synthetic-token"));
            if (path.EndsWith("/files", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"id\":\"synthetic-remote-file\"}"));
            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            if (path.EndsWith("/delete", StringComparison.Ordinal))
            {
                deleteCount++;
                return Task.FromResult(Json("{\"deleted\":true}"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateService(handler).RecognizeAsync(Request(Guid.NewGuid()), cancellation.Token));

        Assert.Equal(1, deleteCount);
    }

    [Fact]
    public async Task FailedDelete_IsRetriedByNextExplicitNetworkOperation()
    {
        var imageId = Guid.NewGuid();
        await SeedPageAsync(imageId);
        var failDeletes = true;
        using var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth", StringComparison.Ordinal))
                return Task.FromResult(TokenResponse("synthetic-token"));
            if (path.EndsWith("/files", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"id\":\"synthetic-remote-file\"}"));
            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
                return Task.FromResult(ChatResponse(ValidRecognition));
            if (path.EndsWith("/delete", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(failDeletes ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"deleted\":true}", Encoding.UTF8, "application/json")
                });
            if (path.EndsWith("/models", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"data\":[{\"id\":\"test-vision-model\"}]}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var service = CreateService(handler);

        var result = await service.RecognizeAsync(Request(imageId), CancellationToken.None);
        Assert.True(result.RemoteDeletePending);
        await using (var context = await _factory.CreateAsync(CancellationToken.None))
            Assert.True((await context.SourceImages.SingleAsync()).RemoteDeletePending);

        failDeletes = false;
        await service.GetModelsAsync(CancellationToken.None);

        await using (var context = await _factory.CreateAsync(CancellationToken.None))
            Assert.False((await context.SourceImages.SingleAsync()).RemoteDeletePending);
    }

    [Fact]
    public async Task UnauthorizedResponse_RefreshesOAuthTokenOnce()
    {
        var oauthCount = 0;
        var modelsCount = 0;
        var bearerTokens = new List<string?>();
        using var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth", StringComparison.Ordinal))
                return Task.FromResult(TokenResponse("synthetic-token-" + ++oauthCount));
            if (path.EndsWith("/models", StringComparison.Ordinal))
            {
                modelsCount++;
                bearerTokens.Add(request.Headers.Authorization?.Parameter);
                return Task.FromResult(modelsCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : Json("{\"data\":[{\"id\":\"test-vision-model\"}]}"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var models = await CreateService(handler).GetModelsAsync(CancellationToken.None);

        Assert.Equal("test-vision-model", Assert.Single(models));
        Assert.Equal(2, oauthCount);
        Assert.Equal(new string?[] { "synthetic-token-1", "synthetic-token-2" }, bearerTokens);
    }

    [Fact]
    public async Task ChangedKeyOrScope_InvalidatesCachedToken()
    {
        var oauthCount = 0;
        using var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth", StringComparison.Ordinal))
                return Task.FromResult(TokenResponse("synthetic-token-" + ++oauthCount));
            if (path.EndsWith("/models", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"data\":[{\"id\":\"test-vision-model\"}]}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var service = CreateService(handler);
        await service.GetModelsAsync(CancellationToken.None);
        await _secrets.SetAsync("gigachat.authorization_key", "another-synthetic-key", CancellationToken.None);
        await service.GetModelsAsync(CancellationToken.None);
        _settings.Value = _settings.Value with { Scope = "GIGACHAT_API_B2B" };

        await service.GetModelsAsync(CancellationToken.None);

        Assert.Equal(3, oauthCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 2)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    public async Task UploadRetry_ReopensImageAndSendsSameBytes(HttpStatusCode firstFailure, int expectedOAuthCount)
    {
        var oauthCount = 0;
        var uploadCount = 0;
        var payloads = new List<byte[]>();
        var streams = new List<TrackingMemoryStream>();
        using var handler = new RecordingHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth", StringComparison.Ordinal))
            {
                oauthCount++;
                return TokenResponse("synthetic-token");
            }
            if (path.EndsWith("/files", StringComparison.Ordinal))
            {
                uploadCount++;
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                payloads.Add(await multipart.Last().ReadAsByteArrayAsync());
                return uploadCount == 1 ? new HttpResponseMessage(firstFailure) : Json("{\"id\":\"synthetic-remote-file\"}");
            }
            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
                return ChatResponse(ValidRecognition);
            if (path.EndsWith("/delete", StringComparison.Ordinal))
                return Json("{\"deleted\":true}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await CreateService(handler).RecognizeAsync(Request(Guid.NewGuid(), streams), CancellationToken.None);

        Assert.Single(result.Assignments);
        Assert.Equal(2, uploadCount);
        Assert.Equal(expectedOAuthCount, oauthCount);
        Assert.Equal(payloads[0], payloads[1]);
        Assert.Equal(2, streams.Count);
        Assert.All(streams, stream => Assert.True(stream.IsDisposed));
    }

    [Fact]
    public async Task NetworkRetry_ReopensImageAndDisposesEveryAttempt()
    {
        var uploadCount = 0;
        var streams = new List<TrackingMemoryStream>();
        using var handler = new RecordingHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth", StringComparison.Ordinal))
                return TokenResponse("synthetic-token");
            if (path.EndsWith("/files", StringComparison.Ordinal))
            {
                uploadCount++;
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, await multipart.Last().ReadAsByteArrayAsync());
                if (uploadCount == 1)
                    throw new HttpRequestException("Synthetic network failure");
                return Json("{\"id\":\"synthetic-remote-file\"}");
            }
            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
                return ChatResponse(ValidRecognition);
            if (path.EndsWith("/delete", StringComparison.Ordinal))
                return Json("{\"deleted\":true}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await CreateService(handler).RecognizeAsync(Request(Guid.NewGuid(), streams), CancellationToken.None);

        Assert.Single(result.Assignments);
        Assert.Equal(2, uploadCount);
        Assert.Equal(2, streams.Count);
        Assert.All(streams, stream => Assert.True(stream.IsDisposed));
    }

    [Fact]
    public async Task CancellationDuringRetryDelay_DoesNotStartAnotherUpload()
    {
        using var cancellation = new CancellationTokenSource();
        var uploadCount = 0;
        var streams = new List<TrackingMemoryStream>();
        using var handler = new RecordingHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth", StringComparison.Ordinal))
                return TokenResponse("synthetic-token");
            if (path.EndsWith("/files", StringComparison.Ordinal))
            {
                uploadCount++;
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                await multipart.Last().ReadAsByteArrayAsync();
                cancellation.Cancel();
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(handler).RecognizeAsync(Request(Guid.NewGuid(), streams), cancellation.Token));

        Assert.Equal(1, uploadCount);
        Assert.All(streams, stream => Assert.True(stream.IsDisposed));
    }

    private GigaChatRecognitionService CreateService(RecordingHandler handler) => new(
        new HttpClient(handler), new GigaChatAuthClient(new HttpClient(handler), _secrets), _factory, _settings);

    private RecognitionRequest Request(Guid imageId, List<TrackingMemoryStream>? streams = null) => new(imageId, _ =>
    {
        var stream = new TrackingMemoryStream([1, 2, 3, 4]);
        streams?.Add(stream);
        return ValueTask.FromResult<Stream>(stream);
    }, new Shift
    {
        Id = Guid.NewGuid(),
        StartLocal = new DateTime(2026, 11, 10, 22, 0, 0),
        EndLocal = new DateTime(2026, 11, 11, 6, 0, 0),
        StartAtUtc = new DateTimeOffset(2026, 11, 10, 19, 0, 0, TimeSpan.Zero),
        EndAtUtc = new DateTimeOffset(2026, 11, 11, 3, 0, 0, TimeSpan.Zero),
        TimeZoneId = "Europe/Moscow",
        Status = ShiftStatus.Active
    }, "test-vision-model", "GIGACHAT_API_PERS");

    private async Task SeedPageAsync(Guid imageId)
    {
        await using var context = await _factory.CreateAsync(CancellationToken.None);
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            StartLocal = new DateTime(2026, 11, 10, 22, 0, 0),
            EndLocal = new DateTime(2026, 11, 11, 6, 0, 0),
            StartAtUtc = new DateTimeOffset(2026, 11, 10, 19, 0, 0, TimeSpan.Zero),
            EndAtUtc = new DateTimeOffset(2026, 11, 11, 3, 0, 0, TimeSpan.Zero),
            TimeZoneId = "Europe/Moscow",
            Status = ShiftStatus.Active
        };
        context.Shifts.Add(shift);
        context.ImportSessions.Add(new ImportSession { Id = Guid.NewGuid(), ShiftId = shift.Id, Status = ImportStatus.Processing });
        context.SourceImages.Add(new SourceImage
        {
            Id = imageId,
            ImportSessionId = context.ImportSessions.Local.Single().Id,
            LocalPath = Path.Combine(_directory, "synthetic.jpg"),
            ProcessingState = ImageProcessingState.Pending
        });
        await context.SaveChangesAsync();
    }

    private static HttpResponseMessage TokenResponse(string token) => Json($"{{\"access_token\":\"{token}\",\"expires_at\":{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}}}");

    private static HttpResponseMessage ChatResponse(string content) => Json(JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content } } }
    }));

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}

internal sealed class TrackingMemoryStream(byte[] content) : MemoryStream(content)
{
    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
        return responder(request);
    }
}
