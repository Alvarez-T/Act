using System;
using System.Collections.Generic;

namespace YFex.State.History.Generator;

/// <summary>Represents one [Undoable] or class-level-derived property.</summary>
internal readonly record struct UndoablePropertyModel(
    string PropertyName,
    string FullTypeName,    // fully-qualified, nullable-annotated, for the cast in setter lambda
    string? ScopeName,      // null = property-level isolation; "" = class-level default
    string? ContextName,    // null = auto-created; non-null = explicit [UndoContext] property name
    int MergeWindowMs,
    bool IsReadOnly         // true = [Computed] or read-only → emit YFEX0707
) : IEquatable<UndoablePropertyModel>;

/// <summary>Represents a [UndoContext]-marked property on the class.</summary>
internal readonly record struct UndoContextPropertyModel(
    string PropertyName     // name of the UndoContext property
) : IEquatable<UndoContextPropertyModel>;

/// <summary>Per-class aggregated model used by UndoableEmitter.</summary>
internal readonly record struct UndoableClassModel(
    string Namespace,
    string ClassName,
    string FullyQualifiedClassName,   // global::Ns.ClassName — used in setter cast
    EquatableArray<string> TypeParameters,
    EquatableArray<UndoablePropertyModel> Properties,
    EquatableArray<UndoContextPropertyModel> ExplicitContexts,
    bool HasSinglePrimaryScope        // true → emit IUndoable; false → multi-scope, skip IUndoable
) : IEquatable<UndoableClassModel>;

/// <summary>Raw intermediate model emitted per attributed symbol before grouping.</summary>
internal readonly record struct UndoableRawModel(
    string Namespace,
    string ClassName,
    string FullyQualifiedClassName,
    EquatableArray<string> TypeParameters,
    UndoablePropertyModel? Property,       // null for [UndoContext]-only entries
    UndoContextPropertyModel? ContextProp, // null for [Undoable]-only entries
    bool HasError                          // true → suppress code gen for this entry
) : IEquatable<UndoableRawModel>;
