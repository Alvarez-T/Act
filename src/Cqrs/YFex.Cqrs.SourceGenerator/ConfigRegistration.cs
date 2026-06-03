using System;
using System.Collections.Generic;

namespace YFex.Cqrs.SourceGenerator;

/// <summary>
/// Represents one IAggregateConfiguration implementation found during compilation
/// scanning, used for Pipeline B (AddYFexConfigurations emission).
/// </summary>
internal readonly struct ConfigRegistration : IEquatable<ConfigRegistration>
{
    public string ImplementationFqn { get; }
    public IReadOnlyList<(string InterfaceName, string AggregateType)> Interfaces { get; }

    public ConfigRegistration(
        string implementationFqn,
        IReadOnlyList<(string, string)> interfaces)
    {
        ImplementationFqn = implementationFqn;
        Interfaces = interfaces;
    }

    public bool Equals(ConfigRegistration other)
        => ImplementationFqn == other.ImplementationFqn;

    public override bool Equals(object obj)
        => obj is ConfigRegistration other && Equals(other);

    public override int GetHashCode() => ImplementationFqn?.GetHashCode() ?? 0;

    public static bool operator ==(ConfigRegistration l, ConfigRegistration r) => l.Equals(r);
    public static bool operator !=(ConfigRegistration l, ConfigRegistration r) => !l.Equals(r);
}
