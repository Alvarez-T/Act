using YFex.Rules;
using YFex.Validation;

namespace YFex.Cqrs;

/// <summary>
/// Opt-in handler of a command configured with <c>.UseSituation().Enforce()</c> (P7 → D24, 09 §9): receives the validated
/// command AND the server's decision — persist <see cref="Decision{TSituation}.Adjustments"/>, totals, fingerprint and the
/// situation bytes with the document (06 §2). When registered, it wins over the classic/validated handlers.
/// </summary>
public interface IRuledCommandHandler<TCommand, TResult, TSituation>
    where TCommand : ICommand<TResult>
    where TSituation : ISituation<TSituation>
{
    Task<TResult> HandleAsync(Valid<TCommand> command, Decision<TSituation> decision, CancellationToken ct);
}

/// <summary>Void-command counterpart of <see cref="IRuledCommandHandler{TCommand,TResult,TSituation}"/>.</summary>
public interface IRuledCommandHandler<TCommand, TSituation>
    where TCommand : ICommand
    where TSituation : ISituation<TSituation>
{
    Task HandleAsync(Valid<TCommand> command, Decision<TSituation> decision, CancellationToken ct);
}
