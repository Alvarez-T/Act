using System;

namespace YFex.Cqrs.SourceGenerator
{
    /// <summary>
    /// Represents a query record to generate a static helper method for.
    /// </summary>
    internal readonly struct QueryToGenerate : IEquatable<QueryToGenerate>
    {
        public string RecordName { get; }
        public string MethodName { get; }
        public string ReturnType { get; }
        public EquatableArray<ParameterInfo> Parameters { get; }

        public QueryToGenerate(
            string recordName,
            string methodName,
            string returnType,
            EquatableArray<ParameterInfo> parameters)
        {
            RecordName = recordName;
            MethodName = methodName;
            ReturnType = returnType;
            Parameters = parameters;
        }

        public bool Equals(QueryToGenerate other)
            => RecordName == other.RecordName
            && MethodName == other.MethodName
            && ReturnType == other.ReturnType
            && Parameters == other.Parameters;

        public override bool Equals(object obj)
            => obj is QueryToGenerate other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (RecordName?.GetHashCode() ?? 0);
                h = h * 31 + (MethodName?.GetHashCode() ?? 0);
                h = h * 31 + (ReturnType?.GetHashCode() ?? 0);
                h = h * 31 + Parameters.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(QueryToGenerate left, QueryToGenerate right) => left.Equals(right);
        public static bool operator !=(QueryToGenerate left, QueryToGenerate right) => !left.Equals(right);
    }
}
