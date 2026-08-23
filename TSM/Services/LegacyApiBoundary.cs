using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TSM.Models;

namespace TSM.Services;

public interface ITsmApiClient
{
    bool IsAuthenticated { get; }
    bool IsPremium { get; }
    bool SupportsEndpoint(string endpoint);
    Task<ApiLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<TsmStatusResponse> GetStatusAsync(string channel, CancellationToken cancellationToken = default);
    Task<string> DownloadDataAsync(string url, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadAddonAsync(string name, string channel, string version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TsmRealmChoice>> GetAvailableRealmsAsync(CancellationToken cancellationToken = default);
    Task AddRealmAsync(string gameVersion, string realmId, CancellationToken cancellationToken = default);
    Task RemoveRealmAsync(string gameVersion, string region, string realmName,
        CancellationToken cancellationToken = default);
    Task<long> GetLastUploadAsync(string endpoint, IReadOnlyList<string> path,
        CancellationToken cancellationToken = default);
    Task UploadAsync(TsmUploadRequest upload, CancellationToken cancellationToken = default);
    Task<TsmBackupCatalog> GetBackupsAsync(CancellationToken cancellationToken = default);
    Task<byte[]> DownloadBackupAsync(string remoteName, CancellationToken cancellationToken = default);
    Task UploadBackupAsync(string remoteName, byte[] archive, CancellationToken cancellationToken = default);
    Task UploadLogAsync(string log, bool crash = false, CancellationToken cancellationToken = default);
    void Logout();
}

public sealed record ApiLoginResult(bool Success, string Message);

public sealed class TsmLegacyApiClient : ITsmApiClient, IDisposable
{
    internal const int ApplicationVersion = 41402;
    private const int MaximumDownloadedCharacters = 100_000_000;
    private const string RequestSigningSeed = "3FB1CC5EDC5B43F21CB8ACC23B42B703";
    private static readonly Uri OidcEndpoint = new(
        "https://id.tradeskillmaster.com/realms/app/protocol/openid-connect/token");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    private readonly HttpClient _httpClient;
    private readonly Func<bool> _allowInsecureLegacyTransport;
    private readonly Func<long> _unixTime;
    private readonly bool _ownsClient;
    private string _session = string.Empty;
    private bool _isPremium;
    private Dictionary<string, string> _endpointSubdomains = new(StringComparer.OrdinalIgnoreCase);

    public TsmLegacyApiClient(
        Func<bool> allowInsecureLegacyTransport,
        HttpClient? httpClient = null,
        Func<long>? unixTime = null)
    {
        _allowInsecureLegacyTransport = allowInsecureLegacyTransport;
        _unixTime = unixTime ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None ||
                IsTsmHost(request.RequestUri?.Host)
        });
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"TSMApplication/{ApplicationVersion}");
    }

    public bool IsAuthenticated => _session.Length > 0;
    public bool IsPremium => _isPremium;
    public bool SupportsEndpoint(string endpoint) => endpoint is "auth" or "log" or "realms2" ||
                                                     _endpointSubdomains.ContainsKey(endpoint);

    public async Task<ApiLoginResult> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!_allowInsecureLegacyTransport())
            return new ApiLoginResult(false,
                "Legacy account sync is disabled. Review and enable it in Settings → Advanced first.");

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return new ApiLoginResult(false, "Enter both an email address and password.");

        try
        {
            using var tokenResponse = await _httpClient.PostAsync(OidcEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = email.Trim(),
                    ["password"] = password,
                    ["client_id"] = "legacy-desktop-app",
                    ["grant_type"] = "password",
                    ["code"] = string.Empty,
                    ["redirect_uri"] = string.Empty,
                    ["scope"] = "openid"
                }), cancellationToken);

            if (tokenResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                return new ApiLoginResult(false, "Invalid email address or password.");
            tokenResponse.EnsureSuccessStatusCode();
            var token = await DeserializeAsync<OidcTokenResponse>(tokenResponse, cancellationToken);
            if (string.IsNullOrWhiteSpace(token.AccessToken))
                return new ApiLoginResult(false, "The identity service returned no access token.");

            using var sessionResponse = await SendApiAsync(HttpMethod.Post, "app-server", ["auth"],
                new { token = token.AccessToken }, string.Empty, cancellationToken);
            sessionResponse.EnsureSuccessStatusCode();
            var userInfo = await DeserializeAsync<UserInfoResponse>(sessionResponse, cancellationToken);
            if (string.IsNullOrWhiteSpace(userInfo.Session))
                return new ApiLoginResult(false, "The TSM service returned no session.");

            _session = userInfo.Session;
            _isPremium = userInfo.IsPremium;
            _endpointSubdomains = userInfo.EndpointSubdomains ?? new Dictionary<string, string>();
            return new ApiLoginResult(true, userInfo.IsPremium ? "Signed in · Premium" : "Signed in");
        }
        catch (HttpRequestException exception)
        {
            Logout();
            return new ApiLoginResult(false, $"Authentication failed: {SafeMessage(exception)}");
        }
        catch (JsonException)
        {
            Logout();
            return new ApiLoginResult(false, "Authentication failed: the server returned an invalid response.");
        }
    }

    public async Task<TsmStatusResponse> GetStatusAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        EnsureLegacyApiAvailable();
        var subdomain = ResolveSubdomain("status");
        using var response = await SendApiAsync(HttpMethod.Get, subdomain, ["status"], null,
            NormalizeChannel(channel), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<TsmStatusResponse>(response, cancellationToken);
    }

    public async Task<string> DownloadDataAsync(string url, CancellationToken cancellationToken = default)
    {
        EnsureLegacyApiAvailable();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("The data service returned an invalid download URL.");
        if (uri.Scheme == Uri.UriSchemeHttp)
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback)
            throw new InvalidOperationException("Only remote HTTPS data downloads are allowed.");

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        Stream input = responseStream;
        var prefix = new byte[2];
        var prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            var read = await responseStream.ReadAsync(prefix.AsMemory(prefixLength), cancellationToken);
            if (read == 0)
                break;
            prefixLength += read;
        }
        var combined = new PrefixStream(prefix.AsMemory(0, prefixLength), responseStream);
        if (prefixLength == 2 && prefix[0] == 0x1f && prefix[1] == 0x8b)
            input = new GZipStream(combined, CompressionMode.Decompress);
        else
            input = combined;

        using var reader = new StreamReader(input, new UTF8Encoding(false, true), true, 8192, false);
        var result = new StringBuilder();
        var characters = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(characters, cancellationToken);
            if (read == 0)
                break;
            if (result.Length + read > MaximumDownloadedCharacters)
                throw new InvalidDataException("Downloaded data exceeded the 100 MB safety limit.");
            result.Append(characters, 0, read);
        }
        return result.ToString();
    }

    public async Task<byte[]> DownloadAddonAsync(
        string name,
        string channel,
        string version,
        CancellationToken cancellationToken = default)
    {
        EnsureLegacyApiAvailable();
        if (string.IsNullOrWhiteSpace(name) || name.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new InvalidOperationException("The service returned an invalid addon name.");

        var subdomain = ResolveSubdomain("addon");
        using var response = await SendApiAsync(HttpMethod.Get, subdomain, ["addon", name], null,
            NormalizeChannel(channel), cancellationToken, version.TrimStart('v', 'V'));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (payload.Length >= 4 && payload[0] == (byte)'P' && payload[1] == (byte)'K')
            return payload;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            foreach (var propertyName in new[] { "url", "download_url", "downloadUrl", "zip_url", "zipUrl" })
            {
                if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                    continue;
                return await DownloadBinaryAsync(property.GetString()!, cancellationToken);
            }
        }
        catch (JsonException)
        {
        }
        throw new InvalidDataException("The addon service returned neither a ZIP archive nor a download URL.");
    }

    public async Task<IReadOnlyList<TsmRealmChoice>> GetAvailableRealmsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureLegacyApiAvailable();
        using var response = await SendApiAsync(HttpMethod.Get, "app-server", ["realms2", "list"], null,
            string.Empty, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var result = new List<TsmRealmChoice>();
        if (!CollectRealmChoices(document.RootElement, result, string.Empty, string.Empty, true))
            throw new InvalidDataException("The realm service returned no realm lists.");
        return result.DistinctBy(value => (value.GameVersion.ToUpperInvariant(), value.Id))
            .OrderBy(value => value.GameLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Region, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool CollectRealmChoices(
        JsonElement element,
        ICollection<TsmRealmChoice> result,
        string inheritedGameVersion,
        string inheritedRegion,
        bool listContext)
    {
        var foundRealmList = false;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foundRealmList = listContext || inheritedGameVersion.Length > 0;
            foreach (var entry in element.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                var gameVersion = inheritedGameVersion;
                if (gameVersion.Length == 0 && TryReadString(entry, out var entryVersion,
                        "gameVersion", "game_version", "game", "version", "flavor"))
                    gameVersion = NormalizeGameVersion(entryVersion);
                var label = GameVersionLabel(gameVersion);
                if (label.Length > 0 && TryReadStringOrNumber(entry, out var id,
                        "id", "realmId", "realm_id", "serverId", "server_id") &&
                    TryReadString(entry, out var name, "name", "realmName", "realm_name"))
                {
                    var region = TryReadString(entry, out var entryRegion, "region")
                        ? entryRegion
                        : inheritedRegion;
                    result.Add(new TsmRealmChoice(gameVersion, label, region, name, id));
                }
                else
                {
                    foundRealmList |= CollectRealmChoices(entry, result, gameVersion, inheritedRegion, listContext);
                }
            }
            return foundRealmList;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            var propertyGameVersion = NormalizeGameVersion(property.Name);
            var gameVersion = propertyGameVersion.Length > 0 ? propertyGameVersion : inheritedGameVersion;
            var region = IsRegion(property.Name) ? property.Name.ToUpperInvariant() : inheritedRegion;
            var childListContext = listContext || propertyGameVersion.Length > 0 ||
                                   property.Name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                                   property.Name.Equals("realms", StringComparison.OrdinalIgnoreCase) ||
                                   property.Name.Equals("available", StringComparison.OrdinalIgnoreCase) ||
                                   property.Name.Equals("realmLists", StringComparison.OrdinalIgnoreCase);
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                foundRealmList |= CollectRealmChoices(property.Value, result, gameVersion, region, childListContext);
            }
        }
        return foundRealmList;
    }

    private static string NormalizeGameVersion(string value) => value.Trim().ToLowerInvariant() switch
    {
        "retail" => "retail",
        "bcc" => "bcc",
        "tbc" => "tbc",
        "progression" => "progression",
        "classic" => "classic",
        "vanilla" => "vanilla",
        "classic_era" => "classic_era",
        "anniversary" => "anniversary",
        "mop" => "mop",
        "mists" => "mists",
        "classic_mop" => "classic_mop",
        _ => string.Empty
    };

    private static string GameVersionLabel(string gameVersion) => gameVersion switch
    {
        "retail" => "Retail",
        "bcc" or "tbc" or "progression" => "Progression",
        "classic" or "vanilla" or "classic_era" => "Classic Era",
        "anniversary" => "Anniversary",
        "mop" or "mists" or "classic_mop" => "Mists of Pandaria Classic",
        _ => string.Empty
    };

    private static bool IsRegion(string value) => value.Equals("US", StringComparison.OrdinalIgnoreCase) ||
                                                   value.Equals("EU", StringComparison.OrdinalIgnoreCase) ||
                                                   value.Equals("KR", StringComparison.OrdinalIgnoreCase) ||
                                                   value.Equals("TW", StringComparison.OrdinalIgnoreCase) ||
                                                   value.Equals("CN", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadString(JsonElement element, out string value, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String)
                continue;
            value = property.Value.GetString() ?? string.Empty;
            return value.Length > 0;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryReadStringOrNumber(JsonElement element, out string value, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => string.Empty
            };
            return value.Length > 0;
        }
        value = string.Empty;
        return false;
    }

    public async Task AddRealmAsync(
        string gameVersion,
        string realmId,
        CancellationToken cancellationToken = default) =>
        await UpdateRealmSelectionAsync("add", gameVersion, realmId, cancellationToken);

    public async Task RemoveRealmAsync(
        string gameVersion,
        string region,
        string realmName,
        CancellationToken cancellationToken = default)
    {
        EnsureLegacyApiAvailable();
        if (!AllowedGameVersions.Contains(gameVersion) || string.IsNullOrWhiteSpace(region) ||
            string.IsNullOrWhiteSpace(realmName) || !SafeRealmPart(region) || !SafeRealmPart(realmName))
            throw new InvalidOperationException("Invalid realm selection.");
        await SendRealmSelectionAsync(["realms2", "remove", gameVersion, region, realmName], cancellationToken);
    }

    public async Task<long> GetLastUploadAsync(string endpoint, IReadOnlyList<string> path,
        CancellationToken cancellationToken = default)
    {
        EnsureUploadEndpoint(endpoint);
        using var response = await SendApiAsync(HttpMethod.Get, ResolveSubdomain(endpoint),
            [endpoint, .. path], null, string.Empty, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("lastUpload", out var value) && value.TryGetInt64(out var timestamp)
            ? timestamp
            : 0;
    }

    public async Task UploadAsync(TsmUploadRequest upload, CancellationToken cancellationToken = default)
    {
        EnsureUploadEndpoint(upload.Endpoint);
        using var response = await SendApiAsync(HttpMethod.Post, ResolveSubdomain(upload.Endpoint),
            [upload.Endpoint, .. upload.Path], upload.Data, string.Empty, cancellationToken,
            contentType: upload.BinaryContent ? "application/octet-stream" : "application/json");
        response.EnsureSuccessStatusCode();
    }

    public async Task<TsmBackupCatalog> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        EnsureLegacyApiAvailable();
        using var response = await SendApiAsync(HttpMethod.Get, ResolveSubdomain("backup"), ["backup"], null,
            string.Empty, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var backups = new List<TsmRemoteBackup>();
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        foreach (var group in data.EnumerateObject())
        {
            var separator = group.Name.IndexOf('_');
            if (separator <= 0 || group.Value.ValueKind != JsonValueKind.Array)
                continue;
            var systemId = group.Name[..separator];
            var account = group.Name[(separator + 1)..];
            foreach (var entry in group.Value.EnumerateArray())
            {
                if (!entry.TryGetProperty("timestamp", out var timestamp) || !timestamp.TryGetInt64(out var value))
                    continue;
                var keep = entry.TryGetProperty("keep", out var keepValue) && keepValue.ValueKind == JsonValueKind.True;
                backups.Add(new TsmRemoteBackup(systemId, account, value, keep));
            }
        }
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("missing", out var missingValue) &&
            missingValue.ValueKind == JsonValueKind.Array)
        foreach (var entry in missingValue.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } name)
                missing.Add(name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name : name + ".zip");
        }
        return new TsmBackupCatalog(backups, missing);
    }

    public async Task<byte[]> DownloadBackupAsync(string remoteName, CancellationToken cancellationToken = default)
    {
        ValidateArchiveName(remoteName);
        using var response = await SendApiAsync(HttpMethod.Get, ResolveSubdomain("backup"),
            ["backup", EncodePath(remoteName)], null, string.Empty, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task UploadBackupAsync(string remoteName, byte[] archive,
        CancellationToken cancellationToken = default)
    {
        ValidateArchiveName(remoteName);
        using var response = await SendApiAsync(HttpMethod.Post, ResolveSubdomain("backup"),
            ["backup", EncodePath(remoteName)], archive, string.Empty, cancellationToken,
            contentType: "application/octet-stream");
        response.EnsureSuccessStatusCode();
    }

    public async Task UploadLogAsync(string log, bool crash = false, CancellationToken cancellationToken = default)
    {
        EnsureLegacyApiAvailable();
        using var response = await SendApiAsync(HttpMethod.Post, "app-server",
            ["log", crash ? "crash" : "user"], log, string.Empty, cancellationToken,
            contentType: "text/plain");
        response.EnsureSuccessStatusCode();
    }

    private async Task UpdateRealmSelectionAsync(
        string action,
        string gameVersion,
        string realmId,
        CancellationToken cancellationToken)
    {
        EnsureLegacyApiAvailable();
        if (!AllowedGameVersions.Contains(gameVersion) || string.IsNullOrWhiteSpace(realmId) ||
            realmId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new InvalidOperationException("Invalid realm selection.");
        await SendRealmSelectionAsync(["realms2", action, gameVersion, realmId], cancellationToken);
    }

    private async Task SendRealmSelectionAsync(IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(HttpMethod.Get, "app-server", path, null, string.Empty,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (payload.Length == 0)
            return;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : "The realm service rejected the selection.";
                throw new InvalidOperationException(message);
            }
        }
        catch (JsonException)
        {
        }
    }

    private static bool SafeRealmPart(string value) => value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ' ' or '\'');

    public void Logout()
    {
        _session = string.Empty;
        _isPremium = false;
        _endpointSubdomains.Clear();
    }

    public void Dispose()
    {
        Logout();
        if (_ownsClient)
            _httpClient.Dispose();
    }

    private async Task<HttpResponseMessage> SendApiAsync(
        HttpMethod method,
        string subdomain,
        IReadOnlyList<string> parts,
        object? body,
        string channel,
        CancellationToken cancellationToken,
        string tsmVersion = "",
        string contentType = "application/json")
    {
        EnsureAllowedSubdomain(subdomain);
        var timestamp = _unixTime();
        var signatureSource = $"{ApplicationVersion}:{timestamp}:{RequestSigningSeed}";
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatureSource))).ToLowerInvariant();
        var path = string.Join('/', parts.Select(Uri.EscapeDataString));
        var parameters = new Dictionary<string, string>
        {
            ["session"] = _session,
            ["version"] = ApplicationVersion.ToString(),
            ["time"] = timestamp.ToString(),
            ["token"] = signature
        };
        if (channel.Length > 0)
            parameters["channel"] = channel;
        if (tsmVersion.Length > 0)
            parameters["tsm_version"] = tsmVersion;
        var query = string.Join('&', parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var uri = new Uri($"http://{subdomain}.tradeskillmaster.com/v2/{path}?{query}");
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            var raw = body switch
            {
                byte[] bytes => bytes,
                string text => Encoding.UTF8.GetBytes(text),
                _ => JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions)
            };
            if (body is byte[])
                request.Content = new ByteArrayContent(raw);
            else
            {
                using var buffer = new MemoryStream();
                using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true))
                    gzip.Write(raw);
                request.Content = new ByteArrayContent(buffer.ToArray());
                request.Content.Headers.ContentEncoding.Add("gzip");
            }
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<byte[]> DownloadBinaryAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidDataException("The addon service returned an invalid download URL.");
        if (uri.Scheme == Uri.UriSchemeHttp)
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback)
            throw new InvalidDataException("Only remote HTTPS addon downloads are allowed.");
        return await _httpClient.GetByteArrayAsync(uri, cancellationToken);
    }

    private string ResolveSubdomain(string endpoint)
    {
        if (!_endpointSubdomains.TryGetValue(endpoint, out var subdomain))
            throw new InvalidOperationException($"The authenticated session did not enable the '{endpoint}' endpoint.");
        EnsureAllowedSubdomain(subdomain);
        return subdomain;
    }

    private void EnsureLegacyApiAvailable()
    {
        if (!_allowInsecureLegacyTransport())
            throw new InvalidOperationException("Legacy private API access is disabled in Settings.");
        if (!IsAuthenticated)
            throw new InvalidOperationException("Sign in before synchronizing account data.");
    }

    private void EnsureAllowedSubdomain(string subdomain)
    {
        if (subdomain.Length is < 1 or > 63 ||
            subdomain.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new InvalidOperationException("The service returned an invalid endpoint name.");
    }

    private static void EnsureUploadEndpoint(string endpoint)
    {
        if (!UploadEndpoints.Contains(endpoint))
            throw new InvalidOperationException("Unsupported TSM upload endpoint.");
    }

    private static string EncodePath(string value) => Convert.ToBase64String(Encoding.ASCII.GetBytes(value))
        .Replace('+', '-').Replace('/', '_');

    private static void ValidateArchiveName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 ||
            !value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '#')))
            throw new InvalidOperationException("Invalid backup archive name.");
    }

    private static bool IsTsmHost(string? host) => host is not null &&
        (host.Equals("tradeskillmaster.com", StringComparison.OrdinalIgnoreCase) ||
         host.EndsWith(".tradeskillmaster.com", StringComparison.OrdinalIgnoreCase));

    private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
               ?? throw new JsonException("Empty JSON response.");
    }

    private static string NormalizeChannel(string channel) =>
        string.Equals(channel, "Beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "release";

    private static IReadOnlySet<string> AllowedGameVersions { get; } =
        new HashSet<string>(
            ["retail", "bcc", "tbc", "progression", "classic", "vanilla", "classic_era", "anniversary", "mop", "mists", "classic_mop"],
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> UploadEndpoints { get; } =
        new HashSet<string>(["auctiondb_scan", "black_market", "sales", "groups", "analytics", "error_report", "sv"],
            StringComparer.Ordinal);

    private static string SafeMessage(HttpRequestException exception) =>
        exception.StatusCode is { } statusCode ? $"server returned {(int)statusCode}." : "could not reach the service.";

    private sealed class OidcTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;
    }

    private sealed class UserInfoResponse
    {
        [JsonPropertyName("session")]
        public string Session { get; init; } = string.Empty;

        [JsonPropertyName("isPremium")]
        public bool IsPremium { get; init; }

        [JsonPropertyName("endpointSubdomains")]
        public Dictionary<string, string>? EndpointSubdomains { get; init; }
    }

    private sealed class PrefixStream(ReadOnlyMemory<byte> prefix, Stream remainder) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var copied = CopyPrefix(buffer.AsSpan(offset, count));
            return copied > 0 ? copied : remainder.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var copied = CopyPrefix(buffer.Span);
            return copied > 0 ? copied : await remainder.ReadAsync(buffer, cancellationToken);
        }

        private int CopyPrefix(Span<byte> destination)
        {
            var count = Math.Min(destination.Length, prefix.Length - _position);
            if (count <= 0)
                return 0;
            prefix.Span.Slice(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
