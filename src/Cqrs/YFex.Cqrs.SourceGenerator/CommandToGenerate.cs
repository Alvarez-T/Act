using System;

namespace YFex.Cqrs.SourceGenerator
{
    internal readonly struct CommandToGenerate : IEquatable<CommandToGenerate>
    {
        public string RecordName { get; }
        public string MethodName { get; }
        public EquatableArray<ParameterInfo> Parameters { get; }

        /// <summary>Null if ICommand (no result); non-null if ICommand&lt;TResult&gt;.</summary>
        public string? ResultType { get; }

        /// <summary>True if the record implements IQueueable.</summary>
        public bool IsQueueable { get; }

        public CommandToGenerate(
            string recordName,
            string methodName,
            EquatableArray<ParameterInfo> parameters,
            string? resultType,
            bool isQueueable)
        {
            RecordName  = recordName;
            MethodName  = methodName;
            Parameters  = parameters;
            ResultType  = resultType;
            IsQueueable = isQueueable;
        }

        public bool Equals(CommandToGenerate other)
            => RecordName  == other.RecordName
            && MethodName  == other.MethodName
            && Parameters  == other.Parameters
            && ResultType  == other.ResultType
            && IsQueueable == other.IsQueueable;

        public override bool Equals(object obj)
            => obj is CommandToGenerate other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (RecordName?.GetHashCode()  ?? 0);
                h = h * 31 + (MethodName?.GetHashCode()  ?? 0);
                h = h * 31 + Parameters.GetHashCode();
                h = h * 31 + (ResultType?.GetHashCode()  ?? 0);
                h = h * 31 + IsQueueable.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(CommandToGenerate l, CommandToGenerate r) => l.Equals(r);
        public static bool operator !=(CommandToGenerate l, CommandToGenerate r) => !l.Equals(r);
    }
}
