using System.Text.Json;
using YFex.Security.Events;

namespace YFex.Security.ApiReverser;

public sealed record AuthFlowRecord
{
    public string AuthType { get; init; } = "";
    public string? TokenEndpoint { get; init; }
    public string? AuthorizeEndpoint { get; init; }
    public string? LoginEndpoint { get; init; }
    public string[] Scopes { get; init; } = [];
    public string? ClientId { get; init; }
    public string? TokenHeaderName { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>
/// [PERSONAL USE] Detects the authentication flow from a chronological HTTP capture
/// sequence using ordered heuristics: OAuth2/OIDC, cookie session, API key, then custom
/// token-in-body → header.
/// </summary>
public sealed class AuthFlowExtractor
{
    public AuthFlowRecord? ExtractAuthFlow(IReadOnlyList<HttpCaptureEvent> chronologicalCaptures)
    {
        var captures = chronologicalCaptures.OrderBy(c => c.Timestamp).ToList();
        if (captures.Count == 0) return null;

        return TryOAuth2(captures)
            ?? TryCookie(captures)
            ?? TryApiKey(captures)
            ?? TryCustomToken(captures);
    }

    private static AuthFlowRecord? TryOAuth2(List<HttpCaptureEvent> captures)
    {
        var authorize = captures.FirstOrDefault(c =>
            c.Path.Contains("/authorize", StringComparison.OrdinalIgnoreCase) ||
            c.Path.Contains("/oauth/authorize", StringComparison.OrdinalIgnoreCase));

        var token = captures.FirstOrDefault(c =>
            c.Path.Contains("/token", StringComparison.OrdinalIgnoreCase) &&
            (c.RequestBody?.Contains("grant_type", StringComparison.OrdinalIgnoreCase) == true ||
             c.QueryString.Contains("grant_type", StringComparison.OrdinalIgnoreCase)));

        if (token is null && authorize is null) return null;

        string? clientId = ExtractParam(authorize?.QueryString, "client_id")
            ?? ExtractParam(token?.RequestBody, "client_id");
        string? scopeRaw = ExtractParam(authorize?.QueryString, "scope")
            ?? ExtractParam(token?.RequestBody, "scope");

        return new AuthFlowRecord
        {
            AuthType = "OAuth2/OIDC",
            AuthorizeEndpoint = authorize?.Url,
            TokenEndpoint = token?.Url,
            ClientId = clientId,
            Scopes = scopeRaw?.Split(' ', '+', StringSplitOptions.RemoveEmptyEntries) ?? [],
            TokenHeaderName = "Authorization",
            Description = "OAuth2 authorization code / token exchange; bearer token sent in Authorization header."
        };
    }

    private static AuthFlowRecord? TryCookie(List<HttpCaptureEvent> captures)
    {
        var login = captures.FirstOrDefault(c =>
            c.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            (c.Path.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
             c.Path.Contains("/signin", StringComparison.OrdinalIgnoreCase) ||
             c.Path.Contains("/session", StringComparison.OrdinalIgnoreCase)) &&
            c.ResponseHeaders is not null &&
            c.ResponseHeaders.Keys.Any(k => k.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)));

        if (login is null) return null;

        return new AuthFlowRecord
        {
            AuthType = "Cookie",
            LoginEndpoint = login.Url,
            TokenHeaderName = "Cookie",
            Description = "Form/JSON login returns Set-Cookie; subsequent requests carry the session cookie."
        };
    }

    private static AuthFlowRecord? TryApiKey(List<HttpCaptureEvent> captures)
    {
        foreach (var capture in captures)
        {
            var apiKeyHeader = capture.RequestHeaders.Keys.FirstOrDefault(k =>
                k.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("Api-Key", StringComparison.OrdinalIgnoreCase));

            if (apiKeyHeader is not null)
            {
                return new AuthFlowRecord
                {
                    AuthType = "ApiKey",
                    TokenHeaderName = apiKeyHeader,
                    Description = $"Static API key passed in '{apiKeyHeader}' header."
                };
            }

            if (capture.QueryString.Contains("api_key=", StringComparison.OrdinalIgnoreCase) ||
                capture.QueryString.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
            {
                return new AuthFlowRecord
                {
                    AuthType = "ApiKey",
                    TokenHeaderName = "query:api_key",
                    Description = "API key passed as a query string parameter."
                };
            }
        }

        return null;
    }

    private static AuthFlowRecord? TryCustomToken(List<HttpCaptureEvent> captures)
    {
        // Early POST returns a token in body that later appears in a custom header.
        for (int i = 0; i < captures.Count; i++)
        {
            var body = captures[i].ResponseBody;
            if (string.IsNullOrEmpty(body)) continue;

            string? token = ExtractJsonValue(body, "token")
                ?? ExtractJsonValue(body, "access_token")
                ?? ExtractJsonValue(body, "accessToken");

            if (token is null || token.Length < 8) continue;

            for (int j = i + 1; j < captures.Count; j++)
            {
                foreach (var (header, value) in captures[j].RequestHeaders)
                {
                    if (value.Contains(token, StringComparison.Ordinal))
                    {
                        return new AuthFlowRecord
                        {
                            AuthType = "CustomToken",
                            LoginEndpoint = captures[i].Url,
                            TokenHeaderName = header,
                            Description = $"Login response token replayed in '{header}' header."
                        };
                    }
                }
            }
        }

        return null;
    }

    private static string? ExtractParam(string? source, string key)
    {
        if (string.IsNullOrEmpty(source)) return null;
        foreach (var pair in source.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    private static string? ExtractJsonValue(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(key, out var v) &&
                v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
