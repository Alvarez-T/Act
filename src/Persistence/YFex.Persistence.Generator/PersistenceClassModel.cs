using System;

namespace YFex.Persistence.Generator;

internal readonly struct PersistenceClassModel : IEquatable<PersistenceClassModel>
{
    public string Namespace { get; }
    public string ClassName { get; }
    public EquatableArray<PersistencePropertyModel> Properties { get; }

    public bool HasAnyPersistProperties => Properties.Count > 0;

    public PersistenceClassModel(
        string @namespace,
        string className,
        EquatableArray<PersistencePropertyModel> properties)
    {
        Namespace  = @namespace;
        ClassName  = className;
        Properties = properties;
    }

    public bool Equals(PersistenceClassModel other) =>
        Namespace  == other.Namespace  &&
        ClassName  == other.ClassName  &&
        Properties.Equals(other.Properties);

    public override bool Equals(object? obj) => obj is PersistenceClassModel m && Equals(m);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (Namespace?.GetHashCode() ?? 0);
            h = h * 31 + (ClassName?.GetHashCode() ?? 0);
            h = h * 31 + Properties.GetHashCode();
            return h;
        }
    }
}
