using System;
using System.Text;

namespace YFex.Cqrs.SourceGenerator
{

    /// <summary>
    /// Represents a parameter in a record constructor.
    /// </summary>
    internal readonly struct ParameterInfo : IEquatable<ParameterInfo>
    {
        public string Type { get; }
        public string Name { get; }
        public string NameCamelCase { get; }

        public ParameterInfo(string type, string name, string nameCamelCase)
        {
            Type = type;
            Name = name;
            NameCamelCase = nameCamelCase;
        }

        public bool Equals(ParameterInfo other)
            => Type == other.Type && Name == other.Name && NameCamelCase == other.NameCamelCase;

        public override bool Equals(object obj)
            => obj is ParameterInfo other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (Type?.GetHashCode() ?? 0);
                h = h * 31 + (Name?.GetHashCode() ?? 0);
                h = h * 31 + (NameCamelCase?.GetHashCode() ?? 0);
                return h;
            }
        }

        public static bool operator ==(ParameterInfo left, ParameterInfo right) => left.Equals(right);
        public static bool operator !=(ParameterInfo left, ParameterInfo right) => !left.Equals(right);
    }
}
