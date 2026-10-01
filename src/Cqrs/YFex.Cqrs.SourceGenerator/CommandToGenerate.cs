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

        /// <summary><see cref="ResultType"/> in ActualLab.Generators' proxy-signature format (null when no result).</summary>
        public string? ProxyResultType { get; }

        /// <summary><see cref="ResultType"/> in ActualLab.Generators' <c>KeepCode</c> format (null when no result).</summary>
        public string? KeepResultType { get; }

        public CommandToGenerate(
            string recordName,
            string methodName,
            EquatableArray<ParameterInfo> parameters,
            string? resultType,
            bool isQueueable,
            string? proxyResultType,
            string? keepResultType)
        {
            RecordName  = recordName;
            MethodName  = methodName;
            Parameters  = parameters;
            ResultType  = resultType;
            IsQueueable = isQueueable;
            ProxyResultType = proxyResultType;
            KeepResultType = keepResultType;
        }

        public bool Equals(CommandToGenerate other)
            => RecordName  == other.RecordName
            && MethodName  == other.MethodName
            && Parameters  == other.Parameters
            && ResultType  == other.ResultType
            && IsQueueable == other.IsQueueable
            && ProxyResultType == other.ProxyResultType
            && KeepResultType == other.KeepResultType;

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
                h = h * 31 + (ProxyResultType?.GetHashCode() ?? 0);
                return h;
            }
        }

        public static bool operator ==(CommandToGenerate l, CommandToGenerate r) => l.Equals(r);
        public static bool operator !=(CommandToGenerate l, CommandToGenerate r) => !l.Equals(r);
    }
}
