using System;

namespace YFex.Cqrs.SourceGenerator
{
    /// <summary>
    /// The full model for a class that needs CQRS code generation.
    /// All fields are value-equatable for proper incremental caching.
    /// </summary>
    internal readonly struct ClassToGenerate : IEquatable<ClassToGenerate>
    {
        public string Namespace { get; }
        public string ClassName { get; }
        public EquatableArray<QueryToGenerate> Queries { get; }
        public EquatableArray<CommandToGenerate> Commands { get; }
        public EquatableArray<EventToGenerate> Events { get; }

        public ClassToGenerate(
            string @namespace,
            string className,
            EquatableArray<QueryToGenerate> queries,
            EquatableArray<CommandToGenerate> commands,
            EquatableArray<EventToGenerate> events)
        {
            Namespace = @namespace;
            ClassName = className;
            Queries = queries;
            Commands = commands;
            Events = events;
        }

        public bool Equals(ClassToGenerate other)
            => Namespace == other.Namespace
            && ClassName == other.ClassName
            && Queries == other.Queries
            && Commands == other.Commands
            && Events == other.Events;

        public override bool Equals(object obj)
            => obj is ClassToGenerate other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (Namespace?.GetHashCode() ?? 0);
                h = h * 31 + (ClassName?.GetHashCode() ?? 0);
                h = h * 31 + Queries.GetHashCode();
                h = h * 31 + Commands.GetHashCode();
                h = h * 31 + Events.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(ClassToGenerate left, ClassToGenerate right) => left.Equals(right);
        public static bool operator !=(ClassToGenerate left, ClassToGenerate right) => !left.Equals(right);

        public bool HasAnyMembers
            => !Queries.IsEmpty || !Commands.IsEmpty || !Events.IsEmpty;
    }
}
