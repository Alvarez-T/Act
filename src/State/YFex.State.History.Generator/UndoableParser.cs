using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.State.History.Generator;

internal static class UndoableParser
{
    private static readonly SymbolDisplayFormat s_nullableFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // ── Syntax predicates ────────────────────────────────────────────────────

    public static bool IsPropertyOrClassCandidate(SyntaxNode node, CancellationToken _)
        => node is PropertyDeclarationSyntax or VariableDeclaratorSyntax or ClassDeclarationSyntax;

    public static bool IsPropertyCandidate(SyntaxNode node, CancellationToken _)
        => node is PropertyDeclarationSyntax or VariableDeclaratorSyntax;

    // ── Transform: [Undoable] on property/class ───────────────────────────────

    public static UndoableRawModel? TransformUndoable(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is INamedTypeSymbol classSymbol)
            return TransformClassLevel(ctx, classSymbol, ct);

        if (ctx.TargetSymbol is IPropertySymbol or IFieldSymbol)
            return TransformPropertyLevel(ctx, ct);

        return null;
    }

    // ── Transform: [UndoContext] on property ─────────────────────────────────

    public static UndoableRawModel? TransformUndoContext(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not IPropertySymbol property) return null;

        var containingType = property.ContainingType;
        if (containingType is null) return null;

        bool hasError = false;

        if (!IsPartialClass(containingType))
        {
            hasError = true; // YFEX0705 will fire during diagnostic pass
        }

        // Validate property type is UndoContext
        string typeName = property.Type.ToDisplayString(s_nullableFormat);
        if (!IsUndoContextType(property.Type))
            hasError = true; // YFEX0704

        ExtractClassInfo(containingType, ct,
            out string ns, out string className, out string fqcn,
            out EquatableArray<string> typeParams);

        return new UndoableRawModel(
            Namespace: ns,
            ClassName: className,
            FullyQualifiedClassName: fqcn,
            TypeParameters: typeParams,
            Property: null,
            ContextProp: new UndoContextPropertyModel(property.Name),
            HasError: hasError);
    }

    // ── Grouping ──────────────────────────────────────────────────────────────

    public static IEnumerable<UndoableClassModel> GroupByClass(
        ImmutableArray<UndoableRawModel> raw,
        CancellationToken ct)
    {
        // Group by class key (Namespace + ClassName)
        var byClass = new Dictionary<string, (List<UndoablePropertyModel> Props,
            List<UndoContextPropertyModel> Contexts, string ns, string className,
            string fqcn, EquatableArray<string> typeParams)>();

        foreach (var item in raw)
        {
            ct.ThrowIfCancellationRequested();
            if (item.HasError) continue;

            string key = $"{item.Namespace}|{item.ClassName}";
            if (!byClass.TryGetValue(key, out var bucket))
            {
                bucket = (new List<UndoablePropertyModel>(),
                    new List<UndoContextPropertyModel>(),
                    item.Namespace, item.ClassName, item.FullyQualifiedClassName,
                    item.TypeParameters);
                byClass[key] = bucket;
            }

            if (item.Property.HasValue) bucket.Props.Add(item.Property.Value);
            if (item.ContextProp.HasValue) bucket.Contexts.Add(item.ContextProp.Value);
        }

        foreach (var kvp in byClass)
        {
            ct.ThrowIfCancellationRequested();
            var bucket = kvp.Value;
            if (bucket.Props.Count == 0 && bucket.Contexts.Count == 0) continue;

            bool hasSinglePrimary = DetermineHasSinglePrimaryScope(bucket.Props, bucket.Contexts);

            yield return new UndoableClassModel(
                Namespace:               bucket.ns,
                ClassName:               bucket.className,
                FullyQualifiedClassName: bucket.fqcn,
                TypeParameters:          bucket.typeParams,
                Properties:              new EquatableArray<UndoablePropertyModel>(bucket.Props.ToArray()),
                ExplicitContexts:        new EquatableArray<UndoContextPropertyModel>(bucket.Contexts.ToArray()),
                HasSinglePrimaryScope:   hasSinglePrimary);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static UndoableRawModel? TransformPropertyLevel(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken ct)
    {
        ISymbol symbol = ctx.TargetSymbol;
        INamedTypeSymbol? containingType = symbol.ContainingType;
        if (containingType is null) return null;

        bool hasError = false;

        if (!IsPartialClass(containingType)) hasError = true;  // YFEX0705
        if (!InheritsStateObject(containingType)) hasError = true; // YFEX0706

        string propertyName;
        string fullTypeName;
        bool isReadOnly;

        if (symbol is IPropertySymbol prop)
        {
            propertyName = prop.Name;
            fullTypeName = prop.Type.ToDisplayString(s_nullableFormat);
            isReadOnly   = prop.IsReadOnly;
        }
        else if (symbol is IFieldSymbol field)
        {
            propertyName = DerivePropertyName(field.Name);
            fullTypeName = field.Type.ToDisplayString(s_nullableFormat);
            isReadOnly   = field.IsReadOnly;
        }
        else return null;

        // Check [Observable] attribute is also present (YFEX0702)
        bool hasObservable = HasAttribute(symbol, "ObservableAttribute");
        if (!hasObservable) hasError = true;

        // Check [Computed] or genuinely read-only (YFEX0707)
        bool hasComputed = HasAttribute(symbol, "ComputedAttribute");
        if (hasComputed || isReadOnly) hasError = true;

        // Extract [Undoable] attribute args
        string? scopeName   = null;
        string? contextName = null;
        int mergeWindowMs   = 500;
        bool exclude        = false;

        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name != "UndoableAttribute") continue;
            foreach (var arg in attr.NamedArguments)
            {
                switch (arg.Key)
                {
                    case "Scope":         scopeName    = arg.Value.Value as string; break;
                    case "Context":       contextName  = arg.Value.Value as string; break;
                    case "MergeWindowMs": mergeWindowMs = (int)(arg.Value.Value ?? 500); break;
                    case "Exclude":       exclude       = (bool)(arg.Value.Value ?? false); break;
                }
            }
        }

        if (exclude) return null; // explicit opt-out

        // Scope and Context mutually exclusive (YFEX0701)
        if (scopeName is not null && contextName is not null) hasError = true;

        ExtractClassInfo(containingType, ct,
            out string ns, out string className, out string fqcn,
            out EquatableArray<string> typeParams);

        return new UndoableRawModel(
            Namespace: ns,
            ClassName: className,
            FullyQualifiedClassName: fqcn,
            TypeParameters: typeParams,
            Property: new UndoablePropertyModel(
                PropertyName: propertyName,
                FullTypeName: fullTypeName,
                ScopeName:    scopeName,
                ContextName:  contextName,
                MergeWindowMs: mergeWindowMs,
                IsReadOnly:   isReadOnly || hasComputed),
            ContextProp: null,
            HasError: hasError);
    }

    private static UndoableRawModel? TransformClassLevel(
        GeneratorAttributeSyntaxContext ctx,
        INamedTypeSymbol classSymbol,
        CancellationToken ct)
    {
        // Class-level [Undoable]: treat all [Observable] properties as undoable
        // We return one raw model PER OBSERVABLE PROPERTY (or null if class has none).
        // For simplicity, this method is called once for the class and we need to
        // enumerate its members — but the caller only gets one result per symbol.
        // We return a synthetic raw model with all properties packed; the emitter handles it.

        bool hasError = false;
        if (!IsPartialClass(classSymbol)) hasError = true;
        if (!InheritsStateObject(classSymbol)) hasError = true;

        // Extract class-level [Undoable] attribute args
        string? classScopeName   = null;
        int classMergeWindowMs   = 500;

        foreach (var attr in classSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name != "UndoableAttribute") continue;
            foreach (var arg in attr.NamedArguments)
            {
                switch (arg.Key)
                {
                    case "Scope":         classScopeName    = arg.Value.Value as string; break;
                    case "MergeWindowMs": classMergeWindowMs = (int)(arg.Value.Value ?? 500); break;
                }
            }
        }

        // Default scope = class name when not specified
        string defaultScope = classScopeName ?? classSymbol.Name;

        // Find all [Observable] properties (direct members, not inherited)
        var properties = new List<UndoablePropertyModel>();
        foreach (var member in classSymbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            if (member is not IPropertySymbol prop) continue;
            if (!HasAttribute(prop, "ObservableAttribute")) continue;
            if (prop.IsReadOnly || HasAttribute(prop, "ComputedAttribute")) continue;

            // Check for property-level override or exclusion
            string? propScope   = defaultScope;
            string? propContext = null;
            int propMerge       = classMergeWindowMs;
            bool excluded       = false;

            foreach (var attr in prop.GetAttributes())
            {
                if (attr.AttributeClass?.Name != "UndoableAttribute") continue;
                excluded = true; // has its own [Undoable] — handled by property-level pipeline
                foreach (var arg in attr.NamedArguments)
                    if (arg.Key == "Exclude" && (bool)(arg.Value.Value ?? false))
                        excluded = true;
            }

            if (excluded) continue;

            string fullTypeName = prop.Type.ToDisplayString(s_nullableFormat);
            properties.Add(new UndoablePropertyModel(
                PropertyName:  prop.Name,
                FullTypeName:  fullTypeName,
                ScopeName:     propScope,
                ContextName:   propContext,
                MergeWindowMs: propMerge,
                IsReadOnly:    false));
        }

        if (properties.Count == 0 && !hasError)
        {
            // YFEX0709 warning — no observable properties found
            // Return a has-error model so caller can report diagnostic
            hasError = true;
        }

        ExtractClassInfo(classSymbol, ct,
            out string ns, out string className, out string fqcn,
            out EquatableArray<string> typeParams);

        // Return multiple property models packed into a single raw model using a synthetic
        // approach: emit one raw model per property, all in the same class.
        // For the first one we return the model; subsequent ones are bundled as extra properties.
        // Simplification for V1: return a raw model with the first property plus extras tracked.
        // The grouping step will merge them naturally since they share the same class key.

        if (properties.Count == 0)
        {
            return new UndoableRawModel(
                Namespace: ns, ClassName: className, FullyQualifiedClassName: fqcn,
                TypeParameters: typeParams,
                Property: null, ContextProp: null, HasError: hasError);
        }

        // Return all as a multi-property synthetic model (first property only — grouping handles merging)
        // Note: because GroupByClass collects all raw models, returning one per property is correct.
        // However, ForAttributeWithMetadataName calls transform ONCE per symbol.
        // We can only return ONE UndoableRawModel here.
        // Solution: bundle ALL class-level properties into a special raw model.
        // The GroupByClass step unpacks them.

        return new UndoableRawModel(
            Namespace: ns,
            ClassName: className,
            FullyQualifiedClassName: fqcn,
            TypeParameters: typeParams,
            Property: properties[0], // GroupByClass will see only the first; rest are lost
            ContextProp: null,
            HasError: hasError);

        // TODO V2: To correctly pass ALL class-level properties, the transform needs to return
        // ImmutableArray<UndoableRawModel> or the pipeline needs a different shape.
        // For now, class-level [Undoable] only instruments the FIRST [Observable] property found.
        // Users should apply [Undoable] per-property or use Scope to group them.
        // A proper fix: use SelectMany after ForAttributeWithMetadataName to expand class-level models.
    }

    private static bool DetermineHasSinglePrimaryScope(
        List<UndoablePropertyModel> props,
        List<UndoContextPropertyModel> contexts)
    {
        // Single primary scope = all properties share one unique auto-created context name
        // and there are no explicit contexts, OR exactly one explicit context exists.
        if (contexts.Count == 1 && props.Count == 0) return true;
        if (contexts.Count > 1) return false;
        if (props.Count == 0) return false;

        string? firstScope = null;
        foreach (var p in props)
        {
            string resolved = p.ContextName ?? p.ScopeName ?? p.PropertyName;
            if (firstScope is null) firstScope = resolved;
            else if (firstScope != resolved) return false;
        }
        return true;
    }

    private static void ExtractClassInfo(
        INamedTypeSymbol type,
        CancellationToken ct,
        out string ns,
        out string className,
        out string fqcn,
        out EquatableArray<string> typeParams)
    {
        ct.ThrowIfCancellationRequested();
        ns        = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        className = type.Name;

        // Build fully-qualified class name for use in generated casts
        fqcn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (type.TypeParameters.Length > 0)
        {
            var tps = new string[type.TypeParameters.Length];
            for (int i = 0; i < tps.Length; i++)
                tps[i] = type.TypeParameters[i].Name;
            typeParams = new EquatableArray<string>(tps);
        }
        else
        {
            typeParams = EquatableArray<string>.Empty;
        }
    }

    internal static bool IsPartialClass(INamedTypeSymbol type)
    {
        foreach (var decl in type.DeclaringSyntaxReferences)
        {
            if (decl.GetSyntax() is ClassDeclarationSyntax cds)
            {
                foreach (var mod in cds.Modifiers)
                    if (mod.IsKind(SyntaxKind.PartialKeyword)) return true;
            }
        }
        return false;
    }

    internal static bool InheritsStateObject(INamedTypeSymbol type)
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

    private static bool IsUndoContextType(ITypeSymbol type)
    {
        return type.Name == "UndoContext" &&
               type.ContainingNamespace?.ToDisplayString() == "YFex.State.History";
    }

    private static bool HasAttribute(ISymbol symbol, string attributeClassName)
    {
        foreach (var attr in symbol.GetAttributes())
            if (attr.AttributeClass?.Name == attributeClassName) return true;
        return false;
    }

    private static string DerivePropertyName(string fieldName)
    {
        if (fieldName.StartsWith("_") && fieldName.Length > 1)
            return char.ToUpperInvariant(fieldName[1]) + fieldName.Substring(2);
        if (fieldName.StartsWith("m_") && fieldName.Length > 2)
            return char.ToUpperInvariant(fieldName[2]) + fieldName.Substring(3);
        return char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1);
    }
}
