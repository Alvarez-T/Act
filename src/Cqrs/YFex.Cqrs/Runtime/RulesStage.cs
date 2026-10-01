using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using YFex.Rules;
using YFex.Validation;

namespace YFex.Cqrs.Runtime;

/// <summary>Outcome of the rules stage (stages 3–4, 09 §6).</summary>
public readonly struct RulesOutcome
{
    internal RulesOutcome(Error? error, object? decision, bool flagged)
    {
        Error = error;
        Decision = decision;
        Flagged = flagged;
    }

    /// <summary>Rejection (validation problem, approval required, mismatch per policy); null when the message may proceed.</summary>
    public Error? Error { get; }
    /// <summary>The <c>Decision&lt;TSituation&gt;</c> (boxed reference — it's a class) for the handler.</summary>
    public object? Decision { get; }
    /// <summary>Accepted but must be reviewed (fingerprint mismatch under AcceptAndFlag, offline approval).</summary>
    public bool Flagged { get; }
    public bool IsRejected => Error is not null;
}

/// <summary>
/// Stages 3 (gather: <see cref="ISituationLoader{TFrom,TSituation}"/>) and 4 (rules: evaluate, deny, approvals,
/// fingerprint mismatch) for one message type. Configured with <c>.UseSituation&lt;TS, TLoader&gt;().Enforce(set)</c>.
/// Type-erased entry for dispatchers; the typed subclass knows the message and the situation.
/// </summary>
public abstract class RulesStage
{
    public abstract Type SituationType { get; }

    /// <summary>Runs stages 3–4 for <paramref name="message"/>.</summary>
    public abstract ValueTask<RulesOutcome> RunAsync(object message, IServiceProvider services, CancellationToken ct);

    /// <summary>
    /// Invokes an <see cref="IRuledCommandHandler{TCommand,TResult,TSituation}"/> when one is registered; null otherwise
    /// (the regular/validated handler runs and can read the decision through <see cref="IDecisionAccessor{TSituation}"/>).
    /// </summary>
    public abstract Task<TResult>? TryInvokeRuled<TCommand, TResult>(IServiceProvider services, TCommand command, object decision, CancellationToken ct)
        where TCommand : ICommand<TResult>;

    /// <summary>Void-command counterpart of <see cref="TryInvokeRuled{TCommand,TResult}"/>.</summary>
    public abstract Task? TryInvokeRuled<TCommand>(IServiceProvider services, TCommand command, object decision, CancellationToken ct)
        where TCommand : ICommand;
}

/// <summary>
/// Typed rules stage. The rule data comes from a registered <see cref="RuleSetHost{TSituation}"/> for the same
/// definition (published tables/parameters/overrides, as-of); without one, the code-only set is used.
/// </summary>
public sealed class RulesStage<TMessage, TSituation> : RulesStage where TSituation : ISituation<TSituation>
{
    private readonly Func<IServiceProvider, ISituationLoader<TMessage, TSituation>> _loader;
    private readonly bool _isQuery;

    internal RulesStage(Func<IServiceProvider, ISituationLoader<TMessage, TSituation>> loader, RuleSetDefinition<TSituation> set,
        EnforceOptions options, bool isQuery)
    {
        _loader = loader;
        Set = set;
        Options = options;
        _isQuery = isQuery;
    }

    public RuleSetDefinition<TSituation> Set { get; }
    public EnforceOptions Options { get; }
    public override Type SituationType => typeof(TSituation);

    public override async ValueTask<RulesOutcome> RunAsync(object message, IServiceProvider services, CancellationToken ct)
    {
        var msg = (TMessage)message;
        // Stage 3 — gather (all I/O happens here, Principle A).
        var situation = await _loader(services).LoadAsync(msg, ct).ConfigureAwait(false);

        // Stage 4 — decide (pure, sync).
        var evaluate = new EvaluateOptions { Trace = Options.Trace };
        var decision = services.GetService<RuleSetHost<TSituation>>() is { } host && ReferenceEquals(host.Definition, Set)
            ? host.Evaluate(in situation, in evaluate)
            : Set.Evaluate(in situation, in evaluate);

        // Queries expose decisions (GetSaleOptions) — they never reject on the verdict.
        if (_isQuery) return new(null, decision, false);

        if (decision.Verdict.IsDenied)
            return new(Error.Validation(new ValidationResult(Options.Paths.Map(decision.Violations))), decision, false);

        bool flagged = false;
        var bound = msg as IDecisionBound;
        if (decision.Verdict.IsNeedsApproval)
        {
            var check = await CheckApprovalsAsync(decision, bound, services, ct).ConfigureAwait(false);
            if (check.Error is { } rejected) return new(rejected, decision, false);
            flagged |= check.Flagged;
        }

        if (bound is not null && (bound.DecisionFingerprint != 0 || Options.RequireFingerprint)
            && bound.DecisionFingerprint != decision.Fingerprint)
        {
            switch (Options.OnMismatch)
            {
                case DecisionMismatchPolicy.Reject:
                    return new(Error.Validation(ValidationResult.Of(MismatchViolation(bound, decision))), decision, false);
                case DecisionMismatchPolicy.Escalate:
                    return new(new Error(ErrorType.Conflict, "The decision changed since the client evaluated it.",
                        ViolationList.From([MismatchViolation(bound, decision)])), decision, false);
                case DecisionMismatchPolicy.AcceptAndFlag:
                    flagged = true;
                    break;
            }
        }

        if (flagged && services.GetService<IDecisionAuditSink>() is { } audit)
            await audit.OnFlaggedAsync(message, decision, bound?.DecisionFingerprint ?? 0, ct).ConfigureAwait(false);
        return new(null, decision, flagged);
    }

    /// <summary>Approval reasons with message paths (04 §14 path map).</summary>
    private static ApprovalRequestList MapReasons(ApprovalRequestList approvals, SituationPathMap paths)
    {
        if (paths.IsEmpty || approvals.IsEmpty) return approvals;
        var mapped = new ApprovalRequest[approvals.Count];
        for (int i = 0; i < mapped.Length; i++)
        {
            ref readonly var a = ref approvals[i];
            mapped[i] = new ApprovalRequest(a.Permission, a.Reason.WithPath(paths.Map(a.Reason.Path)), a.Rule);
        }
        return ApprovalRequestList.From(mapped);
    }

    private static Violation MismatchViolation(IDecisionBound bound, Decision<TSituation> d)
        => new(FieldPath.Empty, "rules.decision.mismatch", Severity.Error,
            new RuleArgs(RawValue.From(unchecked((long)bound.DecisionFingerprint)), RawValue.From(unchecked((long)d.Fingerprint))),
            new ViolationOrigin(ViolationSource.Rule, 0));

    /// <summary>Every required permission needs a valid token bound to THIS fingerprint (09 §6 step 3, D34).</summary>
    private async ValueTask<(Error? Error, bool Flagged)> CheckApprovalsAsync(Decision<TSituation> d, IDecisionBound? bound,
        IServiceProvider services, CancellationToken ct)
    {
        var required = Error.ApprovalRequired(MapReasons(d.Approvals, Options.Paths), d.Fingerprint, Options.Paths.Map(d.Violations));
        var tokens = bound?.Approvals;
        var verifier = services.GetService<IOverrideTokenVerifier>();
        if (tokens is null || tokens.Count == 0 || verifier is null) return (required, false);

        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var directory = services.GetService<IApproverDirectory>();
        bool flagged = false;
        for (int r = 0; r < d.Approvals.Count; r++)
        {
            var request = d.Approvals[r];
            bool ok = false;
            foreach (var token in tokens)
            {
                if (!string.Equals(token.Permission, request.Permission, StringComparison.Ordinal)) continue;
                var v = verifier.Verify(in token, in request, d.Fingerprint, now);
                if (!v.IsValid) continue;
                // Device (offline) tokens: the approver's permission is re-checked now — the server didn't issue them.
                if (token.Kind == OverrideTokenKind.Device
                    && (directory is null || !await directory.HasPermissionAsync(token.ApproverId, token.Permission, ct).ConfigureAwait(false)))
                    continue;
                ok = true;
                flagged |= v.Flagged;
                break;
            }
            if (!ok) return (required, false);
        }
        return (null, flagged);
    }

    public override Task<TResult>? TryInvokeRuled<TCommand, TResult>(IServiceProvider services, TCommand command, object decision, CancellationToken ct)
        => services.GetService<IRuledCommandHandler<TCommand, TResult, TSituation>>() is { } handler
            ? handler.HandleAsync(Validator.AssumeValid(command), (Decision<TSituation>)decision, ct)
            : null;

    public override Task? TryInvokeRuled<TCommand>(IServiceProvider services, TCommand command, object decision, CancellationToken ct)
        => services.GetService<IRuledCommandHandler<TCommand, TSituation>>() is { } handler
            ? handler.HandleAsync(Validator.AssumeValid(command), (Decision<TSituation>)decision, ct)
            : null;
}

/// <summary>Builds rules stages from the configuration verbs (loader resolution mirrors context rules: DI first, else constructed).</summary>
public static class RulesStages
{
    public static RulesStage<TMessage, TSituation> Create<TMessage, TSituation,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLoader>(
        RuleSetDefinition<TSituation> set, EnforceOptions? options, bool isQuery)
        where TSituation : ISituation<TSituation>
        where TLoader : class, ISituationLoader<TMessage, TSituation>
    {
        ArgumentNullException.ThrowIfNull(set);
        return new(static sp => sp.GetService<TLoader>() ?? ActivatorUtilities.CreateInstance<TLoader>(sp), set, options ?? EnforceOptions.Default, isQuery);
    }
}

/// <summary>
/// The decision of the message being handled, flowing with the async call (set by the dispatcher around the handler).
/// </summary>
public static class DecisionContext
{
    private static readonly AsyncLocal<Entry?> s_current = new();

    /// <summary>The decision of <see cref="Message"/> (instance identity — a nested dispatch never sees its caller's).</summary>
    public sealed record Entry(object Message, RulesStage Stage, object Decision, bool Flagged);

    public static Entry? Current => s_current.Value;

    /// <summary>The current decision when it belongs to <paramref name="message"/>.</summary>
    public static Entry? For(object message) => s_current.Value is { } e && ReferenceEquals(e.Message, message) ? e : null;

    /// <summary>
    /// Publishes <paramref name="outcome"/>'s decision for <paramref name="message"/> (or clears the slot when the message
    /// has no rules stage); dispose to restore the previous value.
    /// </summary>
    public static Scope Enter(object message, RulesStage? stage, in RulesOutcome outcome)
    {
        var previous = s_current.Value;
        s_current.Value = stage is not null && outcome.Decision is { } d ? new Entry(message, stage, d, outcome.Flagged) : null;
        return new Scope(previous);
    }

    public readonly struct Scope(Entry? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }
}

/// <summary>For handlers that don't implement <see cref="IRuledCommandHandler{TCommand,TResult,TSituation}"/> (09 §9 alternative).</summary>
public interface IDecisionAccessor<TSituation> where TSituation : ISituation<TSituation>
{
    Decision<TSituation>? Current { get; }
}

/// <summary>Default accessor (register as a singleton — it reads the async-flowing context).</summary>
public sealed class DecisionAccessor<TSituation> : IDecisionAccessor<TSituation> where TSituation : ISituation<TSituation>
{
    public Decision<TSituation>? Current => DecisionContext.Current?.Decision as Decision<TSituation>;
}

/// <summary>Receives flagged decisions (mismatch accepted under AcceptAndFlag, offline approvals) — audit/review queue.</summary>
public interface IDecisionAuditSink
{
    ValueTask OnFlaggedAsync(object message, object decision, ulong clientFingerprint, CancellationToken ct);
}

/// <summary>
/// "Does user X hold permission P now?" — needed to accept OFFLINE (device-signed) approvals, whose approver the server
/// did not check when the token was issued (D34). Without a registered directory, device tokens are rejected.
/// </summary>
public interface IApproverDirectory
{
    ValueTask<bool> HasPermissionAsync(string userId, string permission, CancellationToken ct);
}
