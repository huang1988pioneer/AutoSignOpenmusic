using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenMusicFlow;

internal sealed class OpenMusicClient : IDisposable
{
    public const string DefaultBaseUrl = "https://www.openmusic.ai";
    private const int SuccessCode = 200;

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();

    public OpenMusicClient(string? baseUrl = null, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var root = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(root + "/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) OpenMusicFlow/1.0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", root);
        _http.DefaultRequestHeaders.Referrer = new Uri(root + "/");
    }

    public static string Md5Hex(string password) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

    public async Task<JsonElement> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["email"] = email,
            ["password"] = Md5Hex(password),
            ["utm"] = null,
            ["source"] = null,
        };
        return await SendAsync(HttpMethod.Post, "common-api/v1/login", body, cancellationToken);
    }

    public Task<JsonElement> GetUserAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "common-api/v1/user", null, cancellationToken);

    public Task<JsonElement> GetCheckInStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "api/activity/check-in/status?entry=refresh", null, cancellationToken);

    public Task<JsonElement> ClaimAsync(object activityId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/activity/check-in/claim", new { activity_id = activityId }, cancellationToken);

    public async Task<ClaimFlowResult> RunClaimAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var log = new StringBuilder();
        var login = await LoginAsync(email, password, cancellationToken);
        log.AppendLine($"LOGIN OK {email}");

        var user = await GetUserAsync(cancellationToken);
        var userSummary = SummarizeUser(user);
        log.AppendLine("USER " + userSummary);

        JsonElement? status = null;
        try
        {
            status = await GetCheckInStatusAsync(cancellationToken);
            log.AppendLine("CHECKIN_STATUS " + Truncate(status.Value.GetRawText(), 800));
        }
        catch (ApiException exception)
        {
            log.AppendLine("CHECKIN_STATUS FAILED " + exception.Message);
        }

        if (status is { } snapshot && GetBool(snapshot, "today_checked_in"))
        {
            log.AppendLine("ALREADY CLAIMED today_checked_in=true " + userSummary);
            return new ClaimFlowResult(true, true, userSummary, userSummary, log.ToString());
        }

        var activityId = ActivityIdFromStatus(status);
        if (activityId is null)
        {
            log.AppendLine("CLAIM FAILED: no activity_id in CHECKIN_STATUS");
            throw new ApiException(3, "目前沒有可領取的簽到活動（status 沒有 activity_id）。", "api/activity/check-in/claim");
        }

        log.AppendLine($"CLAIM POST api/activity/check-in/claim activity_id={activityId}");
        var claim = await ClaimAsync(activityId, cancellationToken);
        var already = GetBool(claim, "alreadyCheckedIn");
        var after = userSummary;
        try
        {
            after = SummarizeUser(await GetUserAsync(cancellationToken));
        }
        catch (ApiException)
        {
            after = "unknown (re-fetch failed)";
        }

        if (already)
            log.AppendLine("ALREADY CLAIMED " + userSummary);
        else
        {
            log.AppendLine("CLAIMED " + Truncate(claim.GetRawText(), 300));
            log.AppendLine("BEFORE " + userSummary);
            log.AppendLine("AFTER  " + after);
        }

        return new ClaimFlowResult(true, already, userSummary, after, log.ToString());
    }

    public static string SummarizeUser(JsonElement data)
    {
        var user = data.TryGetProperty("user", out var userEl) ? userEl : default;
        var email = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("email", out var emailEl)
            ? emailEl.GetString()
            : null;
        var id = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out var idEl)
            ? idEl.ToString()
            : null;
        var total = 0;
        var buckets = 0;
        if (data.TryGetProperty("user_credit_balances", out var balances) && balances.ValueKind == JsonValueKind.Array)
        {
            foreach (var bucket in balances.EnumerateArray())
            {
                buckets++;
                if (bucket.TryGetProperty("balance", out var balance) && balance.TryGetInt32(out var value))
                    total += value;
            }
        }
        return $"email={email} id={id} credits={total} buckets={buckets}";
    }

    public static object? ActivityIdFromStatus(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } root) return null;
        if (TryGetActivityId(root, out var direct)) return direct;
        foreach (var name in new[] { "activity", "check_in", "checkin" })
        {
            if (root.TryGetProperty(name, out var nested) && TryGetActivityId(nested, out var nestedId))
                return nestedId;
        }
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object && TryGetActivityId(property.Value, out var nestedId))
                return nestedId;
        }
        return null;
    }

    private static bool TryGetActivityId(JsonElement element, out object id)
    {
        id = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("activity_id", out var value))
            return false;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        id = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : value.ToString();
        return !string.IsNullOrWhiteSpace(id.ToString());
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        }
        catch (JsonException)
        {
            throw new ApiException(-1, $"non-JSON response: {Truncate(raw, 200)}", path);
        }

        var root = document.RootElement.Clone();
        var code = root.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var parsed)
            ? parsed
            : (int)response.StatusCode;
        if (code != SuccessCode)
        {
            var message = root.TryGetProperty("message", out var messageEl)
                ? messageEl.GetString() ?? raw
                : raw;
            throw new ApiException(code, message, path);
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            return data.Clone();
        return root.Clone();
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    public void Dispose() => _http.Dispose();
}

internal sealed class ApiException(int code, string message, string path)
    : Exception($"API {path} -> code={code} message='{message}'")
{
    public int Code { get; } = code;
    public string Path { get; } = path;
}

internal sealed record ClaimFlowResult(
    bool Ok,
    bool AlreadyClaimed,
    string Before,
    string After,
    string Log);
