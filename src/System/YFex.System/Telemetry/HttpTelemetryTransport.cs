using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace YFex.System.Telemetry;

public sealed class HttpTelemetryTransport : ITelemetryTransport
{
    private readonly HttpClient _client;
    private readonly TelemetryOptions _options;

    public HttpTelemetryTransport(IOptions<TelemetryOptions> options)
    {
        _options = options.Value;
        _client = new HttpClient
        {
            BaseAddress = _options.Endpoint,
            Timeout = _options.HttpTimeout
        };
    }

    public async Task<bool> ShipBatchAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken ct)
    {
        try
        {
            using var response = await _client.PostAsJsonAsync(
                $"v1/events/{_options.AppId}",
                batch,
                ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
