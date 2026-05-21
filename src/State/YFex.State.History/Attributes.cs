using System;

namespace YFex.State.History
{
    /// <summary>
    /// Makes an <c>[Observable]</c> property (or all <c>[Observable]</c> properties on a class)
    /// participate in undo/redo. Must be combined with <see cref="YFex.State.ObservableAttribute"/>
    /// on individual properties, or applied at class level to opt in all observable properties.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field,
        AllowMultiple = false)]
    public sealed class UndoableAttribute : Attribute
    {
        /// <summary>
        /// Named shared scope. Properties with the same scope name share one auto-created
        /// <see cref="UndoContext"/>. When applied at class level, all <c>[Observable]</c>
        /// properties share this scope; defaults to the class name when omitted.
        /// Mutually exclusive with <see cref="Context"/>.
        /// </summary>
        public string? Scope { get; init; }

        /// <summary>
        /// References a <c>[UndoContext]</c>-marked property on the same class by name.
        /// Use <c>nameof()</c> for compile-time safety.
        /// Mutually exclusive with <see cref="Scope"/>.
        /// </summary>
        public string? Context { get; init; }

        /// <summary>
        /// Milliseconds for same-property consecutive change coalescing. Default 500 ms.
        /// Set to 0 to record every change as a separate undo entry.
        /// </summary>
        public int MergeWindowMs { get; init; } = 500;

        /// <summary>
        /// When <see cref="UndoableAttribute"/> is applied at class level, set this to
        /// <see langword="true"/> on a specific property to opt it out of undo tracking.
        /// </summary>
        public bool Exclude { get; init; }
    }

    /// <summary>
    /// Marks an <see cref="UndoContext"/> property as explicitly managed by the user.
    /// The generator exposes <c>UndoCommand</c>/<c>RedoCommand</c> wrappers on the owning class.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class UndoContextAttribute : Attribute { }

    /// <summary>
    /// Makes an <c>[Observable]</c> property of type <see cref="YFex.State.Collections.StateList{T}"/>
    /// participate in collection-level undo/redo. The generator subscribes an
    /// <see cref="UndoableCollectionObserver{T}"/> in <c>OnActivated</c> / <c>OnDeactivated</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class UndoableCollectionAttribute : Attribute
    {
        /// <summary>Named shared scope (same as <see cref="UndoableAttribute.Scope"/>).</summary>
        public string? Scope { get; init; }

        /// <summary>
        /// References a <c>[UndoContext]</c>-marked property on the same class (same as
        /// <see cref="UndoableAttribute.Context"/>).
        /// </summary>
        public string? Context { get; init; }
    }
}
