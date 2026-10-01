using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using YFex.Validation;

namespace YFex.Cqrs.Runtime;

/// <summary>Untyped handle of a <see cref="ContextRuleFactory{T}"/> (registration descriptors are type-erased).</summary>
public abstract class ContextRuleFactory
{
    private protected ContextRuleFactory() { }

    public abstract Type MessageType { get; }

    // Builds the typed stage-5 validator from every factory registered for the message (baseline + overrides).
    internal abstract MessageValidator CreateValidator(List<ContextRuleFactory> all);
}

/// <summary>
/// Creates a context rule (stage 5, async I/O: uniqueness, stock, FK) for one message. Built by the configuration
/// builders from <c>.Validate&lt;TRule&gt;()</c> (DI) or inline lambdas — no <c>Activator</c> at dispatch time.
/// </summary>
public sealed class ContextRuleFactory<T>(Func<IServiceProvider, IContextRule<T>> create) : ContextRuleFactory
{
    public override Type MessageType => typeof(T);

    public IContextRule<T> Create(IServiceProvider services) => create(services);

    /// <summary>A stateless rule instance shared by every dispatch.</summary>
    public static ContextRuleFactory<T> Shared(IContextRule<T> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new(_ => rule);
    }

    /// <summary>Resolved from DI when registered, else constructed with DI-provided constructor arguments.</summary>
    public static ContextRuleFactory<T> FromServices<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRule>()
        where TRule : class, IContextRule<T>
        => new(static sp => sp.GetService<TRule>() ?? ActivatorUtilities.CreateInstance<TRule>(sp));

    internal override MessageValidator CreateValidator(List<ContextRuleFactory> all)
    {
        var typed = new ContextRuleFactory<T>[all.Count];
        for (int i = 0; i < typed.Length; i++) typed[i] = (ContextRuleFactory<T>)all[i];
        return new MessageValidator<T>(typed);
    }
}

/// <summary>
/// Stage 5 of a message (its context rules), compiled once per message type. Generic dispatchers use
/// <see cref="MessageValidator{T}"/> directly; untyped boundaries (the server <c>IHandlerInvoker</c> decorator) call
/// <see cref="ValidateAsync(object, IServiceProvider, CancellationToken)"/>.
/// </summary>
public abstract class MessageValidator
{
    private protected MessageValidator() { }

    public abstract Type MessageType { get; }

    public abstract ValueTask<ValidationResult> ValidateAsync(object message, IServiceProvider services, CancellationToken ct);
}

public sealed class MessageValidator<T> : MessageValidator
{
    private readonly ContextRuleFactory<T>[] _rules;

    internal MessageValidator(ContextRuleFactory<T>[] rules) => _rules = rules;

    public override Type MessageType => typeof(T);

    public ReadOnlySpan<ContextRuleFactory<T>> Rules => _rules;

    public override ValueTask<ValidationResult> ValidateAsync(object message, IServiceProvider services, CancellationToken ct)
        => ValidateAsync((T)message, services, ct);

    /// <summary>Runs the rules sequentially; stops after the first rule that reports an error.</summary>
    public async ValueTask<ValidationResult> ValidateAsync(T message, IServiceProvider services, CancellationToken ct)
    {
        using var collector = ViolationCollector.Rent();
        foreach (var factory in _rules)
        {
            await factory.Create(services).CheckAsync(message, collector, ct).ConfigureAwait(false);
            if (collector.HasErrors) break;
        }
        return collector.ToResult();
    }
}

/// <summary>
/// The validation stages every dispatcher runs (09 §1): 1 shape → 2 authorize → (3–4 rules, Phase 4) → 5 context →
/// 6 CustomLogic → 7 handler. Shared by <c>LocalDispatcher</c>, the server invoker decorator and the Wolverine
/// dispatcher so the order is identical everywhere.
/// </summary>
public static class MessagePipeline
{
    /// <summary>
    /// Stage 1 — shape (generated <c>Validate</c> through <see cref="ValidationSlot{T}"/>, runtime fluent rules as
    /// fallback). Allocation-free when the message is valid without warnings; <paramref name="violations"/> holds the
    /// errors on failure or the warnings/infos on success.
    /// </summary>
    public static bool CheckShape<T>(in T message, out ValidationResult violations)
    {
        using var scope = ViolationWriter.Begin(out var w);
        bool valid = ValidationSlot<T>.Validate(in message, ref w);
        violations = w.Count == 0 ? default : w.ToResult();
        return valid;
    }

    /// <summary>Stage 1 for type-erased callers (server invoker decorator, replay): <see cref="ObjectValidation"/>.</summary>
    public static bool CheckShape(object message, out ValidationResult violations)
    {
        using var scope = ViolationWriter.Begin(out var w);
        bool valid = ObjectValidation.Validate(message, ref w);
        violations = w.Count == 0 ? default : w.ToResult();
        return valid;
    }

    /// <summary>
    /// Clamps <c>Range(coerce: true)</c> members (generated <c>Coerce</c> through <see cref="CoercionSlot{T}"/>). Runs
    /// before stage 1 and, for queries, before the cache key is computed — so paging variants share one cache entry.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static T Coerce<T>(in T message) => CoercionSlot<T>.Apply(in message);

    /// <summary>Stage 5 — context rules of the policy (none → valid, synchronously).</summary>
    public static ValueTask<ValidationResult> CheckContextAsync<T>(T message, MessageValidator? validator,
        IServiceProvider services, CancellationToken ct)
        => validator is MessageValidator<T> typed
            ? typed.ValidateAsync(message, services, ct)
            : new ValueTask<ValidationResult>(ValidationResult.Valid);
}
