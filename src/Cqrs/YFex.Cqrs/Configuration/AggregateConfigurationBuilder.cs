using YFex.Cqrs.Registration;

namespace YFex.Cqrs.Configuration;

/// <summary>
/// Entry point for configuring all messages belonging to an aggregate.
/// Stores strongly-typed source interfaces — no List&lt;object&gt;.
/// </summary>
public sealed class AggregateConfigurationBuilder<TAggregate>
{
    // Strongly-typed: IQueryConfigurationSource / ICommandConfigurationSource
    // (not List<object> — framework principle 4)
    private readonly List<IQueryConfigurationSource>   _queries   = [];
    private readonly List<ICommandConfigurationSource> _commands  = [];
    private readonly List<EventGroupConfigurationBuilder> _events = [];

    // ── Queries ───────────────────────────────────────────────────────────────

    public QueryConfigurationBuilder<TQuery, TResult> Query<TQuery, TResult>()
        where TQuery : IQuery<TResult>
    {
        var b = new QueryConfigurationBuilder<TQuery, TResult>();
        _queries.Add(b);
        return b;
    }

    // ── Commands (no result) ──────────────────────────────────────────────────

    public CommandConfigurationBuilder<TCommand> Command<TCommand>()
        where TCommand : ICommand
    {
        var b = new CommandConfigurationBuilder<TCommand>();
        _commands.Add(b);
        return b;
    }

    // ── Commands (with result) ────────────────────────────────────────────────

    public CommandConfigurationBuilder<TCommand, TResult> Command<TCommand, TResult>()
        where TCommand : ICommand<TResult>
    {
        var b = new CommandConfigurationBuilder<TCommand, TResult>();
        _commands.Add(b);
        return b;
    }

    // ── Events ────────────────────────────────────────────────────────────────

    public EventGroupConfigurationBuilder Events<T1>() where T1 : IEvent
        => AddEventGroup(typeof(T1));

    public EventGroupConfigurationBuilder Events<T1, T2>()
        where T1 : IEvent where T2 : IEvent
        => AddEventGroup(typeof(T1), typeof(T2));

    public EventGroupConfigurationBuilder Events<T1, T2, T3>()
        where T1 : IEvent where T2 : IEvent where T3 : IEvent
        => AddEventGroup(typeof(T1), typeof(T2), typeof(T3));

    public EventGroupConfigurationBuilder Events<T1, T2, T3, T4>()
        where T1 : IEvent where T2 : IEvent where T3 : IEvent where T4 : IEvent
        => AddEventGroup(typeof(T1), typeof(T2), typeof(T3), typeof(T4));

    private EventGroupConfigurationBuilder AddEventGroup(params Type[] types)
    {
        var b = new EventGroupConfigurationBuilder(types);
        _events.Add(b);
        return b;
    }

    // ── Internal: called by IAggregateConfiguration<T>.Collect() ─────────────

    internal AggregateRegistrations BuildRegistrations()
    {
        var queries  = new QueryRegistrationMetadata[_queries.Count];
        for (int i = 0; i < _queries.Count; i++)
            queries[i] = _queries[i].BuildMetadata();

        var commands = new CommandRegistrationMetadata[_commands.Count];
        for (int i = 0; i < _commands.Count; i++)
            commands[i] = _commands[i].BuildMetadata();

        var events = new EventGroupRegistrationMetadata[_events.Count];
        for (int i = 0; i < _events.Count; i++)
            events[i] = _events[i].BuildMetadata();

        return new AggregateRegistrations(queries, commands, events);
    }
}
