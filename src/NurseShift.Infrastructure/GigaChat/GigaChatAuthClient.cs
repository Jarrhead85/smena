using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NurseShift.Core.Enums;
using NurseShift.Core.Interfaces;

namespace NurseShift.Infrastructure.GigaChat;

public sealed class GigaChatAuthClient(HttpClient httpClient, ISecretStore secretStore)
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAtUtc;
    private string? _authorizationKeyHash;
    private string? _tokenScope;

    public async Task<string> GetTokenAsync(string scope, bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = await secretStore.GetAsync("gigachat.authorization_key", cancellationToken);
        if (string.IsNullOrWhiteSpace(key))
            throw new RecognitionException(SafeErrorCode.Unauthorized);
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        if (CanUseCachedToken(scope, keyHash, forceRefresh))
            return _accessToken!;
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (CanUseCachedToken(scope, keyHash, forceRefresh))
                return _accessToken!;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://ngw.devices.sberbank.ru:9443/api/v2/oauth");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", key);
            request.Headers.Add("RqUID", Guid.NewGuid().ToString());
            request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("scope", scope)]);
            try
            {
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new RecognitionException(MapStatus(response.StatusCode));
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var token = json.RootElement.GetProperty("access_token").GetString();
                var expires = json.RootElement.GetProperty("expires_at").GetInt64();
                if (string.IsNullOrWhiteSpace(token))
                    throw new RecognitionException(SafeErrorCode.ResponseInvalid);
                _accessToken = token;
                _expiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(expires);
                _authorizationKeyHash = keyHash;
                _tokenScope = scope;
                return token;
            }
            catch (RecognitionException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RecognitionException(SafeErrorCode.Timeout);
            }
            catch (HttpRequestException exception) when (IsTlsError(exception))
            {
                throw new RecognitionException(SafeErrorCode.TlsValidationFailed);
            }
            catch (HttpRequestException)
            {
                throw new RecognitionException(SafeErrorCode.NetworkUnavailable);
            }
            catch (JsonException)
            {
                throw new RecognitionException(SafeErrorCode.ResponseInvalid);
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private bool CanUseCachedToken(string scope, string keyHash, bool forceRefresh) => !forceRefresh && _accessToken is not null &&
        _authorizationKeyHash == keyHash && _tokenScope == scope && _expiresAtUtc > DateTimeOffset.UtcNow.AddSeconds(60);

    internal static SafeErrorCode MapStatus(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized => SafeErrorCode.Unauthorized,
        System.Net.HttpStatusCode.Forbidden => SafeErrorCode.PermissionDenied,
        System.Net.HttpStatusCode.RequestEntityTooLarge => SafeErrorCode.ImageTooLarge,
        System.Net.HttpStatusCode.TooManyRequests => SafeErrorCode.RateLimited,
        >= System.Net.HttpStatusCode.InternalServerError => SafeErrorCode.ServiceUnavailable,
        _ => SafeErrorCode.ResponseInvalid
    };

    internal static bool IsTlsError(HttpRequestException exception) =>
        exception.InnerException is AuthenticationException ||
        exception.InnerException?.InnerException is AuthenticationException ||
        exception.InnerException is IOException { InnerException: AuthenticationException };
}
