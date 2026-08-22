using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TSM.ItemDatabaseBuilder;

internal sealed class BlizzardApiClient : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _region;
    private readonly string _locale;
    private readonly int _maximumAttempts;
    private readonly TimeSpan _minimumRequestSpacing;
    private readonly SemaphoreSlim _requestScheduleLock = new(1, 1);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;
    private DateTimeOffset _lastRequestAt;

    public BlizzardApiClient(string clientId, string clientSecret, string region, string locale,
        int requestsPerSecond, int maximumAttempts)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _region = region;
        _locale = locale;
        _maximumAttempts = maximumAttempts;
        _minimumRequestSpacing = TimeSpan.FromSeconds(1d / requestsPerSecond);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TSM-ItemDatabaseBuilder/0.1");
    }

    public async Task<JsonDocument?> GetItemAsync(long itemId, string apiNamespace,
        CancellationToken cancellationToken) =>
        await GetAsync($"/data/wow/item/{itemId}", apiNamespace, cancellationToken);

    public async Task<JsonDocument?> GetItemMediaAsync(long itemId, string apiNamespace,
        CancellationToken cancellationToken) =>
        await GetAsync($"/data/wow/media/item/{itemId}", apiNamespace, cancellationToken);

    public async Task<IReadOnlyList<long>> DiscoverItemIdsAsync(string apiNamespace,
        CancellationToken cancellationToken)
    {
        var ids = new SortedSet<long>();
        var minimumId = 1L;
        var batch = 0;
        while (minimumId <= int.MaxValue)
        {
            batch++;
            using var document = await GetAsync("/data/wow/search/item", apiNamespace, cancellationToken,
                $"id=[{minimumId},{int.MaxValue}]&_page=1&_pageSize=1000&orderby=id");
            if (document is null || !document.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                break;
            var previousCount = ids.Count;
            var greatestId = minimumId - 1;
            foreach (var result in results.EnumerateArray())
            {
                if (result.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var id) &&
                    id.TryGetInt64(out var value) && value > 0)
                {
                    ids.Add(value);
                    greatestId = Math.Max(greatestId, value);
                }
            }
            Console.Write($"\rDiscovering item IDs: batch {batch:N0} · {ids.Count:N0} items");
            if (results.GetArrayLength() == 0)
                break;
            if (ids.Count == previousCount || greatestId < minimumId)
                throw new InvalidDataException("Blizzard item search did not advance to the next ID range.");
            minimumId = greatestId + 1;
        }
        Console.WriteLine();
        return ids.ToArray();
    }

    private async Task<JsonDocument?> GetAsync(string path, string apiNamespace,
        CancellationToken cancellationToken, string? additionalQuery = null)
    {
        for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
        {
            await ThrottleAsync(cancellationToken);
            var token = await GetTokenAsync(cancellationToken);
            var uri = new UriBuilder("https", $"{_region}.api.blizzard.com")
            {
                Path = path,
                Query = $"namespace={Uri.EscapeDataString(apiNamespace)}&locale={Uri.EscapeDataString(_locale)}" +
                        (string.IsNullOrWhiteSpace(additionalQuery) ? string.Empty : $"&{additionalQuery}")
            }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is HttpRequestException ||
                exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt == _maximumAttempts)
                    throw;
                var delay = TimeSpan.FromSeconds(30);
                Console.Error.WriteLine($"Blizzard API request failed; retrying at {DateTimeOffset.Now.Add(delay):T}.");
                await Task.Delay(delay, cancellationToken);
                continue;
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt < _maximumAttempts)
                {
                    _accessToken = null;
                    continue;
                }
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                }
                if (attempt == _maximumAttempts || !ShouldRetry(response.StatusCode))
                    throw new HttpRequestException(
                        $"Blizzard API returned {(int)response.StatusCode} {response.ReasonPhrase} for {path}.",
                        null, response.StatusCode);
                var delay = response.Headers.RetryAfter?.Delta ??
                            response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ??
                            (response.StatusCode == HttpStatusCode.TooManyRequests
                                ? TimeSpan.FromMinutes(1)
                                : TimeSpan.FromSeconds(30));
                if (delay <= TimeSpan.Zero)
                    delay = TimeSpan.FromSeconds(30);
                if (delay > TimeSpan.FromMinutes(15))
                    delay = TimeSpan.FromMinutes(15);
                Console.Error.WriteLine(
                    $"Blizzard API returned {(int)response.StatusCode}; pausing until {DateTimeOffset.Now.Add(delay):T}.");
                await Task.Delay(delay, cancellationToken);
            }
        }
        throw new HttpRequestException("Blizzard API request failed.");
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return _accessToken;
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return _accessToken;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.battle.net/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials"
                })
            };
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            _accessToken = document.RootElement.GetProperty("access_token").GetString() ??
                           throw new InvalidDataException("The OAuth response did not contain an access token.");
            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expiry)
                ? expiry.GetInt32()
                : 86400;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset scheduledAt;
        await _requestScheduleLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            scheduledAt = _lastRequestAt + _minimumRequestSpacing;
            if (scheduledAt < now)
                scheduledAt = now;
            _lastRequestAt = scheduledAt;
        }
        finally
        {
            _requestScheduleLock.Release();
        }
        var delay = scheduledAt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout || statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    public void Dispose()
    {
        _requestScheduleLock.Dispose();
        _tokenLock.Dispose();
        _http.Dispose();
    }
}
