using System.Text.Json;
using YFex.Security.Events;
using YFex.Security.Storage;

namespace YFex.Security.ApiReverser;

/// <summary>
/// [PERSONAL USE] Analyzes captured <see cref="HttpCaptureEvent"/>s into API endpoint
/// definitions: clusters concrete paths into templates, infers required headers and auth,
/// merges request/response body shapes, and upserts into the API catalog.
/// </summary>
public sealed class HttpFlowAnalyzer
{
    private readonly IApiCatalogRepository _catalog;

    public HttpFlowAnalyzer(IApiCatalogRepository catalog)
    {
        _catalog = catalog;
    }

    public async Task AnalyzeAndCatalogAsync(
        string serviceName,
        IReadOnlyList<HttpCaptureEvent> captures,
        CancellationToken ct = default)
    {
        // 1. Group by (host, method)
        var groups = captures
            .Where(c => !string.IsNullOrEmpty(c.Host))
            .GroupBy(c => (c.Host, c.Method));

        foreach (var group in groups)
        {
            var members = group.ToList();

            // 2. Cluster concrete paths → templates
            var templates = ClusterPaths(members.Select(m => m.Path).ToList());

            foreach (var (template, indices) in templates)
            {
                var endpointCaptures = indices.Select(i => members[i]).ToList();

                // 3a. Required headers = present in >80% of requests
                var requiredHeaders = ComputeRequiredHeaders(endpointCaptures);

                // 3b. Auth type
                var (authType, authDetails) = DetectAuth(endpointCaptures);

                // 3c. Merge body schemas
                string? requestSchema = MergeJsonSchema(endpointCaptures.Select(c => c.RequestBody));
                string? responseSchema = MergeJsonSchema(endpointCaptures.Select(c => c.ResponseBody));

                var first = endpointCaptures[0];
                string now = DateTimeOffset.UtcNow.ToString("O");

                await _catalog.UpsertEndpointAsync(new ApiEndpointRecord
                {
                    ServiceName = serviceName,
                    EndpointUrl = first.Url,
                    Method = group.Key.Method,
                    Host = group.Key.Host,
                    PathTemplate = template,
                    RequiredHeaders = requiredHeaders.Count > 0
                        ? JsonSerializer.Serialize(requiredHeaders) : null,
                    AuthType = authType,
                    AuthDetails = authDetails,
                    RequestBodySchema = requestSchema,
                    ResponseBodySchema = responseSchema,
                    DiscoveredAt = now,
                    LastSeenAt = now,
                    CallCount = endpointCaptures.Count
                }, ct);
            }
        }
    }

    /// <summary>
    /// Cluster concrete paths into templates by replacing variable segments with {param_N}.
    /// A segment is variable if it parses as a GUID, an integer, or varies across >50% of
    /// the requests that share the same segment count and sibling structure.
    /// </summary>
    internal static Dictionary<string, List<int>> ClusterPaths(List<string> paths)
    {
        var result = new Dictionary<string, List<int>>();

        // Group by segment count first; templating only makes sense within equal arity.
        var bySegmentCount = new Dictionary<int, List<int>>();
        var splitCache = new List<string[]>();

        for (int i = 0; i < paths.Count; i++)
        {
            var segments = paths[i].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            splitCache.Add(segments);
            if (!bySegmentCount.TryGetValue(segments.Length, out var list))
                bySegmentCount[segments.Length] = list = [];
            list.Add(i);
        }

        foreach (var (segCount, indices) in bySegmentCount)
        {
            if (segCount == 0)
            {
                result["/"] = indices;
                continue;
            }

            // Per position, count distinct values to decide if it's a parameter.
            for (int pos = 0; pos < segCount; pos++)
            {
                // (computed lazily inside BuildTemplate)
            }

            foreach (var idx in indices)
            {
                var segments = splitCache[idx];
                var templateSegments = new string[segCount];

                for (int pos = 0; pos < segCount; pos++)
                {
                    templateSegments[pos] = IsVariableSegment(segments[pos], pos, indices, splitCache)
                        ? $"{{param_{pos}}}"
                        : segments[pos];
                }

                string template = "/" + string.Join("/", templateSegments);
                if (!result.TryGetValue(template, out var members))
                    result[template] = members = [];
                members.Add(idx);
            }
        }

        return result;
    }

    private static bool IsVariableSegment(string segment, int pos, List<int> siblingIndices, List<string[]> splitCache)
    {
        if (Guid.TryParse(segment, out _)) return true;
        if (long.TryParse(segment, out _)) return true;

        // High-cardinality detection: if this position varies a lot across siblings.
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var idx in siblingIndices)
        {
            var segs = splitCache[idx];
            if (pos < segs.Length)
            {
                distinct.Add(segs[pos]);
                count++;
            }
        }

        if (count < 4) return false;
        return distinct.Count > count * 0.5;
    }

    private static Dictionary<string, string> ComputeRequiredHeaders(List<HttpCaptureEvent> captures)
    {
        if (captures.Count == 0) return new();

        var headerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var capture in captures)
            foreach (var header in capture.RequestHeaders.Keys)
                headerCounts[header] = headerCounts.GetValueOrDefault(header) + 1;

        double threshold = captures.Count * 0.8;
        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (header, count) in headerCounts)
        {
            if (count < threshold) continue;
            if (IsVolatileHeader(header)) continue;
            // Use the most recent observed value as an example.
            string? example = captures.LastOrDefault(c => c.RequestHeaders.ContainsKey(header))
                ?.RequestHeaders[header];
            required[header] = example ?? "";
        }

        return required;
    }

    private static bool IsVolatileHeader(string header) =>
        header.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

    private static (string? Type, string? Details) DetectAuth(List<HttpCaptureEvent> captures)
    {
        foreach (var capture in captures)
        {
            foreach (var (name, value) in capture.RequestHeaders)
            {
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        return ("OAuth2/Bearer", "Authorization: Bearer <token>");
                    if (value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                        return ("Basic", "Authorization: Basic <base64>");
                    return ("Custom-Authorization", name);
                }

                if (name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Api-Key", StringComparison.OrdinalIgnoreCase))
                    return ("ApiKey", name);
            }

            if (capture.RequestHeaders.Keys.Any(k => k.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
                return ("Cookie", "Cookie-based session");
        }

        return (null, null);
    }

    /// <summary>
    /// Merge a set of JSON bodies into a union schema: { "field": "type" }. Conflicting
    /// types collapse to "any"; nested objects recurse one level.
    /// </summary>
    internal static string? MergeJsonSchema(IEnumerable<string?> bodies)
    {
        var schema = new Dictionary<string, string>();
        bool any = false;

        foreach (var body in bodies)
        {
            if (string.IsNullOrWhiteSpace(body)) continue;

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                any = true;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    string type = JsonTypeName(prop.Value);
                    if (schema.TryGetValue(prop.Name, out var existing) && existing != type)
                        schema[prop.Name] = "any";
                    else
                        schema[prop.Name] = type;
                }
            }
            catch (JsonException) { }
        }

        return any ? JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true }) : null;
    }

    private static string JsonTypeName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => "any"
    };
}
