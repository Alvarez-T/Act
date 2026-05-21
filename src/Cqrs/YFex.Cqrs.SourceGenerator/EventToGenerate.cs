using System;

namespace YFex.Cqrs.SourceGenerator
{
    /// <summary>
    /// Represents an event record to generate a static subscription method for.
    /// </summary>
    internal readonly struct EventToGenerate : IEquatable<EventToGenerate>
    {
        public string RecordName { get; }
        public string MethodName { get; }

        public EventToGenerate(string recordName, string methodName)
        {
            RecordName = recordName;
            MethodName = methodName;
        }

        public bool Equals(EventToGenerate other)
            => RecordName == other.RecordName && MethodName == other.MethodName;

        public override bool Equals(object obj)
            => obj is EventToGenerate other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (RecordName?.GetHashCode() ?? 0);
                h = h * 31 + (MethodName?.GetHashCode() ?? 0);
                return h;
            }
        }

        public static bool operator ==(EventToGenerate left, EventToGenerate right) => left.Equals(right);
        public static bool operator !=(EventToGenerate left, EventToGenerate right) => !left.Equals(right);
    }
}
