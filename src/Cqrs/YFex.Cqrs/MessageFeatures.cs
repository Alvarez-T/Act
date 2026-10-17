using System.Diagnostics.CodeAnalysis;

namespace YFex.Cqrs;

/// <summary>
/// Typed settings another library attaches to a command's configuration without YFex.Cqrs knowing them — for example
/// YFex.Offline's <c>.Offline(...)</c> verb stores its policy here (offline ⚑O13). One value per feature type; a later
/// configuration (server or client overrides) replaces the value of an earlier one. Read once at startup by the owning
/// library, never on the dispatch hot path.
/// <code>
/// // the owning library: an extension verb on the command builder
/// public static CommandConfigurationBuilder&lt;TCommand&gt; Audited&lt;TCommand&gt;(this CommandConfigurationBuilder&lt;TCommand&gt; b, string category)
///     where TCommand : ICommand
///     => b.Feature(new AuditFeature(category));
///
/// // at startup
/// foreach (var (type, policy) in registry.Commands)
///     if (policy.Features?.TryGet(out AuditFeature? audit) == true) table.Add(type, audit);
/// </code>
/// </summary>
public sealed class MessageFeatures
{
    private readonly KeyValuePair<Type, object>[] _items;

    internal MessageFeatures(KeyValuePair<Type, object>[] items) => _items = items;

    /// <summary>Number of features attached.</summary>
    public int Count => _items.Length;

    /// <summary>The feature of type <typeparamref name="TFeature"/>, when attached.</summary>
    public bool TryGet<TFeature>([NotNullWhen(true)] out TFeature? feature) where TFeature : class
    {
        for (int i = 0; i < _items.Length; i++)
        {
            if (_items[i].Key != typeof(TFeature)) continue;
            feature = (TFeature)_items[i].Value;
            return true;
        }
        feature = null;
        return false;
    }

    /// <summary>The feature of type <typeparamref name="TFeature"/>, or <see langword="null"/>.</summary>
    public TFeature? Get<TFeature>() where TFeature : class => TryGet(out TFeature? feature) ? feature : null;

    internal static MessageFeatures? From(Dictionary<Type, object>? features)
    {
        if (features is null || features.Count == 0) return null;
        var items = new KeyValuePair<Type, object>[features.Count];
        int n = 0;
        foreach (var item in features) items[n++] = item;
        return new MessageFeatures(items);
    }
}

/// <summary>Feature storage shared by the command builders (one value per feature type, last write wins).</summary>
internal static class MessageFeatureSlots
{
    public static void Set<TFeature>(ref List<KeyValuePair<Type, object>>? slots, TFeature feature) where TFeature : class
    {
        ArgumentNullException.ThrowIfNull(feature);
        slots ??= [];
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Key != typeof(TFeature)) continue;
            slots[i] = new(typeof(TFeature), feature);
            return;
        }
        slots.Add(new(typeof(TFeature), feature));
    }

    public static TFeature? Get<TFeature>(List<KeyValuePair<Type, object>>? slots) where TFeature : class
    {
        if (slots is null) return null;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Key == typeof(TFeature)) return (TFeature)slots[i].Value;
        return null;
    }
}
