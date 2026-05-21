// Polyfills required when targeting netstandard2.0 with LangVersion 12.
// The C# compiler looks for these types in the compilation; if they are absent it errors.

using System;

namespace YFex.State.Generator
{
    // Enables C# 9+ init-only setters and record types on netstandard2.0.
    // NOTE: The compiler resolves IsExternalInit from System.Runtime.CompilerServices —
    // the block below this namespace provides it in the correct location.
    internal static class IsExternalInit { }

    // Enables C# 11+ required members on netstandard2.0.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    // Enables C# 11+ required members on netstandard2.0.
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}

namespace System.Runtime.CompilerServices
{
    // The compiler resolves this type by name to enable C# 9+ init-only setters
    // and record / record struct types when targeting netstandard2.0.
    internal static class IsExternalInit { }
}

namespace System.Diagnostics.CodeAnalysis
{
    // Enables [MemberNotNull] on netstandard2.0.
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
    internal sealed class MemberNotNullAttribute : Attribute
    {
        public MemberNotNullAttribute(string member) => Members = new[] { member };
        public MemberNotNullAttribute(params string[] members) => Members = members;
        public string[] Members { get; }
    }
}
