namespace Incursa.Integrations.WorkOS.Core.Clients;

using System.Net.Http.Headers;
using Incursa.Integrations.WorkOS.Abstractions.Configuration;
using Incursa.Integrations.WorkOS.Abstractions.Management;
using Incursa.Integrations.WorkOS.Abstractions.Mapping;

public sealed class WorkOsManagementHttpClient : IWorkOsManagementClient
{
    private readonly HttpClient _httpClient;
    private readonly IWorkOsTenantMapper _tenantMapper;

    public WorkOsManagementHttpClient(HttpClient httpClient, WorkOsManagementOptions options, IWorkOsTenantMapper tenantMapper)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tenantMapper);

        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _httpClient.Timeout = options.RequestTimeout;
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _tenantMapper = tenantMapper;
    }

    public async ValueTask<WorkOsManagedApiKey?> ValidateApiKeyAsync(string presentedKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presentedKey))
        {
            return null;
        }

        var response = await PostJsonAsync("api_keys/validations", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["value"] = presentedKey,
        }, ct).ConfigureAwait(false);

        if (response is null)
        {
            return null;
        }

        if (!response.RootElement.TryGetProperty("api_key", out var apiKeyElement))
        {
            return null;
        }

        if (apiKeyElement.ValueKind == JsonValueKind.Null || apiKeyElement.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (apiKeyElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var keyId = TryGetString(apiKeyElement, "id") ?? TryGetString(apiKeyElement, "api_key_id") ?? string.Empty;
        var orgId = TryGetString(apiKeyElement, "organization_id")
            ?? TryGetOwnerOrganizationId(apiKeyElement)
            ?? string.Empty;
        var displayName = TryGetString(apiKeyElement, "name") ?? "api-key";

        List<string> permissions = [];
        if (apiKeyElement.TryGetProperty("permissions", out var permissionsElement) && permissionsElement.ValueKind == JsonValueKind.Array)
        {
            permissions.AddRange(permissionsElement.EnumerateArray().Where(static x => x.ValueKind == JsonValueKind.String).Select(static x => x.GetString() ?? string.Empty).Where(static x => !string.IsNullOrWhiteSpace(x)));
        }

        return new WorkOsManagedApiKey(
            ApiKeyId: keyId,
            OrganizationId: orgId,
            DisplayName: displayName,
            CreatedUtc: TryGetDateTimeOffset(apiKeyElement, "created_at") ?? DateTimeOffset.UtcNow,
            ExpiresUtc: TryGetDateTimeOffset(apiKeyElement, "expires_at"),
            RevokedUtc: TryGetDateTimeOffset(apiKeyElement, "revoked_at"),
            Permissions: permissions);
    }

    public async ValueTask<WorkOsCreatedApiKey> CreateApiKeyAsync(string organizationId, string displayName, IReadOnlyCollection<string> scopes, int? ttlHours, CancellationToken ct = default)
    {
        Dictionary<string, object?> requestBody = new(StringComparer.Ordinal)
        {
            ["name"] = displayName,
            ["permissions"] = scopes,
        };

        if (ttlHours is > 0)
        {
            requestBody["expires_at"] = DateTimeOffset.UtcNow.AddHours(ttlHours.Value).ToString("O", CultureInfo.InvariantCulture);
        }

        var response = await PostJsonAsync(
            $"organizations/{Uri.EscapeDataString(organizationId)}/api_keys",
            requestBody,
            throwOnFailure: true,
            ct).ConfigureAwait(false);

        if (response is null)
        {
            throw new InvalidOperationException("WorkOS did not return api key payload.");
        }

        var payload = UnwrapData(response.RootElement);
        var tenantId = await _tenantMapper.GetTenantIdAsync(organizationId, ct).ConfigureAwait(false) ?? string.Empty;
        return new WorkOsCreatedApiKey(
            ApiKeyId: TryGetFirstString(payload, "id", "api_key_id") ?? string.Empty,
            DisplayName: TryGetString(payload, "name") ?? displayName,
            CreatedUtc: TryGetDateTimeOffset(payload, "created_at") ?? DateTimeOffset.UtcNow,
            ExpiresUtc: TryGetDateTimeOffset(payload, "expires_at"),
            EffectiveScopes: scopes.Select(static x => x.ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            OrganizationId: organizationId,
            TenantId: tenantId,
            Secret: TryGetCreateSecret(payload) ?? string.Empty);
    }

    public async IAsyncEnumerable<WorkOsApiKeySummary> ListApiKeysAsync(string organizationId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"organizations/{Uri.EscapeDataString(organizationId)}/api_keys", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!payload.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var tenantId = await _tenantMapper.GetTenantIdAsync(organizationId, ct).ConfigureAwait(false) ?? string.Empty;
        foreach (var item in data.EnumerateArray())
        {
            yield return ParseSummary(item, organizationId, tenantId);
        }
    }

    public async ValueTask<WorkOsApiKeySummary?> GetApiKeyAsync(string organizationId, string apiKeyId, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"organizations/{Uri.EscapeDataString(organizationId)}/api_keys/{Uri.EscapeDataString(apiKeyId)}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var tenantId = await _tenantMapper.GetTenantIdAsync(organizationId, ct).ConfigureAwait(false) ?? string.Empty;
        return ParseSummary(payload.RootElement, organizationId, tenantId);
    }

    public async ValueTask RevokeApiKeyAsync(string organizationId, string apiKeyId, CancellationToken ct = default)
    {
        using var response = await _httpClient.PostAsync($"organizations/{Uri.EscapeDataString(organizationId)}/api_keys/{Uri.EscapeDataString(apiKeyId)}/revoke", content: null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask<bool> IsOrganizationAdminAsync(string organizationId, string subject, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"user_management/organization_memberships?organization_id={Uri.EscapeDataString(organizationId)}&user_id={Uri.EscapeDataString(subject)}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!payload.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in data.EnumerateArray())
        {
            var role = TryGetString(item, "role_slug")
                ?? (item.TryGetProperty("role", out var roleObj) ? TryGetString(roleObj, "slug") : null);

            if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "owner", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<JsonDocument?> PostJsonAsync(string path, object body, CancellationToken ct)
        => await PostJsonAsync(path, body, throwOnFailure: false, ct).ConfigureAwait(false);

    private async ValueTask<JsonDocument?> PostJsonAsync(string path, object body, bool throwOnFailure, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(path, content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (throwOnFailure)
            {
                var responseBody = response.Content is null
                    ? null
                    : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw CreateFailure(response, responseBody);
            }

            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static HttpRequestException CreateFailure(HttpResponseMessage response, string? responseBody)
    {
        var message = $"WorkOS management request failed with status code {(int)response.StatusCode}.";
        var errorCode = TryGetErrorCode(responseBody);
        if (errorCode is not null)
        {
            message += $" Error code: {errorCode}.";
        }

        var requestId = TryGetRequestId(response);
        if (requestId is not null)
        {
            message += $" Request ID: {requestId}.";
        }

        return new HttpRequestException(message, inner: null, response.StatusCode);
    }

    private static string? TryGetErrorCode(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var payload = JsonDocument.Parse(responseBody);
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var directError = TryGetSafeDiagnosticToken(payload.RootElement, "error")
                ?? TryGetSafeDiagnosticToken(payload.RootElement, "code");
            if (directError is not null)
            {
                return directError;
            }

            if (payload.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                return TryGetSafeDiagnosticToken(error, "code")
                    ?? TryGetSafeDiagnosticToken(error, "type");
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? TryGetRequestId(HttpResponseMessage response)
    {
        string[] headerNames = ["X-Request-ID", "X-WorkOS-Request-ID", "WorkOS-Request-ID"];
        foreach (var headerName in headerNames)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                var requestId = values.Select(TryGetSafeDiagnosticToken).FirstOrDefault(static value => value is not null);
                if (requestId is not null)
                {
                    return requestId;
                }
            }
        }

        return null;
    }

    private static string? TryGetSafeDiagnosticToken(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? TryGetSafeDiagnosticToken(value.GetString())
            : null;
    }

    private static string? TryGetSafeDiagnosticToken(string? value)
    {
        const int MaxDiagnosticTokenLength = 128;

        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxDiagnosticTokenLength)
        {
            return null;
        }

        if (value.StartsWith("sk_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("pk_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? value
            : null;
    }

    private static WorkOsApiKeySummary ParseSummary(JsonElement item, string organizationId, string tenantId)
    {
        List<string> scopes = [];
        if (item.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Array)
        {
            scopes.AddRange(permissions.EnumerateArray()
                .Where(static x => x.ValueKind == JsonValueKind.String)
                .Select(static x => (x.GetString() ?? string.Empty).ToLowerInvariant())
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static x => x, StringComparer.Ordinal));
        }

        return new WorkOsApiKeySummary(
            ApiKeyId: TryGetString(item, "id") ?? string.Empty,
            DisplayName: TryGetString(item, "name") ?? "api-key",
            CreatedUtc: TryGetDateTimeOffset(item, "created_at") ?? DateTimeOffset.UtcNow,
            ExpiresUtc: TryGetDateTimeOffset(item, "expires_at"),
            RevokedUtc: TryGetDateTimeOffset(item, "revoked_at"),
            EffectiveScopes: scopes,
            OrganizationId: organizationId,
            TenantId: tenantId);
    }

    private static JsonElement UnwrapData(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            return data;
        }

        return root;
    }

    private static string? TryGetCreateSecret(JsonElement payload)
    {
        return TryGetFirstString(payload, "secret", "value", "api_key", "token", "one_time_token_secret", "one_time_secret")
            ?? TryGetNestedString(payload, "one_time_token", "secret", "token", "value")
            ?? TryGetNestedString(payload, "token", "secret", "value")
            ?? TryGetNestedString(payload, "api_key", "secret", "value", "token", "api_key")
            ?? TryGetNestedString(payload, "data", "secret", "value", "token");
    }

    private static string? TryGetNestedString(JsonElement element, string objectName, params string[] propertyNames)
    {
        if (element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            return TryGetFirstString(nested, propertyNames);
        }

        return null;
    }

    private static string? TryGetFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = TryGetString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? TryGetOwnerOrganizationId(JsonElement element)
    {
        if (!element.TryGetProperty("owner", out var ownerElement) || ownerElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var ownerType = TryGetString(ownerElement, "type");
        if (!string.Equals(ownerType, "organization", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TryGetString(ownerElement, "id");
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}

