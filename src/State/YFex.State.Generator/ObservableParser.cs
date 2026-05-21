using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.State.Generator;

internal static class ObservableParser
{
    // Display format that includes nullable reference-type annotations (e.g. string?, Task<int>?)
    private static readonly SymbolDisplayFormat s_nullableFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // ── Syntax predicate (fast — no SemanticModel) ───────────────────────────

    public static bool IsCandidateSyntax(SyntaxNode node, CancellationToken _)
    {
        // Field declaration  →  [Observable] private string _foo = "";
        if (node is VariableDeclaratorSyntax)
            return node.Parent?.Parent is FieldDeclarationSyntax;

        // Property declaration  →  [Observable] public partial string Foo { get; set; }
        return node is PropertyDeclarationSyntax;
    }

    // ── Semantic transform (runs only on filtered nodes) ─────────────────────

    public static ObservablePropertyRawModel? Transform(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        ISymbol? symbol = ctx.TargetSymbol;
        if (symbol is null) return null;

        INamedTypeSymbol? containingType = symbol.ContainingType;
        if (containingType is null) return null;

        if (!IsPartialClass(containingType)) return null;
        if (!InheritsStateObject(containingType)) return null;

            string propertyName;
        string fieldName;
        string typeName;
        string? xmlDoc;
        bool isReadOnly;

        if (symbol is IFieldSymbol field)
        {
            fieldName = field.Name;
            propertyName = DerivePropertyName(fieldName);
            typeName = field.Type.ToDisplayString(s_nullableFormat);
            xmlDoc = field.GetDocumentationCommentXml();
            isReadOnly = field.IsReadOnly;
        }
        else if (symbol is IPropertySymbol property)
        {
            propertyName = property.Name;
            fieldName = $"__{char.ToLowerInvariant(propertyName[0])}{propertyName.Substring(1)}_backing";
            typeName = property.Type.ToDisplayString(s_nullableFormat);
            xmlDoc = property.GetDocumentationCommentXml();
            isReadOnly = property.IsReadOnly;
        }
        else
        {
            return null;
        }

        var allAttributes = symbol.GetAttributes();

        var equalityStrategy = ResolveEqualityStrategy(symbol, ct);
        string? customComparerType = ResolveCustomComparerType(allAttributes, ct);

        uint parentCount = CountParentObservableProperties(containingType, ct);
        bool isMvvm = InheritsMvvmStateObject(containingType);

        bool notifyOnTaskCompletion = HasNotifyOnTaskCompletionAttribute(allAttributes);
        string? taskResultTypeFullName = null;

        bool hasTaskTypeError = false;
        if (notifyOnTaskCompletion)
        {
            ITypeSymbol typeSymbol = symbol is IFieldSymbol f2 ? f2.Type : ((IPropertySymbol)symbol).Type;
            if (!ResolveTaskResultType(typeSymbol, ct, out taskResultTypeFullName))
            {
                hasTaskTypeError = true;
                notifyOnTaskCompletion = false;
            }
        }

        // Activation participation: true when the property type inherits from StateObject
        // and [IgnoreActivation] is not present.
        bool ignoreActivation = false;
        foreach (var a in allAttributes)
            if (a.AttributeClass?.Name == "IgnoreActivationAttribute") { ignoreActivation = true; break; }
        bool participatesInActivation = false;
        bool isNullableActivatable = false;
        if (!ignoreActivation)
        {
            ITypeSymbol propType = symbol is IFieldSymbol f3 ? f3.Type : ((IPropertySymbol)symbol).Type;
            // Strip nullability wrapper to check the underlying named type
            ITypeSymbol unwrapped = propType is INamedTypeSymbol nt2 &&
                nt2.NullableAnnotation == NullableAnnotation.Annotated
                ? nt2.WithNullableAnnotation(NullableAnnotation.None)
                : propType;
            participatesInActivation = unwrapped is INamedTypeSymbol activatableType
                && InheritsStateObjectOrIsStateObject(activatableType);
            if (participatesInActivation)
            {
                // null if reference type in nullable context or explicit ? annotation
                isNullableActivatable = propType.NullableAnnotation == NullableAnnotation.Annotated
                    || propType.TypeKind == TypeKind.Error // unknown type — be safe
                    || (propType is INamedTypeSymbol nt3 && nt3.IsReferenceType
                        && propType.NullableAnnotation != NullableAnnotation.NotAnnotated);
            }
        }

        // Validator types from [ValidateWith] and [ValidateAsync]
        var validatorTypes = ResolveValidatorTypes(allAttributes, "ValidateWithAttribute", ct);
        var asyncValidatorTypes = ResolveValidatorTypes(allAttributes, "ValidateAsyncAttribute", ct);

        return new ObservablePropertyRawModel(
            Namespace: containingType.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : containingType.ContainingNamespace.ToDisplayString(),
            ClassName: containingType.Name,
            TypeParameters: GetTypeParameters(containingType),
            FieldName: fieldName,
            PropertyName: propertyName,
            TypeName: typeName,
            XmlDoc: string.IsNullOrWhiteSpace(xmlDoc) ? null : xmlDoc,
            EqualityKind: equalityStrategy,
            CustomComparerType: customComparerType,
            IsSealed: containingType.IsSealed,
            ParentPropertyCount: parentCount,
            IsMvvm: isMvvm,
            IsReadOnly: isReadOnly,
            NotifyOnTaskCompletion: notifyOnTaskCompletion,
            TaskResultTypeFullName: taskResultTypeFullName,
            HasTaskTypeError: hasTaskTypeError,
            ParticipatesInActivation: participatesInActivation,
            IsNullableActivatable: isNullableActivatable,
            ValidatorTypes: validatorTypes,
            AsyncValidatorTypes: asyncValidatorTypes);
    }

    // ── Group per-property raw models into per-class models ──────────────────

    public static IEnumerable<ObservableClassModel> GroupByClass(
        ImmutableArray<ObservablePropertyRawModel> allProps,
        CancellationToken _)
    {
        var groups = new Dictionary<string, List<ObservablePropertyRawModel>>();

        foreach (var prop in allProps)
        {
            string key = string.IsNullOrEmpty(prop.Namespace)
                ? prop.ClassName
                : prop.Namespace + "." + prop.ClassName;
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new List<ObservablePropertyRawModel>();
            list.Add(prop);
        }

        foreach (var kvp in groups)
        {
            var props = kvp.Value;
            // Sort by property name — gives stable ordering within this class's own ID block.
            // IDs start at ParentPropertyCount so they never collide with ancestor IDs.
            props.Sort(static (a, b) =>
                StringComparer.Ordinal.Compare(a.PropertyName, b.PropertyName));

            var first = props[0];
            uint idBase = first.ParentPropertyCount;

            var propModels = new ObservablePropertyModel[props.Count];
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i];
                propModels[i] = new ObservablePropertyModel(
                    FieldName: p.FieldName,
                    PropertyName: p.PropertyName,
                    TypeName: p.TypeName,
                    XmlDoc: p.XmlDoc,
                    PropertyId: idBase + (uint)i,
                    EqualityKind: p.EqualityKind,
                    CustomComparerType: p.CustomComparerType,
                    IsReadOnly: p.IsReadOnly,
                    NotifyOnTaskCompletion: p.NotifyOnTaskCompletion,
                    TaskResultTypeFullName: p.TaskResultTypeFullName,
                    ParticipatesInActivation: p.ParticipatesInActivation,
                    IsNullableActivatable: p.IsNullableActivatable,
                    ValidatorTypes: p.ValidatorTypes,
                    AsyncValidatorTypes: p.AsyncValidatorTypes);
            }

            yield return new ObservableClassModel(
                Namespace: first.Namespace,
                ClassName: first.ClassName,
                TypeParameters: first.TypeParameters,
                Properties: new EquatableArray<ObservablePropertyModel>(propModels),
                ComputedProperties: EquatableArray<ComputedPropertyModel>.Empty,
                IsSealed: first.IsSealed,
                ParentPropertyCount: idBase,
                IsMvvm: first.IsMvvm);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsPartialClass(INamedTypeSymbol type)
    {
        foreach (var decl in type.DeclaringSyntaxReferences)
        {
            if (decl.GetSyntax() is ClassDeclarationSyntax cls)
            {
                foreach (var mod in cls.Modifiers)
                    if (mod.IsKind(SyntaxKind.PartialKeyword)) return true;
            }
        }
        return false;
    }

    private static bool InheritsStateObject(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name == "StateObject" &&
                current.ContainingNamespace?.ToDisplayString() == "YFex.State")
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static bool InheritsStateObjectOrIsStateObject(INamedTypeSymbol type)
    {
        // Accept StateObject itself or any type that inherits from it
        if (type.Name == "StateObject" &&
            type.ContainingNamespace?.ToDisplayString() == "YFex.State")
            return true;
        if (InheritsStateObject(type)) return true;

        // Also accept any type that implements IActivatable (e.g. FusionStateBinding<T>)
        // so the generator cascades Activate/Deactivate to it even without inheriting StateObject.
        return ImplementsIActivatable(type);
    }

    private static bool ImplementsIActivatable(INamedTypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.Name == "IActivatable" &&
                iface.ContainingNamespace?.ToDisplayString() == "YFex.State")
                return true;
        }
        return false;
    }

    private static bool InheritsMvvmStateObject(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name == "MvvmStateObject" &&
                current.ContainingNamespace?.ToDisplayString() == "YFex.State.Mvvm")
                return true;
            current = current.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Counts the total number of [Observable] properties declared on all ancestor classes.
    /// This becomes the base ID for the current class's own properties, ensuring IDs are stable
    /// across the inheritance hierarchy regardless of property name ordering in child classes.
    /// </summary>
    private static uint CountParentObservableProperties(INamedTypeSymbol type, CancellationToken ct)
    {
        uint count = 0;
        var current = type.BaseType;
        while (current is not null)
        {
            // Stop at framework base classes
            string name = current.Name;
            if (name == "StateObject" || name == "MvvmStateObject" || name == "Object")
                break;

            ct.ThrowIfCancellationRequested();

            foreach (var member in current.GetMembers())
            {
                if (member.Kind != SymbolKind.Property && member.Kind != SymbolKind.Field)
                    continue;
                foreach (var attr in member.GetAttributes())
                {
                    if (attr.AttributeClass?.Name == "ObservableAttribute")
                    {
                        count++;
                        break;
                    }
                }
            }
            current = current.BaseType;
        }
        return count;
    }

    private static string DerivePropertyName(string fieldName)
    {
        int start = 0;
        while (start < fieldName.Length && fieldName[start] == '_') start++;
        if (start >= fieldName.Length) return fieldName;
        return char.ToUpperInvariant(fieldName[start]) + fieldName.Substring(start + 1);
    }

    private static EquatableArray<string> GetTypeParameters(INamedTypeSymbol type)
    {
        if (type.TypeParameters.IsEmpty) return EquatableArray<string>.Empty;
        var names = new string[type.TypeParameters.Length];
        for (int i = 0; i < names.Length; i++)
            names[i] = type.TypeParameters[i].Name;
        return new EquatableArray<string>(names);
    }

    private static EqualityStrategy ResolveEqualityStrategy(ISymbol symbol, CancellationToken ct)
    {
        ITypeSymbol typeSymbol;
        if (symbol is IFieldSymbol f) typeSymbol = f.Type;
        else if (symbol is IPropertySymbol p) typeSymbol = p.Type;
        else return EqualityStrategy.Default;

        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "EqualityComparerAttribute")
                return EqualityStrategy.Custom;
        }

        if (typeSymbol is ITypeParameterSymbol) return EqualityStrategy.Default;

        string fullName = typeSymbol.ToDisplayString();

        return fullName switch
        {
            "float" or "System.Single" => EqualityStrategy.FloatNaN,
            "double" or "System.Double" => EqualityStrategy.DoubleNaN,
            "string" or "System.String" => EqualityStrategy.StringOrdinal,
            "bool" or "System.Boolean"
            or "byte" or "System.Byte"
            or "sbyte" or "System.SByte"
            or "short" or "System.Int16"
            or "ushort" or "System.UInt16"
            or "int" or "System.Int32"
            or "uint" or "System.UInt32"
            or "long" or "System.Int64"
            or "ulong" or "System.UInt64"
            or "char" or "System.Char"
            or "decimal" or "System.Decimal"
            or "System.Guid"
            or "System.DateTime"
            or "System.DateTimeOffset"
            or "System.TimeSpan"
            or "System.DateOnly"
            or "System.TimeOnly" => EqualityStrategy.DirectEquals,
            _ => ResolveForUnknownType(typeSymbol),
        };
    }

    private static EqualityStrategy ResolveForUnknownType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum) return EqualityStrategy.DirectEquals;
        if (type.IsValueType)
        {
            foreach (var member in type.GetMembers("op_Equality"))
                if (member is IMethodSymbol) return EqualityStrategy.DirectEquals;
            return EqualityStrategy.Default;
        }
        return EqualityStrategy.ReferenceType;
    }

    private static string? ResolveCustomComparerType(
        ImmutableArray<AttributeData> attributes, CancellationToken ct)
    {
        foreach (var attr in attributes)
        {
            if (attr.AttributeClass?.Name == "EqualityComparerAttribute" &&
                attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is INamedTypeSymbol comparerType)
            {
                return comparerType.ToDisplayString(s_nullableFormat);
            }
        }
        return null;
    }

    private static EquatableArray<string> ResolveValidatorTypes(
        ImmutableArray<AttributeData> attributes, string attributeName, CancellationToken ct)
    {
        List<string>? result = null;
        foreach (var attr in attributes)
        {
            if (attr.AttributeClass?.Name != attributeName) continue;
            if (attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is INamedTypeSymbol validatorType)
            {
                result ??= new List<string>();
                result.Add(validatorType.ToDisplayString(s_nullableFormat));
            }
        }
        return result is null
            ? EquatableArray<string>.Empty
            : new EquatableArray<string>(result.ToArray());
    }

    private static bool HasNotifyOnTaskCompletionAttribute(ImmutableArray<AttributeData> attributes)
    {
        foreach (var attr in attributes)
            if (attr.AttributeClass?.Name == "NotifyOnTaskCompletionAttribute") return true;
        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="type"/> is <c>Task</c> or <c>Task&lt;T&gt;</c>.
    /// For the generic case, <paramref name="taskResultTypeFullName"/> is set to T's fully-qualified name.
    /// </summary>
    private static bool ResolveTaskResultType(
        ITypeSymbol type, CancellationToken ct, out string? taskResultTypeFullName)
    {
        taskResultTypeFullName = null;

        // Strip nullability wrapper
        var unwrapped = type is INamedTypeSymbol named && named.NullableAnnotation == NullableAnnotation.Annotated
            ? named.WithNullableAnnotation(NullableAnnotation.None)
            : type;

        if (unwrapped is not INamedTypeSymbol namedType) return false;

        string ns = namedType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (ns != "System.Threading.Tasks") return false;

        if (namedType.Name == "Task" && namedType.TypeArguments.Length == 0)
            return true;

        if (namedType.Name == "Task" && namedType.TypeArguments.Length == 1)
        {
            taskResultTypeFullName = namedType.TypeArguments[0]
                .ToDisplayString(s_nullableFormat);
            return true;
        }

        return false;
    }
}

// Raw model capturing per-property data before grouping.
// Not stored in the incremental cache — only used transiently inside the pipeline.
internal readonly record struct ObservablePropertyRawModel(
    string Namespace,
    string ClassName,
    EquatableArray<string> TypeParameters,
    string FieldName,
    string PropertyName,
    string TypeName,
    string? XmlDoc,
    EqualityStrategy EqualityKind,
    string? CustomComparerType,
    bool IsSealed,
    uint ParentPropertyCount,
    bool IsMvvm,
    bool IsReadOnly,
    bool NotifyOnTaskCompletion,
    string? TaskResultTypeFullName,
    bool HasTaskTypeError,
    bool ParticipatesInActivation,
    bool IsNullableActivatable,
    EquatableArray<string> ValidatorTypes,
    EquatableArray<string> AsyncValidatorTypes
) : IEquatable<ObservablePropertyRawModel>;
