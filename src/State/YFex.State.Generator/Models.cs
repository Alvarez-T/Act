using System;

namespace YFex.State.Generator;

// ────────────────────────────────────────────────────────────────────────────
//  All model types are readonly record structs with EquatableArray<T> for any
//  collection field. NEVER store ISymbol or SyntaxNode — they root old
//  compilations and break incremental caching.
// ────────────────────────────────────────────────────────────────────────────

internal readonly record struct ObservableClassModel(
    string Namespace,
    string ClassName,
    EquatableArray<string> TypeParameters,
    EquatableArray<ObservablePropertyModel> Properties,
    EquatableArray<ComputedPropertyModel> ComputedProperties,
    bool IsSealed,
    /// <summary>
    /// Number of [Observable] properties declared on all ancestor classes.
    /// This class's own property IDs start at this value, forming a global ID block
    /// that never collides with parent IDs regardless of property name ordering.
    /// </summary>
    uint ParentPropertyCount,
    /// <summary>
    /// True when the class inherits from MvvmStateObject. The emitter only generates
    /// __mvvmArgsCache and GetPropertyChangedArgs when this is true.
    /// </summary>
    bool IsMvvm
) : IEquatable<ObservableClassModel>;

internal readonly record struct ObservablePropertyModel(
    string FieldName,
    string PropertyName,
    string TypeName,
    string? XmlDoc,
    uint PropertyId,
    EqualityStrategy EqualityKind,
    string? CustomComparerType,
    bool IsReadOnly,
    /// <summary>
    /// True when [NotifyOnTaskCompletion] is present. The emitter replaces the standard
    /// setter body with a SetFieldAndNotifyOnCompletion call and changes the backing field
    /// type to TaskNotifier / TaskNotifier&lt;T&gt;.
    /// </summary>
    bool NotifyOnTaskCompletion,
    /// <summary>
    /// Non-null only when NotifyOnTaskCompletion=true and the property type is Task&lt;T&gt;.
    /// Holds the fully-qualified name of T so the emitter can emit TaskNotifier&lt;T&gt;.
    /// </summary>
    string? TaskResultTypeFullName,
    /// <summary>
    /// True when the property type inherits from StateObject and [IgnoreActivation] is absent.
    /// The emitter includes this property in OnActivateCascading/OnDeactivateCascading overrides
    /// and injects activation-sync code in the setter.
    /// </summary>
    bool ParticipatesInActivation,
    /// <summary>
    /// Non-null only when ParticipatesInActivation=true and the type may be null (reference type
    /// or nullable value type). Drives whether the emitter emits null-conditional calls (?.).
    /// </summary>
    bool IsNullableActivatable,
    /// <summary>
    /// True when [Observable] is on a field (<c>_title</c> → <c>Title</c>). The emitter then writes a regular,
    /// non-partial property over the user's field instead of implementing a partial property with its own backing field.
    /// </summary>
    bool IsFieldBacked
) : IEquatable<ObservablePropertyModel>
{
    /// <summary>
    /// True when the setter calls the <c>partial void __OnValidate_X(in T value)</c> hook (D22): every
    /// writable, non-Task-notifier property. YFex.Validation.Generator mirrors this rule exactly — it may only
    /// implement hooks that are declared here (CS0759 otherwise).
    /// </summary>
    public bool HasValidationHook => !IsReadOnly && !NotifyOnTaskCompletion;
}

internal readonly record struct ComputedPropertyModel(
    string PropertyName,
    string TypeName,
    uint PropertyId,
    EquatableArray<string> Dependencies,
    string? XmlDoc
) : IEquatable<ComputedPropertyModel>;

internal enum EqualityStrategy : byte
{
    /// <summary>Direct == operator (bool, int, enum, decimal, Guid, DateTime, etc.)</summary>
    DirectEquals,
    /// <summary>NaN-aware float comparison.</summary>
    FloatNaN,
    /// <summary>NaN-aware double comparison.</summary>
    DoubleNaN,
    /// <summary>string.Equals(..., StringComparison.Ordinal)</summary>
    StringOrdinal,
    /// <summary>ReferenceEquals || (non-null) .Equals()</summary>
    ReferenceType,
    /// <summary>Custom [EqualityComparer(typeof(X))] attribute.</summary>
    Custom,
    /// <summary>EqualityComparer&lt;T&gt;.Default — fallback for unconstrained generic T.</summary>
    Default,
}
