using YFex.Cqrs.Registration;

namespace YFex.Cqrs.Configuration;

public sealed class EventGroupConfigurationBuilder
{
    private readonly Type[]  _eventTypes;
    private          Type?   _groupUnion;

    internal EventGroupConfigurationBuilder(Type[] eventTypes) => _eventTypes = eventTypes;

    /// <summary>Links this set of events as cases of a union group type (e.g. a CustomerLifecycle union).</summary>
    public EventGroupConfigurationBuilder GroupAs<TUnion>() { _groupUnion = typeof(TUnion); return this; }

    internal EventGroupRegistrationMetadata BuildMetadata() => new()
    {
        EventTypes = _eventTypes,
        GroupUnion = _groupUnion,
    };
}
