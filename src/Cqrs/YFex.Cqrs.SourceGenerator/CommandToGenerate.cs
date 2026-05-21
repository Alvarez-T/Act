using System;

namespace YFex.Cqrs.SourceGenerator
{
    /// <summary>
    /// Represents a command record to generate a static helper method for.
    /// </summary>
    internal readonly struct CommandToGenerate : IEquatable<CommandToGenerate>
    {
        public string RecordName { get; }
        public string MethodName { get; }
        public EquatableArray<ParameterInfo> Parameters { get; }

        public CommandToGenerate(
            string recordName,
            string methodName,
            EquatableArray<ParameterInfo> parameters)
        {
            RecordName = recordName;
            MethodName = methodName;
            Parameters = parameters;
        }

        public bool Equals(CommandToGenerate other)
            => RecordName == other.RecordName
            && MethodName == other.MethodName
            && Parameters == other.Parameters;

        public override bool Equals(object obj)
            => obj is CommandToGenerate other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (RecordName?.GetHashCode() ?? 0);
                h = h * 31 + (MethodName?.GetHashCode() ?? 0);
                h = h * 31 + Parameters.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(CommandToGenerate left, CommandToGenerate right) => left.Equals(right);
        public static bool operator !=(CommandToGenerate left, CommandToGenerate right) => !left.Equals(right);
    }
}
