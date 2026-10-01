using System.Security.Claims;
using YFex.Validation;

namespace YFex.Cqrs.Configuration;

// ── Validators ───────────────────────────────────────────────────────────────

[Obsolete("Implement YFex.Validation.IContextRule<TQuery> and register it with .Validate<TRule>().")]
public interface IQueryValidator<TQuery>
{
    ValueTask<ValidationResult> ValidateAsync(TQuery query, CancellationToken ct);
}

[Obsolete("Implement YFex.Validation.IContextRule<TCommand> and register it with .Validate<TRule>().")]
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

// ── Reverse (compensation) handlers ──────────────────────────────────────────

/// <summary>
/// Custom reverse handler for a void command. Wired via <c>.ReversibleWith&lt;THandler&gt;()</c>.
/// Executed by YFex.Cqrs.Compensation when the original command is undone.
/// </summary>
public interface IReverseHandler<in TCommand>
{
    ValueTask CompensateAsync(TCommand original, CancellationToken ct);
}

/// <summary>
/// Custom reverse handler for a result-bearing command. Receives the original command and the
/// result it produced (null when the result was not journaled).
/// </summary>
public interface IReverseHandler<in TCommand, in TResult>
{
    ValueTask CompensateAsync(TCommand original, TResult? result, CancellationToken ct);
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
