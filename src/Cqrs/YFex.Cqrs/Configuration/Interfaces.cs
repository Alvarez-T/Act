using System.Security.Claims;

namespace YFex.Cqrs.Configuration;

// ── Validators ───────────────────────────────────────────────────────────────

public interface IQueryValidator<TQuery>
{
    ValueTask<ValidationResult> ValidateAsync(TQuery query, CancellationToken ct);
}

public interface ICommandValidator<TCommand>
{
    ValueTask<ValidationResult> ValidateAsync(TCommand command, CancellationToken ct);
}

// ── Authorizers ──────────────────────────────────────────────────────────────

public interface IQueryAuthorizer<TQuery>
{
    bool Authorize(ClaimsPrincipal user, TQuery query);
}

public interface ICommandAuthorizer<TCommand>
{
    bool Authorize(ClaimsPrincipal user, TCommand command);
}

// ── Telemetry ────────────────────────────────────────────────────────────────

public sealed class TelemetryConfigurationBuilder
{
    private bool _traceEnabled = true;
    private bool _metricsEnabled = true;
    private string? _spanName;

    public TelemetryConfigurationBuilder TraceEnabled(bool enabled = true) { _traceEnabled = enabled; return this; }
    public TelemetryConfigurationBuilder MetricsEnabled(bool enabled = true) { _metricsEnabled = enabled; return this; }
    public TelemetryConfigurationBuilder SpanName(string name) { _spanName = name; return this; }

    internal (bool Trace, bool Metrics, string? SpanName) Build() =>
        (_traceEnabled, _metricsEnabled, _spanName);
}
