using YFex.Cqrs;
using YFex.Cqrs.Configuration;

namespace YFex.Cqrs.Compensation.Tests;

// ── Aggregate + commands ─────────────────────────────────────────────────────

public sealed class InvoiceAggregate { }

public sealed record ApproveInvoiceCommand(int InvoiceId) : ICommand;
public sealed record UnapproveInvoiceCommand(int InvoiceId) : ICommand;
public sealed record CancelInvoiceCommand(int InvoiceId) : ICommand;

public sealed record CreateInvoiceCommand(string Name) : ICommand<int>;
public sealed record DeleteInvoiceCommand(int Id) : ICommand;

// ── Configurations (call CompiledMessagingRegistry.Build directly — no generator) ─

/// <summary>Approve is reversed by Unapprove (command-only map); both are registered.</summary>
public sealed class ReversibleConfig : IAggregateConfiguration<InvoiceAggregate>
{
    public void Configure(AggregateConfigurationBuilder<InvoiceAggregate> b)
    {
        b.Command<UnapproveInvoiceCommand>();                                     // reverse target
        b.Command<ApproveInvoiceCommand>()
            .Reversible<UnapproveInvoiceCommand>(c => new UnapproveInvoiceCommand(c.InvoiceId));
    }
}

/// <summary>Result-bearing: Create(→id) is reversed by Delete(id) using the result-bearing map.</summary>
public sealed class ResultReversibleConfig : IAggregateConfiguration<InvoiceAggregate>
{
    public void Configure(AggregateConfigurationBuilder<InvoiceAggregate> b)
    {
        b.Command<DeleteInvoiceCommand>();                                        // reverse target
        b.Command<CreateInvoiceCommand, int>()
            .Reversible<DeleteInvoiceCommand>((cmd, result) => new DeleteInvoiceCommand(result));
    }
}

/// <summary>Declares Reversible twice — last (Cancel) must win (single-value override).</summary>
public sealed class OverrideReverseConfig : IAggregateConfiguration<InvoiceAggregate>
{
    public void Configure(AggregateConfigurationBuilder<InvoiceAggregate> b)
    {
        b.Command<UnapproveInvoiceCommand>();
        b.Command<CancelInvoiceCommand>();
        b.Command<ApproveInvoiceCommand>()
            .Reversible<UnapproveInvoiceCommand>(c => new UnapproveInvoiceCommand(c.InvoiceId))
            .Reversible<CancelInvoiceCommand>(c => new CancelInvoiceCommand(c.InvoiceId));
    }
}

/// <summary>Reverse command (Unapprove) is NOT registered → Build must fail fast.</summary>
public sealed class UnregisteredReverseConfig : IAggregateConfiguration<InvoiceAggregate>
{
    public void Configure(AggregateConfigurationBuilder<InvoiceAggregate> b)
        => b.Command<ApproveInvoiceCommand>()
            .Reversible<UnapproveInvoiceCommand>(c => new UnapproveInvoiceCommand(c.InvoiceId));
}

// ── Custom-handler reversal fixtures (ReversibleWith<THandler>) ───────────────

public sealed record ArchiveInvoiceCommand(int InvoiceId) : ICommand;
public sealed record RegisterInvoiceCommand(string Name) : ICommand<int>;

/// <summary>Void custom reverse handler — records the compensated command.</summary>
public sealed class RecordingReverseHandler : IReverseHandler<ArchiveInvoiceCommand>
{
    public List<int> Compensated { get; } = [];
    public ValueTask CompensateAsync(ArchiveInvoiceCommand original, CancellationToken ct)
    {
        Compensated.Add(original.InvoiceId);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Result-bearing custom reverse handler — records the original command + its result.</summary>
public sealed class RecordingResultReverseHandler : IReverseHandler<RegisterInvoiceCommand, int>
{
    public List<(string Name, int Result)> Compensated { get; } = [];
    public ValueTask CompensateAsync(RegisterInvoiceCommand original, int result, CancellationToken ct)
    {
        Compensated.Add((original.Name, result));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Archive is reversed by a custom handler (no reverse command target needed).</summary>
public sealed class HandlerReversibleConfig : IAggregateConfiguration<InvoiceAggregate>
{
    public void Configure(AggregateConfigurationBuilder<InvoiceAggregate> b)
        => b.Command<ArchiveInvoiceCommand>().ReversibleWith<RecordingReverseHandler>();
}

/// <summary>Result-bearing command reversed by a custom handler that receives the journaled result.</summary>
public sealed class HandlerResultReversibleConfig : IAggregateConfiguration<InvoiceAggregate>
{
    public void Configure(AggregateConfigurationBuilder<InvoiceAggregate> b)
        => b.Command<RegisterInvoiceCommand, int>().ReversibleWith<RecordingResultReverseHandler>();
}

// ── Recording dispatcher ─────────────────────────────────────────────────────

public sealed class RecordingDispatcher : IDispatcher
{
    public List<object> Dispatched { get; } = [];

    public ValueTask<CacheableQueryResult<TResult>> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResult> => throw new NotSupportedException();

    public ValueTask<QueueableResult<TResult>> CommandAsync<TCommand, TResult>(TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand<TResult>
    {
        Dispatched.Add(cmd!);
        return new ValueTask<QueueableResult<TResult>>(default(QueueableResult<TResult>));
    }

    public ValueTask<QueueableResult> CommandAsync<TCommand>(TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand
    {
        Dispatched.Add(cmd!);
        return new ValueTask<QueueableResult>(QueueableResult.Ok());
    }

    public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : IEvent => ValueTask.CompletedTask;
}
