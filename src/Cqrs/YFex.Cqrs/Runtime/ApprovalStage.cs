using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization.Public;
using YFex.Canonical;
using YFex.Cryptography;
using YFex.Rules;
using YFex.Validation;

namespace YFex.Cqrs.Runtime;

/// <summary>
/// <c>.RequireApproval(permission, bind: c =&gt; c.X)</c> (authorization-consolidation 06 §1.3, decisions A5/A7): the message needs a
/// token of <see cref="ApprovalPolicy.Permission"/> bound to <c>ApprovalDigest.Of(message type, bound value)</c>. Without one the
/// dispatcher answers <see cref="ErrorKind.ApprovalRequired"/> with the digest; the client asks an approver
/// (<c>Approvals.Commands.Approve</c>) and resends. Runs in the rules slot (after the gate, before context rules); with
/// <c>.Enforce(ruleSet)</c> on the same message it runs after the rules.
/// </summary>
public sealed class ApprovalStage<TMessage> : RulesStage
{
    private readonly Func<TMessage, Hash256> _digest;

    internal ApprovalStage(ApprovalPolicy declared, Func<TMessage, Hash256> digest)
    {
        Declared = declared ?? throw new ArgumentNullException(nameof(declared));
        _digest = digest ?? throw new ArgumentNullException(nameof(digest));
    }

    /// <summary>The code-declared policy (an <see cref="IApprovalPolicySource"/> may change it at run time).</summary>
    public ApprovalPolicy Declared { get; }

    public override Type? SituationType => null;

    /// <summary>The digest a token must bind for <paramref name="message"/>.</summary>
    public Hash256 DigestOf(TMessage message) => _digest(message);

    public override async ValueTask<RulesOutcome> RunAsync(object message, IServiceProvider services, CancellationToken ct)
    {
        var (error, flagged) = await CheckAsync((TMessage)message, services, ct).ConfigureAwait(false);
        // Offline approvals are always reviewed (D34): the sink receives the policy in place of a rules decision.
        if (flagged && services.GetService<IDecisionAuditSink>() is { } audit)
            await audit.OnFlaggedAsync(message, Declared, 0, ct).ConfigureAwait(false);
        return new RulesOutcome(error, null, flagged);
    }

    internal async ValueTask<(Error? Error, bool Flagged)> CheckAsync(TMessage message, IServiceProvider services, CancellationToken ct)
    {
        var policy = ApprovalGate.Resolve(services, typeof(TMessage), Declared);
        if (!policy.IsRequired) return (null, false);
        Hash256 digest = _digest(message);
        var check = await ApprovalGate.CheckAsync(services, (message as IApprovalBound)?.Approvals, policy, digest, null, ct)
            .ConfigureAwait(false);
        if (check.Satisfied) return (null, check.Flags != ApprovalFlags.None);
        var request = new ApprovalRequest(policy.Permission,
            new Violation(FieldPath.Empty, ApprovalGate.RequiredCode, Severity.Error, default, new ViolationOrigin(ViolationSource.Rule, 0)), default);
        return (Error.ApprovalRequired(ApprovalRequestList.From([request]), ApprovalGate.FingerprintOf(digest), in digest), false);
    }

    public override Task<TResult>? TryInvokeRuled<TCommand, TResult>(IServiceProvider services, TCommand command, object decision, CancellationToken ct)
        => null;

    public override Task? TryInvokeRuled<TCommand>(IServiceProvider services, TCommand command, object decision, CancellationToken ct)
        => null;
}

/// <summary>Builds and combines approval stages (configuration verbs).</summary>
public static class ApprovalStages
{
    /// <summary>The stage of <c>.RequireApproval(permission, bind)</c>.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TMessage"/> does not implement <see cref="IApprovalBound"/>.</exception>
    public static ApprovalStage<TMessage> Create<TMessage>(Permission permission, Func<TMessage, Hash256> digest, bool separationOfDuties = true)
    {
        if (!typeof(IApprovalBound).IsAssignableFrom(typeof(TMessage)))
            throw new InvalidOperationException(
                $"'{typeof(TMessage).Name}': .RequireApproval needs the message to implement IApprovalBound (an Approvals property).");
        if (string.IsNullOrEmpty(permission.Value)) throw new ArgumentException("The approval permission is empty.", nameof(permission));
        return new ApprovalStage<TMessage>(new ApprovalPolicy(permission, separationOfDuties), digest);
    }

    /// <summary>The message's rules slot: the rules stage, the approval stage, or the rules stage followed by the approval stage.</summary>
    public static RulesStage? Combine(RulesStage? rules, RulesStage? approval)
    {
        if (approval is null) return rules;
        if (rules is null) return approval;
        rules.AttachApprovals(approval);
        return rules;
    }
}

/// <summary>Digest functions of <c>.RequireApproval</c> bound values (decision A5: text, <see cref="Guid"/>, integers, canonical core types).</summary>
public static class ApprovalBindings
{
    public static Func<TMessage, Hash256> Text<TMessage>(Func<TMessage, string> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return message => ApprovalDigest.Of(typeof(TMessage), bind(message));
    }

    public static Func<TMessage, Hash256> Guid<TMessage>(Func<TMessage, System.Guid> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return message => ApprovalDigest.Of(typeof(TMessage), bind(message));
    }

    public static Func<TMessage, Hash256> Integer<TMessage>(Func<TMessage, long> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return message => ApprovalDigest.Of(typeof(TMessage), bind(message));
    }

    public static Func<TMessage, Hash256> Canonical<TMessage, TValue>(Func<TMessage, TValue> bind) where TValue : ICanonicalValue<TValue>
    {
        ArgumentNullException.ThrowIfNull(bind);
        return message => ApprovalDigest.Of(typeof(TMessage), bind(message));
    }
}

/// <summary>The approval check shared by <see cref="ApprovalStage{TMessage}"/> and the rules stage.</summary>
internal static class ApprovalGate
{
    /// <summary>Violation code of a <c>.RequireApproval</c> request.</summary>
    public const string RequiredCode = "authorization.approval.required";

    /// <summary>The policy after every registered <see cref="IApprovalPolicySource"/> (registration order).</summary>
    public static ApprovalPolicy Resolve(IServiceProvider services, Type messageType, ApprovalPolicy declared)
    {
        var policy = declared;
        foreach (var source in services.GetServices<IApprovalPolicySource>())
            policy = source.Resolve(messageType, policy) ?? policy;
        return policy;
    }

    /// <summary>
    /// True when one of <paramref name="tokens"/> approves <paramref name="policy"/> for <paramref name="digest"/>. Offline tokens also
    /// pass <paramref name="offlineAllowed"/> (the rules amount cap). Without an <see cref="IApprovalTokenVerifier"/> nothing is approved.
    /// </summary>
    public static async ValueTask<(bool Satisfied, ApprovalFlags Flags)> CheckAsync(IServiceProvider services,
        IReadOnlyList<ApprovalToken>? tokens, ApprovalPolicy policy, Hash256 digest, Func<bool>? offlineAllowed, CancellationToken ct)
    {
        if (tokens is null || tokens.Count == 0 || digest.IsZero) return (false, ApprovalFlags.None);
        if (services.GetService<IApprovalTokenVerifier>() is not { } verifier) return (false, ApprovalFlags.None);
        var caller = services.GetService<IPrincipalAccessor>()?.Current;
        var expected = new ApprovalExpectation(policy.Permission, digest,
            caller is { IsAuthenticated: true } ? caller.Actor.Subject : null, caller?.Scope, policy.SeparationOfDuties,
            (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(),
            services.GetService<IApprovalOriginAccessor>()?.Current ?? default);
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token is null || token.Permission != policy.Permission) continue;
            if (token.Kind != ApprovalTokenKind.Server && offlineAllowed is not null && !offlineAllowed()) continue;
            var verification = await verifier.VerifyAsync(token, expected, ct).ConfigureAwait(false);
            if (verification.IsValid) return (true, verification.Flags);
        }
        return (false, ApprovalFlags.None);
    }

    /// <summary>The display fingerprint of a digest (its first 8 bytes): equality and UI only.</summary>
    public static ulong FingerprintOf(in Hash256 digest) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan());
}
