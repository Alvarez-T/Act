using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Validation.Generator;

/// <summary>Turns a type declaration into an equatable <see cref="TypeModel"/>.</summary>
internal static partial class Parser
{
    private const string Ns = "YFex.Validation";
    private const string DataAnnotationsNs = "System.ComponentModel.DataAnnotations";
    private const string SeverityType = "global::YFex.Validation.Severity";

    internal static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat PlainTypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    // Attribute simple names (with and without the "Attribute" suffix) that make a type a candidate.
    private static readonly HashSet<string> s_candidateNames = new(StringComparer.Ordinal)
    {
        "Validatable", "Required", "NotEmpty", "Length", "Range", "Pattern", "Email", "ValidateWith",
        "Each", "Invariant", "FieldCode",
        "StringLength", "MinLength", "MaxLength", "RegularExpression", "EmailAddress",
        // view models (07): contracts, mapping, timing, obsolete State validators
        "ValidatesAs", "MapsTo", "ValidationOptions", "ValidateOn", "ValidateAsync",
        // YFex.Rules decision projection (07 §9.3)
        "ConstraintsFrom",
    };

    // ── Syntax predicate (no semantic model) ─────────────────────────────────────

    public static bool IsCandidate(SyntaxNode node, CancellationToken ct)
    {
        if (node is not TypeDeclarationSyntax type || node is InterfaceDeclarationSyntax) return false;
        if (HasCandidateAttribute(type.AttributeLists)) return true;
        if (type.ParameterList is { } parameters)
            foreach (var p in parameters.Parameters)
                if (HasCandidateAttribute(p.AttributeLists)) return true;
        foreach (var member in type.Members)
        {
            if (member is BaseTypeDeclarationSyntax) continue; // nested types are their own candidates
            if (HasCandidateAttribute(member.AttributeLists)) return true;
        }
        return false;
    }

    private static bool HasCandidateAttribute(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var list in lists)
            foreach (var attr in list.Attributes)
            {
                string name = attr.Name switch
                {
                    QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                    AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
                    SimpleNameSyntax s => s.Identifier.ValueText,
                    _ => attr.Name.ToString(),
                };
                if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 9);
                if (s_candidateNames.Contains(name)) return true;
            }
        return false;
    }

    // ── Semantic transform ───────────────────────────────────────────────────────

    public static TypeModel? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var node = (TypeDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(node, ct) is not INamedTypeSymbol type) return null;

        // Partial types: build the model once, from the first declaration.
        var first = type.DeclaringSyntaxReferences[0];
        if (first.SyntaxTree != node.SyntaxTree || first.Span != node.Span) return null;

        if (type.IsRefLikeType) return null;
        if (IsStateObject(type))   // view models: setter hooks declared by the State generator (D22)
            return HasVmValidationSurface(type) ? BuildVmModel(type, ctx.SemanticModel.Compilation, ct) : null;
        if (!HasValidationSurface(type)) return null;

        return BuildModel(type, ctx.SemanticModel.Compilation, ct);
    }

    internal static TypeModel BuildModel(INamedTypeSymbol type, Compilation compilation, CancellationToken ct,
        IReadOnlyDictionary<string, FluentMemberModel>? fluent = null, EquatableArray<string> compiledFluentKeys = default)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var location = LocationInfo.From(type.Locations.FirstOrDefault());

        bool canEmit = true;
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            if (IsPartial(t)) continue;
            diagnostics.Add(Diag("YFV0001", location, type.ToDisplayString()));
            canEmit = false;
            break;
        }

        // Generated container class names (YFV0013 renames on clash).
        var memberNames = new HashSet<string>(type.GetMembers().Select(m => m.Name), StringComparer.Ordinal);
        string ClassName(string name)
        {
            if (!memberNames.Contains(name)) return name;
            diagnostics.Add(Diag("YFV0013", location, name));
            return "Validation" + name;
        }
        string fieldIdsClass = ClassName("FieldIds");
        string fieldsClass = ClassName("Fields");
        string rulesClass = ClassName("Rules");
        string invariantIdsClass = ClassName("InvariantIds");

        // Base type that is itself validatable → its rules run first, ids continue after its members.
        string? baseFqn = null;
        int baseCount = 0;
        if (type.BaseType is { SpecialType: SpecialType.None } bt && bt.TypeKind == TypeKind.Class && IsValidatableType(bt))
        {
            baseFqn = bt.ToDisplayString(PlainTypeFormat);
            baseCount = CountMembersInHierarchy(bt);
        }

        var discovered = DiscoverMembers(type, fluent?.Keys.ToList());
        var boolMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in type.GetMembers())
            if (m is IPropertySymbol { Type.SpecialType: SpecialType.System_Boolean } or IFieldSymbol { Type.SpecialType: SpecialType.System_Boolean })
                boolMembers.Add(m.Name);

        var members = new List<MemberModel>(discovered.Count);
        ushort id = (ushort)baseCount;
        foreach (var d in discovered)
        {
            ct.ThrowIfCancellationRequested();
            FluentMemberModel? fm = null;
            fluent?.TryGetValue(d.Name, out fm);
            members.Add(BuildMember(d, id++, type, boolMembers, diagnostics, fm));
        }

        var invariants = BuildInvariants(type, members, compilation, diagnostics, ct);

        string keyword = type.IsRecord
            ? type.TypeKind == TypeKind.Struct ? "record struct" : "record"
            : type.TypeKind == TypeKind.Struct ? "struct" : "class";

        var containers = new List<ContainerModel>();
        for (var c = type.ContainingType; c is not null; c = c.ContainingType)
        {
            string ck = c.IsRecord
                ? c.TypeKind == TypeKind.Struct ? "record struct" : "record"
                : c.TypeKind switch { TypeKind.Struct => "struct", TypeKind.Interface => "interface", _ => "class" };
            containers.Insert(0, new ContainerModel(ck, NameWithTypeParameters(c)));
        }

        string fqn = type.ToDisplayString(PlainTypeFormat);
        bool isGeneric = false;
        bool accessible = true;
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            if (t.TypeParameters.Length > 0) isGeneric = true;
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                accessible = false;
        }

        return new TypeModel(
            Namespace: type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
            Containers: new EquatableArray<ContainerModel>(containers.ToArray()),
            Keyword: keyword,
            NameWithTypeParameters: NameWithTypeParameters(type),
            FullyQualifiedName: fqn,
            HintName: HintName(fqn),
            IsGeneric: isGeneric,
            IsRegistrable: !isGeneric && accessible,
            BaseValidatableFqn: baseFqn,
            BaseMemberCount: baseCount,
            FieldIdsClass: fieldIdsClass,
            FieldsClass: fieldsClass,
            RulesClass: rulesClass,
            InvariantIdsClass: invariantIdsClass,
            Members: new EquatableArray<MemberModel>(members.ToArray()),
            Invariants: new EquatableArray<InvariantModel>(invariants.ToArray()),
            Diagnostics: new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()),
            CanEmit: canEmit,
            Location: location,
            MetadataName: MetadataNameOf(type),
            CompiledFluentKeys: compiledFluentKeys);
    }

    // ── Members ──────────────────────────────────────────────────────────────────

    private readonly struct DiscoveredMember
    {
        public DiscoveredMember(ISymbol symbol, ITypeSymbol type, IReadOnlyList<AttributeData> attributes, string? name = null)
        {
            Symbol = symbol;
            Type = type;
            Attributes = attributes;
            Name = name ?? symbol.Name;
        }

        /// <summary>Member name used in paths (an [Observable] field `_name` becomes its generated property `Name`).</summary>
        public string Name { get; }
        public ISymbol Symbol { get; }
        public ITypeSymbol Type { get; }
        public IReadOnlyList<AttributeData> Attributes { get; }
    }

    /// <summary>Public instance input members in declaration order; record positional parameters
    /// contribute their attributes to the matching property.</summary>
    private static List<DiscoveredMember> DiscoverMembers(INamedTypeSymbol type, ICollection<string>? fluentMembers = null)
    {
        var positional = new Dictionary<string, IParameterSymbol>(StringComparer.Ordinal);
        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.Parameters.Length == 0) continue;
            bool isPrimary = ctor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax);
            if (!isPrimary) continue;
            foreach (var p in ctor.Parameters) positional[p.Name] = p;
            break;
        }

        var result = new List<DiscoveredMember>();
        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared && member is not IPropertySymbol) continue;
            if (member.DeclaredAccessibility != Accessibility.Public) continue;

            switch (member)
            {
                case IPropertySymbol p when !p.IsIndexer:
                {
                    var attrs = new List<AttributeData>(p.GetAttributes());
                    bool isPositional = type.IsRecord && positional.TryGetValue(p.Name, out var param);
                    if (isPositional) attrs.InsertRange(0, positional[p.Name].GetAttributes());
                    bool hasRule = attrs.Any(IsRuleLikeAttribute) || fluentMembers?.Contains(p.Name) == true;
                    if (p.SetMethod is null && !isPositional && !hasRule) continue;
                    result.Add(new DiscoveredMember(p, p.Type, attrs));
                    break;
                }
                case IFieldSymbol f when !f.IsConst && !f.IsImplicitlyDeclared:
                    result.Add(new DiscoveredMember(f, f.Type, f.GetAttributes()));
                    break;
            }
        }
        return result;
    }

    private static int CountMembersInHierarchy(INamedTypeSymbol type)
    {
        int count = DiscoverMembers(type).Count;
        if (type.BaseType is { SpecialType: SpecialType.None } b && b.TypeKind == TypeKind.Class && IsValidatableType(b))
            count += CountMembersInHierarchy(b);
        return count;
    }

    private static MemberModel BuildMember(DiscoveredMember d, ushort id, INamedTypeSymbol owner,
        HashSet<string> boolMembers, List<DiagnosticInfo> diagnostics, FluentMemberModel? fluent = null)
    {
        string name = d.Name;
        ITypeSymbol declared = d.Type;
        var loc = LocationInfo.From(d.Symbol.Locations.FirstOrDefault());

        var info = TypeInfo.Of(declared);
        string typeFqn = declared.ToDisplayString(TypeFormat);
        string checkParam = typeFqn;
        if (declared.IsReferenceType && declared.NullableAnnotation != NullableAnnotation.Annotated && !typeFqn.EndsWith("?", StringComparison.Ordinal))
            checkParam = typeFqn + "?";

        string? fieldCode = null;
        bool hasEach = false;
        foreach (var a in d.Attributes)
        {
            if (Is(a, Ns, "FieldCodeAttribute") && a.ConstructorArguments.Length == 1)
                fieldCode = a.ConstructorArguments[0].Value as string;
            if (Is(a, Ns, "EachAttribute")) hasEach = true;
        }
        if (fluent is { Each: true }) hasEach = true;

        // Our attributes win over DataAnnotations for the same rule kind (YFV0010).
        var ownKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in d.Attributes)
            if (a.AttributeClass?.ContainingNamespace.ToDisplayString() == Ns && RuleKindName(a) is { } k) ownKinds.Add(k);

        var memberRules = new List<RuleModel>();
        var elementRules = new List<RuleModel>();
        var asyncRules = new List<RuleModel>();

        TypeInfo? element = null;
        if (hasEach)
        {
            if (info.Collection is CollectionKind.None or CollectionKind.CountOnly || info.ElementType is null)
                diagnostics.Add(Diag("YFV0002", loc, "Each", name, declared.ToDisplayString()));
            else
                element = TypeInfo.Of(info.ElementType);
        }

        foreach (var a in d.Attributes)
        {
            string? kindName = RuleKindName(a);
            if (kindName is null) continue;

            bool isDataAnnotation = a.AttributeClass?.ContainingNamespace.ToDisplayString() == DataAnnotationsNs;
            if (isDataAnnotation && ownKinds.Contains(kindName))
            {
                diagnostics.Add(Diag("YFV0010", loc, name, kindName));
                continue;
            }

            // With [Each], presence rules stay on the collection; every other rule targets elements.
            bool onElement = element is not null && kindName is not ("required" or "notempty");
            TypeInfo target = onElement ? element!.Value : info;

            var rule = BuildRule(RuleInput.From(a), kindName, isDataAnnotation, target, name, fieldCode, boolMembers, loc, diagnostics);
            if (rule is null) continue;
            if (rule.Kind == RuleKind.AsyncValueRule) { if (!onElement) asyncRules.Add(rule); continue; }
            (onElement ? elementRules : memberRules).Add(rule);
        }

        // Fluent rules (03 §4.4): union with the attribute rules; same rule with other arguments → YFV0011.
        if (fluent is not null)
        {
            foreach (var r in fluent.Rules)
            {
                var list = r.Kind == RuleKind.AsyncValueRule ? asyncRules : memberRules;
                if (list.Contains(r)) continue;
                if (list.Any(x => x.Kind == r.Kind && x.RuleTypeFqn == r.RuleTypeFqn && x.When == r.When && x.Profile == r.Profile))
                {
                    diagnostics.Add(Diag("YFV0011", loc, name, r.Kind.ToString()));
                    continue;
                }
                list.Add(r);
            }
        }

        EachModel? each = null;
        if (element is { } e)
        {
            string? elementValidatable = IsValidatableType(e.Underlying) ? e.Underlying.ToDisplayString(PlainTypeFormat) : null;
            each = new EachModel(
                e.Declared.ToDisplayString(TypeFormat), info.Collection, e.Shape, e.Declared.IsReferenceType,
                e.IsNullableValue, elementValidatable, new EquatableArray<RuleModel>(elementRules.ToArray()));
        }

        string? nested = null;
        if (each is null && info.Shape == MemberShape.Other && IsValidatableType(info.Underlying))
            nested = info.Underlying.ToDisplayString(PlainTypeFormat);

        bool hasRequired = memberRules.Any(r => r.Kind == RuleKind.Required);
        bool canInfer = !hasRequired && declared.IsReferenceType
                        && declared.NullableAnnotation == NullableAnnotation.NotAnnotated
                        && info.Shape is MemberShape.String or MemberShape.Other;

        return new MemberModel(
            Name: name,
            TypeFqn: typeFqn,
            CheckParamType: checkParam,
            Id: id,
            Shape: info.Shape,
            Collection: info.Collection,
            IsReferenceType: declared.IsReferenceType,
            IsNullableValue: info.IsNullableValue,
            CanInferRequired: canInfer,
            NestedValidatableFqn: nested,
            NestedIsNullable: declared.IsReferenceType || info.IsNullableValue,
            Each: each,
            Rules: new EquatableArray<RuleModel>(memberRules.ToArray()),
            HasAsyncRules: asyncRules.Count > 0,
            AsyncRules: new EquatableArray<RuleModel>(asyncRules.ToArray()),
            Json: JsonShape.Of(declared, IsValidatableType));
    }

    /// <summary>Normalized rule kind of a YFex.Validation or DataAnnotations attribute; null if not a rule.</summary>
    private static string? RuleKindName(AttributeData a)
    {
        var cls = a.AttributeClass;
        if (cls is null) return null;
        string ns = cls.ContainingNamespace.ToDisplayString();
        if (ns == Ns)
        {
            return cls.MetadataName switch
            {
                "RequiredAttribute" => "required",
                "NotEmptyAttribute" => "notempty",
                "LengthAttribute" => "len",
                "RangeAttribute" => "range",
                "PatternAttribute" => "pattern",
                "EmailAttribute" => "email",
                "ValidateWithAttribute`1" => "with",
                _ => null,
            };
        }
        if (ns == DataAnnotationsNs)
        {
            return cls.MetadataName switch
            {
                "RequiredAttribute" => "required",
                "StringLengthAttribute" or "MinLengthAttribute" or "MaxLengthAttribute" => "len",
                "RangeAttribute" => "range",
                "RegularExpressionAttribute" => "pattern",
                "EmailAddressAttribute" => "email",
                _ => null,
            };
        }
        return null;
    }

    private static bool IsRuleLikeAttribute(AttributeData a)
        => RuleKindName(a) is not null
           || Is(a, Ns, "EachAttribute") || Is(a, Ns, "FieldCodeAttribute");

    // ── Rules ────────────────────────────────────────────────────────────────────

    private static RuleModel? BuildRule(RuleInput a, string kindName, bool isDataAnnotation, TypeInfo target,
        string memberName, string? fieldCode, HashSet<string> boolMembers, LocationInfo? loc, List<DiagnosticInfo> diagnostics)
    {
        // Common named arguments (YFex attributes only).
        int severity = 2;
        string? code = null, profile = null, when = null;
        bool coerce = false;
        foreach (var kv in a.Named)
        {
            switch (kv.Key)
            {
                case "Severity" when kv.Value.Value is int s: severity = s; break;
                case "Severity" when kv.Value.Value is byte b: severity = b; break;
                case "Code": code = kv.Value.Value as string; break;
                case "Profile": profile = kv.Value.Value as string; break;
                case "When": when = kv.Value.Value as string; break;
                case "Coerce" when kv.Value.Value is bool c: coerce = c; break;
            }
        }

        if (when is not null && !boolMembers.Contains(when))
        {
            diagnostics.Add(Diag("YFV0015", loc, when));
            return null;
        }

        string severityExpr = SeverityType + "." + (severity switch { 0 => "Info", 1 => "Warning", _ => "Error" });
        string CodeExpr(string defaultCode) => Literal(code ?? fieldCode ?? defaultCode);

        RuleModel Make(RuleKind kind, string codeExpr) => new(
            kind, codeExpr, severity == 2, severityExpr, when, profile,
            0, 0, null, null, null, null, null, null, null, false, false, null, null, false, null, null);

        void NotApplicable(string rule) => diagnostics.Add(Diag("YFV0002", loc, rule, memberName, target.Declared.ToDisplayString()));

        switch (kindName)
        {
            case "required":
                return Make(RuleKind.Required, CodeExpr("required"));

            case "notempty":
                if (target.Shape is not (MemberShape.String or MemberShape.Collection)) { NotApplicable("NotEmpty"); return null; }
                return Make(RuleKind.NotEmpty, CodeExpr("notempty"));

            case "email":
                if (target.Shape != MemberShape.String) { NotApplicable("Email"); return null; }
                return Make(RuleKind.Email, CodeExpr("email"));

            case "pattern":
            {
                if (target.Shape != MemberShape.String) { NotApplicable("Pattern"); return null; }
                string? pattern = a.Args.Length > 0 ? a.Args[0].Value as string : null;
                if (pattern is null) return null;
                try { _ = new Regex(pattern); }
                catch (ArgumentException ex)
                {
                    diagnostics.Add(Diag("YFV0007", loc, memberName, ex.Message));
                    return null;
                }
                return Make(RuleKind.Pattern, CodeExpr("pattern")) with { Pattern = pattern };
            }

            case "len":
            {
                if (target.Shape is not (MemberShape.String or MemberShape.Collection)) { NotApplicable("Length"); return null; }
                int min = 0, max = int.MaxValue;
                string attrName = a.AttributeMetadataName;
                var args = a.Args;
                if (!isDataAnnotation)
                {
                    if (args.Length > 0 && args[0].Value is int mn) min = mn;
                    if (args.Length > 1 && args[1].Value is int mx) max = mx;
                }
                else if (attrName == "StringLengthAttribute")
                {
                    if (args.Length > 0 && args[0].Value is int mx) max = mx;
                    foreach (var kv in a.Named)
                        if (kv.Key == "MinimumLength" && kv.Value.Value is int ml) min = ml;
                }
                else if (attrName == "MinLengthAttribute")
                {
                    if (args.Length > 0 && args[0].Value is int ml) min = ml;
                }
                else if (attrName == "MaxLengthAttribute")
                {
                    if (args.Length > 0 && args[0].Value is int mx) max = mx;
                }

                if (min < 0 || max < min)
                {
                    diagnostics.Add(Diag("YFV0006", loc, memberName, target.Declared.ToDisplayString(), $"length bounds {min}..{max}"));
                    return null;
                }
                string defaultCode = max == int.MaxValue ? "len.min" : min == 0 ? "len.max" : "len";
                return Make(RuleKind.Length, CodeExpr(defaultCode)) with { MinLength = min, MaxLength = max };
            }

            case "range":
                return BuildRange(a, isDataAnnotation, target, memberName, loc, diagnostics, Make(RuleKind.Range, CodeExpr("range")), coerce);

            case "with":
                return BuildValidateWith(a, target, memberName, loc, diagnostics, Make, code ?? fieldCode);
        }
        return null;
    }

    private static RuleModel? BuildValidateWith(RuleInput a, TypeInfo target, string memberName, LocationInfo? loc,
        List<DiagnosticInfo> diagnostics, Func<RuleKind, string, RuleModel> make, string? codeOverride)
    {
        if (a.RuleType is not { } ruleType)
            return null;

        string ruleFqn = ruleType.ToDisplayString(PlainTypeFormat);
        bool hasArg = a.Args.Length == 1;
        ArgValue arg = hasArg ? a.Args[0] : default;

        INamedTypeSymbol? textRule = null, textRuleArg = null, valueRule = null, valueRuleArg = null, asyncRule = null;
        foreach (var i in ruleType.AllInterfaces)
        {
            if (i.ContainingNamespace.ToDisplayString() != Ns) continue;
            switch (i.MetadataName)
            {
                case "ITextRule": textRule = i; break;
                case "ITextRule`1": textRuleArg = i; break;
                case "IValueRule`1" when SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], target.Underlying): valueRule = i; break;
                case "IValueRule`2" when SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], target.Underlying): valueRuleArg = i; break;
                case "IAsyncValueRule`1" when SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], target.Declared)
                                              || SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], target.Underlying):
                    asyncRule = i; break;
            }
        }

        string codeExpr = codeOverride is not null ? Literal(codeOverride) : ruleFqn + ".Code";

        if (hasArg)
        {
            INamedTypeSymbol? withArg = target.Shape == MemberShape.String && textRuleArg is not null ? textRuleArg : valueRuleArg;
            if (withArg is not null)
            {
                ITypeSymbol argType = withArg.TypeArguments[withArg.TypeArguments.Length - 1];
                string argLiteral = arg.IsPrimitive ? arg.CSharp : "default";
                string argExpr = $"({argType.ToDisplayString(PlainTypeFormat)})({argLiteral})";
                return make(SymbolEqualityComparer.Default.Equals(withArg, textRuleArg) ? RuleKind.TextRule : RuleKind.ValueRule, codeExpr) with
                {
                    RuleTypeFqn = ruleFqn,
                    HasArgument = true,
                    ArgumentExpr = argExpr,
                    ArgumentRawExpr = RawFromConstant(arg),
                };
            }
        }
        else
        {
            if (target.Shape == MemberShape.String && textRule is not null)
                return make(RuleKind.TextRule, codeExpr) with { RuleTypeFqn = ruleFqn };
            if (valueRule is not null)
                return make(RuleKind.ValueRule, codeExpr) with { RuleTypeFqn = ruleFqn };
            if (asyncRule is not null)
                return make(RuleKind.AsyncValueRule, codeExpr) with
                {
                    RuleTypeFqn = ruleFqn,
                    UnwrapNullable = target.IsNullableValue
                                     && !SymbolEqualityComparer.Default.Equals(asyncRule.TypeArguments[0], target.Declared),
                };
        }

        diagnostics.Add(Diag("YFV0005", loc, ruleType.ToDisplayString(), target.Declared.ToDisplayString()));
        return null;
    }

    private static string? RawFromConstant(ArgValue c) => c.Value switch
    {
        int or long or double or bool => $"global::YFex.RawValue.From({c.CSharp})",
        string => $"global::YFex.RawValue.From({c.CSharp})",
        short or byte or sbyte or ushort => $"global::YFex.RawValue.From((int){c.CSharp})",
        uint => $"global::YFex.RawValue.From((long){c.CSharp})",
        float => $"global::YFex.RawValue.From((double){c.CSharp})",
        _ => null,
    };

    // ── Range ────────────────────────────────────────────────────────────────────

    private static RuleModel? BuildRange(RuleInput a, bool isDataAnnotation, TypeInfo target, string memberName,
        LocationInfo? loc, List<DiagnosticInfo> diagnostics, RuleModel baseRule, bool coerce)
    {
        if (target.Shape != MemberShape.Comparable)
        {
            diagnostics.Add(Diag("YFV0002", loc, "Range", memberName, target.Declared.ToDisplayString()));
            return null;
        }

        var args = a.Args;
        // DataAnnotations Range(Type, string, string): skip the Type argument.
        int offset = isDataAnnotation && args.Length == 3 ? 1 : 0;
        if (args.Length < offset + 2) return null;
        ArgValue min = args[offset], max = args[offset + 1];

        ITypeSymbol t = target.Underlying;
        string tFqn = t.ToDisplayString(PlainTypeFormat);
        string typeName = t.ToDisplayString();

        void Invalid(string reason) => diagnostics.Add(Diag("YFV0006", loc, memberName, typeName, reason));

        // 1) Primitive numbers: literals + operators; integer types also get consts.
        if (IsPrimitiveNumber(t.SpecialType))
        {
            if (!TryDecimal(min, out decimal dMin) || !TryDecimal(max, out decimal dMax))
            {
                Invalid("bounds must be numeric");
                return null;
            }
            if (dMin > dMax) { Invalid($"min {dMin} > max {dMax}"); return null; }
            if (IsInteger(t.SpecialType))
            {
                if (decimal.Truncate(dMin) != dMin || decimal.Truncate(dMax) != dMax
                    || !FitsInteger(t.SpecialType, dMin) || !FitsInteger(t.SpecialType, dMax))
                {
                    Invalid("bounds must be whole numbers within the type's range");
                    return null;
                }
            }

            string minLit = NumberLiteral(t.SpecialType, dMin, min);
            string maxLit = NumberLiteral(t.SpecialType, dMax, max);
            bool isInt = IsInteger(t.SpecialType);
            return baseRule with
            {
                MinExpr = minLit,
                MaxExpr = maxLit,
                MinRawExpr = RawNumber(t.SpecialType, minLit),
                MaxRawExpr = RawNumber(t.SpecialType, maxLit),
                MinConstLiteral = isInt ? minLit : null,
                MaxConstLiteral = isInt ? maxLit : null,
                BoundsTypeFqn = tFqn,
                UsesOperators = true,
                Coerce = coerce,
            };
        }

        string? minExpr, maxExpr;
        bool isRawValue = ImplementsRawValue(t);
        bool rawSupported = isRawValue || s_rawTypes.Contains(tFqn);

        // 2) IRawValue types (Money, Mass, an app's Temperature…) with numeric bounds: the plain number read as the
        //    type (T.TryFromRaw — Money(10), Mass in grams, Percentual 10 %), evaluated once into a static field.
        if (isRawValue && min.Value is not string && max.Value is not string)
        {
            string factory = "global::YFex.RawValue.From({0}).As<" + tFqn + ">()";
            if (!TryDecimal(min, out decimal dMin) || !TryDecimal(max, out decimal dMax))
            {
                Invalid("bounds must be numeric (use string bounds for decimals)");
                return null;
            }
            if (dMin > dMax) { Invalid($"min {dMin} > max {dMax}"); return null; }
            minExpr = string.Format(CultureInfo.InvariantCulture, factory, DecimalLiteral(dMin));
            maxExpr = string.Format(CultureInfo.InvariantCulture, factory, DecimalLiteral(dMax));
        }
        // 3) Dates / times / other parsable types: string bounds parsed with invariant culture.
        else
        {
            if (min.Value is not string sMin || max.Value is not string sMax)
            {
                Invalid("use string bounds for this type");
                return null;
            }
            if (s_dateTypes.Contains(tFqn))
            {
                bool isTime = tFqn is "global::System.TimeOnly" or "global::System.TimeSpan";
                bool ok = isTime
                    ? TimeSpan.TryParse(sMin, CultureInfo.InvariantCulture, out var tMin) & TimeSpan.TryParse(sMax, CultureInfo.InvariantCulture, out var tMax) && tMin <= tMax
                    : DateTime.TryParse(sMin, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dtMin)
                      & DateTime.TryParse(sMax, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dtMax) && dtMin <= dtMax;
                if (!ok) { Invalid($"'{sMin}'..'{sMax}' are not valid, ordered {typeName} values"); return null; }
            }
            minExpr = $"{tFqn}.Parse({Literal(sMin)}, global::System.Globalization.CultureInfo.InvariantCulture)";
            maxExpr = $"{tFqn}.Parse({Literal(sMax)}, global::System.Globalization.CultureInfo.InvariantCulture)";
        }

        return baseRule with
        {
            MinExpr = minExpr,
            MaxExpr = maxExpr,
            MinRawExpr = rawSupported ? "global::YFex.RawValue.From(__MIN__)" : null,
            MaxRawExpr = rawSupported ? "global::YFex.RawValue.From(__MAX__)" : null,
            BoundsTypeFqn = tFqn,
            UsesOperators = false,
            Coerce = coerce,
        };
    }

    /// <summary>True when <paramref name="t"/> implements <c>YFex.IRawValue&lt;T&gt;</c> (any app type with a raw form).</summary>
    internal static bool ImplementsRawValue(ITypeSymbol t)
    {
        foreach (var i in t.AllInterfaces)
            if (i.OriginalDefinition.ToDisplayString() == "YFex.IRawValue<TSelf>") return true;
        return false;
    }

    private static readonly HashSet<string> s_dateTypes = new(StringComparer.Ordinal)
    {
        "global::System.DateOnly", "global::System.DateTime", "global::System.DateTimeOffset",
        "global::System.TimeOnly", "global::System.TimeSpan",
    };

    // BCL types with a RawValue.From overload (IRawValue types are detected by interface).
    private static readonly HashSet<string> s_rawTypes = new(StringComparer.Ordinal)
    {
        "global::System.DateOnly", "global::System.TimeOnly", "global::System.DateTime", "global::System.Guid",
    };

    private static bool IsPrimitiveNumber(SpecialType t) => t is SpecialType.System_Int32 or SpecialType.System_Int64
        or SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_UInt16
        or SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_Decimal
        or SpecialType.System_Double or SpecialType.System_Single;

    private static bool IsInteger(SpecialType t) => t is SpecialType.System_Int32 or SpecialType.System_Int64
        or SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_UInt16
        or SpecialType.System_UInt32 or SpecialType.System_UInt64;

    private static bool FitsInteger(SpecialType t, decimal v) => t switch
    {
        SpecialType.System_Int32 => v >= int.MinValue && v <= int.MaxValue,
        SpecialType.System_Int64 => v >= long.MinValue && v <= long.MaxValue,
        SpecialType.System_Int16 => v >= short.MinValue && v <= short.MaxValue,
        SpecialType.System_Byte => v >= byte.MinValue && v <= byte.MaxValue,
        SpecialType.System_SByte => v >= sbyte.MinValue && v <= sbyte.MaxValue,
        SpecialType.System_UInt16 => v >= ushort.MinValue && v <= ushort.MaxValue,
        SpecialType.System_UInt32 => v >= uint.MinValue && v <= uint.MaxValue,
        SpecialType.System_UInt64 => v >= ulong.MinValue && v <= ulong.MaxValue,
        _ => true,
    };

    private static bool TryDecimal(ArgValue c, out decimal value)
    {
        switch (c.Value)
        {
            case int i: value = i; return true;
            case long l: value = l; return true;
            case decimal m: value = m; return true;
            case short or byte or sbyte or ushort or uint:
                value = System.Convert.ToDecimal(c.Value, CultureInfo.InvariantCulture); return true;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f): value = (decimal)f; return true;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d)
                               && d >= (double)decimal.MinValue && d <= (double)decimal.MaxValue:
                value = (decimal)d; return true;
            case string s:
                return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                value = 0; return false;
        }
    }

    private static string DecimalLiteral(decimal d) => d.ToString(CultureInfo.InvariantCulture) + "m";

    private static string NumberLiteral(SpecialType t, decimal value, ArgValue original)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        return t switch
        {
            SpecialType.System_Int32 => text,
            SpecialType.System_Int64 => text + "L",
            SpecialType.System_UInt32 => text + "U",
            SpecialType.System_UInt64 => text + "UL",
            SpecialType.System_Int16 => "(short)" + Paren(text),
            SpecialType.System_Byte => "(byte)" + text,
            SpecialType.System_SByte => "(sbyte)" + Paren(text),
            SpecialType.System_UInt16 => "(ushort)" + text,
            SpecialType.System_Decimal => text + "m",
            SpecialType.System_Single => (original.Value is double od ? od.ToString("R", CultureInfo.InvariantCulture) : text) + "f",
            _ => (original.Value is double d ? d.ToString("R", CultureInfo.InvariantCulture) : text) + "d",
        };

        static string Paren(string s) => s.StartsWith("-", StringComparison.Ordinal) ? "(" + s + ")" : s;
    }

    private static string RawNumber(SpecialType t, string literal) => t switch
    {
        SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Decimal or SpecialType.System_Double
            => $"global::YFex.RawValue.From({literal})",
        SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_UInt16
            => $"global::YFex.RawValue.From((int){literal})",
        SpecialType.System_UInt32 => $"global::YFex.RawValue.From((long){literal})",
        SpecialType.System_UInt64 => $"global::YFex.RawValue.From((decimal){literal})",
        _ => $"global::YFex.RawValue.From((double){literal})",
    };

    // ── Invariants ───────────────────────────────────────────────────────────────

    private static List<InvariantModel> BuildInvariants(INamedTypeSymbol type, List<MemberModel> members,
        Compilation compilation, List<DiagnosticInfo> diagnostics, CancellationToken ct)
    {
        var result = new List<InvariantModel>();
        var memberNames = new HashSet<string>(members.Select(m => m.Name), StringComparer.Ordinal);
        ushort id = 1;

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            var attr = method.GetAttributes().FirstOrDefault(a => Is(a, Ns, "InvariantAttribute"));
            if (attr is null) continue;
            var loc = LocationInfo.From(method.Locations.FirstOrDefault());

            bool returnsOk = method.ReturnsVoid || method.ReturnType.SpecialType == SpecialType.System_Boolean;
            bool sigOk = returnsOk && !method.IsGenericMethod && (method.IsStatic
                ? method.Parameters.Length == 2
                  && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type)
                  && method.Parameters[0].RefKind is RefKind.In or RefKind.None or RefKind.RefReadOnlyParameter
                  && IsWriterRef(method.Parameters[1])
                : method.Parameters.Length == 1 && IsWriterRef(method.Parameters[0]));
            if (!sigOk)
            {
                diagnostics.Add(Diag("YFV0008", loc, method.Name));
                continue;
            }

            string[]? explicitReads = null;
            foreach (var kv in attr.NamedArguments)
                if (kv.Key == "Reads" && kv.Value.Kind == TypedConstantKind.Array)
                    explicitReads = kv.Value.Values.Select(v => v.Value as string).Where(s => s is not null).Select(s => s!).ToArray();

            var (inferred, readsAll) = InferReads(method, type, memberNames, compilation, ct);

            EquatableArray<string> reads;
            bool all;
            if (explicitReads is not null)
            {
                foreach (string r in inferred)
                    if (Array.IndexOf(explicitReads, r) < 0) { diagnostics.Add(Diag("YFV0009w", loc, method.Name, r)); break; }
                reads = new EquatableArray<string>(explicitReads.Where(memberNames.Contains).ToArray());
                all = false;
            }
            else if (readsAll)
            {
                diagnostics.Add(Diag("YFV0009", loc, method.Name));
                reads = EquatableArray<string>.Empty;
                all = true;
            }
            else
            {
                reads = new EquatableArray<string>(inferred.ToArray());
                all = false;
            }

            result.Add(new InvariantModel(method.Name, id++, method.IsStatic, reads, all));
        }
        return result;

        static bool IsWriterRef(IParameterSymbol p)
            => p.RefKind == RefKind.Ref && p.Type.ToDisplayString() == "YFex.Validation.ViolationWriter";
    }

    /// <summary>Members read by an invariant body; readsAll when the object escapes (passed on, other calls).</summary>
    private static (List<string> Reads, bool ReadsAll) InferReads(IMethodSymbol method, INamedTypeSymbol type,
        HashSet<string> memberNames, Compilation compilation, CancellationToken ct)
    {
        var reads = new List<string>();
        void AddRead(string name)
        {
            if (memberNames.Contains(name) && !reads.Contains(name)) reads.Add(name);
        }

        var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) as MethodDeclarationSyntax;
        SyntaxNode? body = (SyntaxNode?)syntax?.Body ?? syntax?.ExpressionBody;
        if (syntax is null || body is null) return (reads, true);

        var model = compilation.GetSemanticModel(syntax.SyntaxTree);
        bool readsAll = false;

        if (method.IsStatic)
        {
            var param = method.Parameters[0];
            foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, ct).Symbol, param)) continue;
                if (id.Parent is MemberAccessExpressionSyntax ma && ma.Expression == id)
                {
                    if (ma.Parent is InvocationExpressionSyntax inv && inv.Expression == ma) readsAll = true;
                    else AddRead(ma.Name.Identifier.ValueText);
                }
                else readsAll = true; // parameter passed on / copied → unknown reads
            }
        }
        else
        {
            foreach (var node in body.DescendantNodes())
            {
                if (node is ThisExpressionSyntax th && th.Parent is not MemberAccessExpressionSyntax) { readsAll = true; continue; }
                if (node is not IdentifierNameSyntax id) continue;
                var symbol = model.GetSymbolInfo(id, ct).Symbol;
                if (symbol is null)
                {
                    // A property emitted by another generator (State: [Observable] field `_x` → property `X`) is not
                    // part of this compilation view — fall back to the identifier text.
                    if (id.Parent is not MemberAccessExpressionSyntax { Name: var n } || n == id) AddRead(id.Identifier.ValueText);
                    continue;
                }
                if (symbol.IsStatic) continue;
                if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, type)) continue;
                if (symbol is IFieldSymbol f && HasObservable(f)) AddRead(DerivePropertyName(f.Name));
                else if (symbol is IPropertySymbol or IFieldSymbol) AddRead(symbol.Name);
                else if (symbol is IMethodSymbol m && !SymbolEqualityComparer.Default.Equals(m, method)) readsAll = true;
            }
        }
        return (reads, readsAll);
    }

    // ── Type helpers ─────────────────────────────────────────────────────────────

    internal readonly struct TypeInfo
    {
        public ITypeSymbol Declared { get; init; }
        public ITypeSymbol Underlying { get; init; }   // Nullable<T> → T
        public bool IsNullableValue { get; init; }
        public MemberShape Shape { get; init; }
        public CollectionKind Collection { get; init; }
        public ITypeSymbol? ElementType { get; init; }

        public static TypeInfo Of(ITypeSymbol declared)
        {
            ITypeSymbol underlying = declared;
            bool nullableValue = false;
            if (declared is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n)
            {
                underlying = n.TypeArguments[0];
                nullableValue = true;
            }

            if (underlying.SpecialType == SpecialType.System_String)
                return new TypeInfo { Declared = declared, Underlying = underlying, IsNullableValue = nullableValue, Shape = MemberShape.String };

            var kind = ClassifyCollection(underlying, out var element);
            if (kind != CollectionKind.None)
                return new TypeInfo
                {
                    Declared = declared, Underlying = underlying, IsNullableValue = nullableValue,
                    Shape = MemberShape.Collection, Collection = kind, ElementType = element,
                };

            return new TypeInfo
            {
                Declared = declared, Underlying = underlying, IsNullableValue = nullableValue,
                Shape = IsSelfComparable(underlying) ? MemberShape.Comparable : MemberShape.Other,
            };
        }
    }

    private static CollectionKind ClassifyCollection(ITypeSymbol t, out ITypeSymbol? element)
    {
        element = null;
        if (t is IArrayTypeSymbol { Rank: 1 } array)
        {
            element = array.ElementType;
            return CollectionKind.Array;
        }
        if (t is not INamedTypeSymbol named) return CollectionKind.None;

        string ns = named.ContainingNamespace.ToDisplayString();
        if (named.MetadataName == "ImmutableArray`1" && ns == "System.Collections.Immutable") { element = named.TypeArguments[0]; return CollectionKind.ImmutableArray; }
        if (named.MetadataName == "List`1" && ns == "System.Collections.Generic") { element = named.TypeArguments[0]; return CollectionKind.List; }

        CollectionKind best = CollectionKind.None;
        IEnumerable<INamedTypeSymbol> interfaces = named.TypeKind == TypeKind.Interface
            ? named.AllInterfaces.Prepend(named)
            : named.AllInterfaces;
        foreach (var i in interfaces)
        {
            if (i.ContainingNamespace.ToDisplayString() != "System.Collections.Generic") continue;
            if (i.MetadataName is "IReadOnlyList`1" or "IList`1")
            {
                element = i.TypeArguments[0];
                return CollectionKind.IndexedList;
            }
            if (i.MetadataName is "IReadOnlyCollection`1" or "ICollection`1")
            {
                element = i.TypeArguments[0];
                best = CollectionKind.CountOnly;
            }
        }
        return best;
    }

    private static bool IsSelfComparable(ITypeSymbol t)
    {
        if (IsPrimitiveNumber(t.SpecialType) || t.SpecialType == SpecialType.System_DateTime) return true;
        foreach (var i in t.AllInterfaces)
            if (i.MetadataName == "IComparable`1" && i.ContainingNamespace.ToDisplayString() == "System"
                && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], t))
                return true;
        return false;
    }

    /// <summary>
    /// True when the type is (or will be, via this generator) an IValidatable: implements
    /// IValidatable&lt;T&gt;, or is a source type with a validation surface.
    /// </summary>
    internal static bool IsValidatableType(ITypeSymbol t)
    {
        if (t is not INamedTypeSymbol n || n.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
        if (n.SpecialType != SpecialType.None) return false;
        foreach (var i in n.AllInterfaces)
            if (i.ContainingNamespace.ToDisplayString() == Ns && i.MetadataName == "IValidatable`1") return true;
        if (!n.Locations.Any(l => l.IsInSource) || n.IsRefLikeType || IsStateObject(n) || !IsPartial(n)) return false;
        return HasValidationSurface(n)
               || t_fluentTargets is { } fluent && fluent.Contains(n.ToDisplayString(PlainTypeFormat));
    }

    /// <summary>
    /// Emittable fluent targets while the merge step rebuilds models (G8: a contract configured only with
    /// <c>.Validation(...)</c> is validatable). Set/reset synchronously by <see cref="FluentMerge"/>.
    /// </summary>
    [ThreadStatic] internal static HashSet<string>? t_fluentTargets;

    /// <summary><c>Type.FullName</c> spelling: <c>Ns.Outer+Inner</c>, generic arity kept (<c>R`1</c>).</summary>
    internal static string MetadataNameOf(INamedTypeSymbol t)
    {
        var parts = new List<string>();
        for (INamedTypeSymbol? c = t; c is not null; c = c.ContainingType) parts.Insert(0, c.MetadataName);
        string nested = string.Join("+", parts);
        return t.ContainingNamespace is null || t.ContainingNamespace.IsGlobalNamespace
            ? nested
            : t.ContainingNamespace.ToDisplayString() + "." + nested;
    }

    private static bool HasValidationSurface(INamedTypeSymbol type)
    {
        if (type.GetAttributes().Any(a => Is(a, Ns, "ValidatableAttribute"))) return true;
        foreach (var m in type.GetMembers())
        {
            switch (m)
            {
                case IPropertySymbol or IFieldSymbol when m.GetAttributes().Any(IsRuleLikeAttribute):
                    return true;
                case IMethodSymbol { MethodKind: MethodKind.Constructor } c
                    when c.Parameters.Any(p => p.GetAttributes().Any(IsRuleLikeAttribute)):
                    return true;
                case IMethodSymbol method when method.GetAttributes().Any(a => Is(a, Ns, "InvariantAttribute")):
                    return true;
            }
        }
        return false;
    }

    private static bool IsStateObject(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (b.ToDisplayString() == "YFex.State.StateObject") return true;
        return false;
    }

    private static bool IsPartial(INamedTypeSymbol t)
    {
        foreach (var r in t.DeclaringSyntaxReferences)
            if (r.GetSyntax() is TypeDeclarationSyntax decl && !decl.Modifiers.Any(SyntaxKind.PartialKeyword))
                return false;
        return t.DeclaringSyntaxReferences.Length > 0;
    }

    private static bool Is(AttributeData a, string ns, string metadataName)
        => a.AttributeClass is { } c && c.MetadataName == metadataName && c.ContainingNamespace.ToDisplayString() == ns;

    private static string NameWithTypeParameters(INamedTypeSymbol t)
        => t.TypeParameters.Length == 0 ? t.Name : t.Name + "<" + string.Join(", ", t.TypeParameters.Select(p => p.Name)) + ">";

    private static string HintName(string fqn)
    {
        var chars = fqn.Replace("global::", "").ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '.' && chars[i] != '_') chars[i] = '_';
        return new string(chars) + ".Validation.g.cs";
    }

    internal static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static DiagnosticInfo Diag(string key, LocationInfo? loc, params string[] args)
        => new(key, loc, new EquatableArray<string>(args));
}
