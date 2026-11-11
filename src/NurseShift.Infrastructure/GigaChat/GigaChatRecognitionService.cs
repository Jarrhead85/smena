using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;
using NurseShift.Core.Models;
using NurseShift.Infrastructure.Persistence;

namespace NurseShift.Infrastructure.GigaChat;

public sealed class GigaChatRecognitionService(
    HttpClient httpClient,
    GigaChatAuthClient authClient,
    INurseShiftDbContextFactory contextFactory,
    ISettingsStore settingsStore) : IRecognitionService
{
    private const string ApiBase = "https://api.giga.chat/v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken cancellationToken)
    {
        await CleanupPendingDeletesAsync(request.Scope, cancellationToken);
        string? remoteFileId = null;
        var deletePending = false;
        RecognitionResult? recognizedResult = null;
        try
        {
            remoteFileId = await SendWithRetryAsync(token => UploadAsync(request, token), request.Scope, cancellationToken);
            await SetRemoteStateAsync(request.SourceImageId, remoteFileId, true, CancellationToken.None);
            deletePending = true;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var content = await SendWithRetryAsync(token => ChatAsync(remoteFileId, request, token), request.Scope, cancellationToken);
                try
                {
                    recognizedResult = RecognitionResponseParser.Parse(content);
                    break;
                }
                catch (RecognitionException exception) when (exception.ErrorCode == SafeErrorCode.ResponseInvalid && attempt == 0)
                {
                }
            }
            if (recognizedResult is null)
                throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        }
        finally
        {
            if (remoteFileId is not null)
            {
                try
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await SendWithRetryAsync(token => DeleteAsync(remoteFileId, request.Scope, token), request.Scope, cleanupTimeout.Token);
                    deletePending = false;
                    await SetRemoteStateAsync(request.SourceImageId, remoteFileId, false, CancellationToken.None);
                }
                catch (Exception)
                {
                    deletePending = true;
                    await SetRemoteStateAsync(request.SourceImageId, remoteFileId, true, CancellationToken.None);
                }
            }
        }
        return (recognizedResult ?? throw new RecognitionException(SafeErrorCode.ResponseInvalid)) with { RemoteDeletePending = deletePending };
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        await CleanupPendingDeletesAsync(settings.Scope, cancellationToken);
        return await SendWithRetryAsync(async token =>
        {
            using var response = await SendApiAsync(HttpMethod.Get, "/models", settings.Scope, null, token);
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            return (IReadOnlyList<string>)json.RootElement.GetProperty("data").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()).Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>().ToArray();
        }, settings.Scope, cancellationToken);
    }

    public async Task<OperationResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsStore.GetAsync(cancellationToken);
            var models = await GetModelsAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(settings.Model) && !models.Contains(settings.Model, StringComparer.Ordinal))
                return new OperationResult(SafeErrorCode.Validation);
            return OperationResult.Success();
        }
        catch (RecognitionException exception)
        {
            return new OperationResult(exception.ErrorCode);
        }
    }

    private async Task<string> UploadAsync(RecognitionRequest request, CancellationToken cancellationToken)
    {
        await using var image = await request.OpenImage(cancellationToken);
        if (image.CanSeek && image.Length > 15 * 1024 * 1024)
            throw new RecognitionException(SafeErrorCode.ImageTooLarge);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("general"), "purpose");
        var fileContent = new StreamContent(image);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(fileContent, "file", "page.jpg");
        using var response = await SendApiAsync(HttpMethod.Post, "/files", request.Scope, content, cancellationToken);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("id").GetString() ?? throw new RecognitionException(SafeErrorCode.ResponseInvalid);
    }

    private async Task<string> ChatAsync(string remoteFileId, RecognitionRequest request, CancellationToken cancellationToken)
    {
        var systemPrompt = await ReadPromptAsync(cancellationToken);
        var localContext = request.Shift;
        var payload = new
        {
            model = request.Model,
            stream = false,
            temperature = 0.1,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = $"Извлеки назначения из приложенного листа. Контекст смены: начало {localContext.StartLocal:yyyy-MM-dd HH:mm}; конец {localContext.EndLocal:yyyy-MM-dd HH:mm}; часовой пояс {localContext.TimeZoneId}. Не предполагай отсутствующие данные. Верни только JSON по схеме 1.0.",
                    attachments = new[] { remoteFileId }
                }
            }
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await SendApiAsync(HttpMethod.Post, "/chat/completions", request.Scope, content, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
               ?? throw new RecognitionException(SafeErrorCode.ResponseInvalid);
    }

    private async Task<bool> DeleteAsync(string remoteFileId, string scope, CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(HttpMethod.Post, $"/files/{Uri.EscapeDataString(remoteFileId)}/delete", scope, null, cancellationToken);
        return true;
    }

    private async Task<HttpResponseMessage> SendApiAsync(HttpMethod method, string path, string scope, HttpContent? content, CancellationToken cancellationToken)
    {
        var token = await authClient.GetTokenAsync(scope, false, cancellationToken);
        using var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = content;
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RecognitionException(SafeErrorCode.Timeout);
        }
        catch (HttpRequestException exception) when (GigaChatAuthClient.IsTlsError(exception))
        {
            throw new RecognitionException(SafeErrorCode.TlsValidationFailed);
        }
        catch (HttpRequestException)
        {
            throw new RecognitionException(SafeErrorCode.NetworkUnavailable);
        }
        if (!response.IsSuccessStatusCode)
        {
            var code = GigaChatAuthClient.MapStatus(response.StatusCode);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
                retryAfter = date - DateTimeOffset.UtcNow;
            response.Dispose();
            throw new RecognitionException(code, retryAfter);
        }
        return response;
    }

    private async Task<T> SendWithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, string scope, CancellationToken cancellationToken)
    {
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (RecognitionException exception) when (exception.ErrorCode == SafeErrorCode.Unauthorized && !refreshed)
            {
                refreshed = true;
                await authClient.GetTokenAsync(scope, true, cancellationToken);
            }
            catch (RecognitionException exception) when (attempt < 2 && IsRetryable(exception.ErrorCode))
            {
                var delay = exception.RetryAfter is { } retryAfter ? TimeSpan.FromMilliseconds(Math.Clamp(retryAfter.TotalMilliseconds, 0, 60000)) : TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
            catch (JsonException)
            {
                throw new RecognitionException(SafeErrorCode.ResponseInvalid);
            }
            catch (InvalidOperationException)
            {
                throw new RecognitionException(SafeErrorCode.ResponseInvalid);
            }
        }
    }

    private static bool IsRetryable(SafeErrorCode code) => code is SafeErrorCode.NetworkUnavailable or SafeErrorCode.Timeout or SafeErrorCode.ServiceUnavailable or SafeErrorCode.RateLimited;

    private async Task CleanupPendingDeletesAsync(string scope, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var pendingPages = await context.SourceImages.AsNoTracking()
            .Where(item => item.RemoteDeletePending && item.RemoteFileId != null)
            .Select(item => new { item.Id, item.RemoteFileId })
            .ToListAsync(cancellationToken);
        foreach (var page in pendingPages)
        {
            try
            {
                await SendWithRetryAsync(token => DeleteAsync(page.RemoteFileId!, scope, token), scope, cancellationToken);
                await SetRemoteStateAsync(page.Id, page.RemoteFileId!, false, CancellationToken.None);
            }
            catch (RecognitionException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task SetRemoteStateAsync(Guid sourceImageId, string remoteFileId, bool pending, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateAsync(cancellationToken);
        var page = await context.SourceImages.SingleOrDefaultAsync(item => item.Id == sourceImageId, cancellationToken);
        if (page is null)
            return;
        page.RemoteFileId = remoteFileId;
        page.RemoteDeletePending = pending;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<string> ReadPromptAsync(CancellationToken cancellationToken)
    {
        await using var stream = typeof(GigaChatRecognitionService).Assembly.GetManifestResourceStream("NurseShift.Infrastructure.GigaChat.recognition-system-prompt.txt")
            ?? throw new RecognitionException(SafeErrorCode.ResponseInvalid);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
