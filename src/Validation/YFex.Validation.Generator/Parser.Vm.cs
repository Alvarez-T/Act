using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Validation.Generator;

/// <summary>
/// View models (YFex.State <c>StateObject</c> subclasses, 07 / D22). The State generator declares
/// <c>partial void __OnValidate_X(in T value)</c> for every writable <c>[Observable]</c> member and
/// <c>partial void __OnBatchValidated()</c>; this generator implements them when the VM has rules.
/// A hook may only be implemented when the State generator declares it (CS0759 otherwise), so
/// <see cref="HasStateHook"/> mirrors the State generator's rule exactly.
/// </summary>
internal static partial class Parser
{
    private const string StateNs = "YFex.State";
    private const string StateValidationNs = "YFex.State.Validation";

    /// <summary>Anything that makes a VM need generated validation.</summary>
    private static bool HasVmValidationSurface(INamedTypeSymbol type)
    {
        if (HasValidationSurface(type)) return true;
        foreach (var a in type.GetAttributes())
            if (Is(a, Ns, "ValidatesAsAttribute`1")) return true;
        foreach (var m in type.GetMembers())
            if (m is IPropertySymbol or IFieldSymbol && m.GetAttributes().Any(a => IsLegacyValidatorAttribute(a) || IsConstraintsFrom(a)))
                return true;
        return false;
    }

    /// <summary><c>[ConstraintsFrom]</c> (YFex.Rules, 07 §9) on a <c>Decision&lt;TS&gt;</c> property — matched by name, no reference.</summary>
    private static bool IsConstraintsFrom(AttributeData a) => Is(a, "YFex.Rules", "ConstraintsFromAttribute");

    private static bool IsLegacyValidatorAttribute(AttributeData a)
        => Is(a, StateNs, "ValidateWithAttribute") || Is(a, StateNs, "ValidateAsyncAttribute");

    internal static bool HasObservable(ISymbol member)
        => member.GetAttributes().Any(a => Is(a, StateNs, "ObservableAttribute"));

    /// <summary>Same rule as <c>ObservableParser.DerivePropertyName</c>: <c>_firstName</c> → <c>FirstName</c>.</summary>
    internal static string DerivePropertyName(string fieldName)
    {
        int start = 0;
        while (start < fieldName.Length && fieldName[start] == '_') start++;
        if (start >= fieldName.Length) return fieldName;
        return char.ToUpperInvariant(fieldName[start]) + fieldName.Substring(start + 1);
    }

    /// <summary>
    /// True when the State generator declares <c>__OnValidate_X</c> for this member: writable <c>[Observable]</c>
    /// property/field of a top-level partial CLASS inheriting StateObject, not a Task notifier.
    /// </summary>
    private static bool HasStateHook(ISymbol member, INamedTypeSymbol owner)
    {
        if (!HasObservable(member) || owner.ContainingType is not null) return false;
        bool partialClass = owner.DeclaringSyntaxReferences.Any(r =>
            r.GetSyntax() is ClassDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.PartialKeyword));
        if (!partialClass) return false;

        ITypeSymbol type;
        switch (member)
        {
            case IPropertySymbol p when !p.IsReadOnly: type = p.Type; break;
            case IFieldSymbol f when !f.IsReadOnly: type = f.Type; break;
            default: return false;
        }
        bool taskNotifier = member.GetAttributes().Any(a => a.AttributeClass?.Name == "NotifyOnTaskCompletionAttribute");
        if (taskNotifier && type is INamedTypeSymbol { Name: "Task" } t
            && t.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" && t.TypeArguments.Length <= 1)
            return false;
        return true;
    }

    // ── Model ────────────────────────────────────────────────────────────────────

    private sealed class Contract
    {
        public Contract(int index, INamedTypeSymbol type, string? prefix, string? profile)
        {
            Index = index;
            Type = type;
            Prefix = prefix;
            Profile = profile;
        }

        public int Index { get; }
        public INamedTypeSymbol Type { get; }
        public string? Prefix { get; }
        public string? Profile { get; }
        public List<PathMapEntry> Map { get; } = new();
        public Dictionary<string, (string VmMember, bool Bang)> Mapped { get; } = new(StringComparer.Ordinal);
    }

    private readonly struct ResolvedMember
    {
        public ResolvedMember(INamedTypeSymbol owner, ISymbol symbol, ITypeSymbol type)
        {
            Owner = owner;
            Symbol = symbol;
            Type = type;
        }

        public INamedTypeSymbol Owner { get; }
        public ISymbol Symbol { get; }
        public ITypeSymbol Type { get; }
    }

    internal static TypeModel BuildVmModel(INamedTypeSymbol type, Compilation compilation, CancellationToken ct)
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

        var memberNames = new HashSet<string>(type.GetMembers().Select(m => m.Name), StringComparer.Ordinal);
        string ClassName(string name)
        {
            if (!memberNames.Contains(name)) return name;
            diagnostics.Add(Diag("YFV0013", location, name));
            return "Validation" + name;
        }

        // ── Members: [Observable] ones (hooks) + anything carrying rules ──────────
        var discovered = new List<DiscoveredMember>();
        var hooks = new List<bool>();
        var boolMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in type.GetMembers())
        {
            if (m.IsStatic || m.IsImplicitlyDeclared) continue;
            ITypeSymbol? mt = m switch
            {
                IPropertySymbol { IsIndexer: false } p => p.Type,
                IFieldSymbol { IsConst: false } f => f.Type,
                _ => null,
            };
            if (mt is null) continue;
            bool observable = HasObservable(m);
            string name = m is IFieldSymbol && observable ? DerivePropertyName(m.Name) : m.Name;
            if (mt.SpecialType == SpecialType.System_Boolean) boolMembers.Add(name);

            var attrs = m.GetAttributes();
            bool ruled = attrs.Any(a => IsRuleLikeAttribute(a) || IsLegacyValidatorAttribute(a) || Is(a, Ns, "MapsToAttribute"));
            bool visible = m.DeclaredAccessibility == Accessibility.Public || observable;
            if (!visible || !(observable || ruled)) continue;

            discovered.Add(new DiscoveredMember(m, mt, attrs, name));
            hooks.Add(HasStateHook(m, type));
        }

        var members = new List<MemberModel>(discovered.Count);
        for (int i = 0; i < discovered.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            members.Add(BuildMember(discovered[i], (ushort)i, type, boolMembers, diagnostics));
        }
        var invariants = BuildInvariants(type, members, compilation, diagnostics, ct);

        // ── Class options ───────────────────────────────────────────────────────
        byte classTrigger = 0;
        string? showExpr = null;
        var contracts = new List<Contract>();
        foreach (var a in type.GetAttributes())
        {
            if (Is(a, StateNs, "ValidationOptionsAttribute"))
            {
                foreach (var kv in a.NamedArguments)
                {
                    if (kv.Key == "Trigger" && kv.Value.Value is byte tr) classTrigger = tr;
                    if (kv.Key == "Show" && kv.Value.Value is byte sh) showExpr = "global::YFex.State.ShowErrors." + ShowName(sh);
                }
            }
            else if (Is(a, Ns, "ValidatesAsAttribute`1") && a.AttributeClass!.TypeArguments[0] is INamedTypeSymbol ct2)
            {
                string? prefix = null, profile = null;
                foreach (var kv in a.NamedArguments)
                {
                    if (kv.Key == "Prefix") prefix = kv.Value.Value as string;
                    if (kv.Key == "Profile") profile = kv.Value.Value as string;
                }
                contracts.Add(new Contract(contracts.Count, ct2, string.IsNullOrEmpty(prefix) ? null : prefix, profile));
            }
        }

        // ── Per-member VM data ─────────────────────────────────────────────────
        var vmMembers = new List<VmMemberModel>(members.Count);
        bool usesLegacy = false;
        for (int i = 0; i < members.Count; i++)
        {
            var d = discovered[i];
            var mem = members[i];
            byte trigger = classTrigger;
            int debounce = 0;
            string? mapsTo = null;
            var legacySync = new List<string>();
            var legacyAsync = new List<string>();
            var loc = LocationInfo.From(d.Symbol.Locations.FirstOrDefault());
            foreach (var a in d.Attributes)
            {
                if (Is(a, StateNs, "ValidateOnAttribute") && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is byte t)
                    trigger = t;
                else if (Is(a, StateNs, "DebounceAttribute") && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is int ms)
                    debounce = ms;
                else if (Is(a, Ns, "MapsToAttribute") && a.ConstructorArguments.Length == 1)
                    mapsTo = a.ConstructorArguments[0].Value as string;
                else if (IsLegacyValidatorAttribute(a) && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is INamedTypeSymbol vt)
                {
                    bool isAsync = a.AttributeClass!.Name == "ValidateAsyncAttribute";
                    string iface = isAsync ? "IAsyncValidator`1" : "IValidator`1";
                    bool ok = vt.AllInterfaces.Any(x => x.MetadataName == iface && x.ContainingNamespace.ToDisplayString() == StateValidationNs);
                    if (!ok) { diagnostics.Add(Diag("YFV0005", loc, vt.ToDisplayString(), d.Type.ToDisplayString())); continue; }
                    (isAsync ? legacyAsync : legacySync).Add(vt.ToDisplayString(PlainTypeFormat));
                    usesLegacy = true;
                }
            }

            var borrowed = BorrowFor(d, mem, mapsTo, contracts, compilation, diagnostics, loc);

            ulong mask = 0;
            for (int k = 0; k < invariants.Count && k < 64; k++)
                if (invariants[k].ReadsAll || invariants[k].Reads.AsSpan().ToArray().Contains(mem.Name))
                    mask |= 1UL << k;

            bool constraintsFrom = d.Attributes.Any(IsConstraintsFrom);
            bool hasRules = !mem.Rules.IsEmpty || mem.Each is not null || mem.NestedValidatableFqn is not null
                            || mem.HasAsyncRules || borrowed.Count > 0 || legacySync.Count > 0 || legacyAsync.Count > 0
                            || constraintsFrom;
            if (hasRules && !hooks[i])
            {
                string reason = HasObservable(d.Symbol)
                    ? "the [Observable] member is read-only or a Task notifier (no setter hook)"
                    : "it is not an [Observable] member of this class (no setter hook)";
                diagnostics.Add(Diag("YFV0025", loc, mem.Name, reason));
            }

            vmMembers.Add(new VmMemberModel(
                Name: mem.Name,
                TypeFqn: mem.TypeFqn,
                HasHook: hooks[i],
                Trigger: trigger,
                DebounceMs: debounce,
                LegacySync: new EquatableArray<string>(legacySync.ToArray()),
                LegacyAsync: new EquatableArray<string>(legacyAsync.ToArray()),
                Borrowed: new EquatableArray<VmBorrowModel>(borrowed.ToArray()),
                WhenDependents: EquatableArray<string>.Empty,
                InvariantMask: mask,
                ConstraintsFrom: constraintsFrom));
        }

        // When-guarded rules: changing the When member re-validates the guarded member.
        for (int i = 0; i < vmMembers.Count; i++)
        {
            var dependents = new List<string>();
            foreach (var other in members)
                if (other.Name != vmMembers[i].Name && other.Rules.Any(r => r.When == vmMembers[i].Name))
                    dependents.Add(other.Name);
            if (dependents.Count > 0)
                vmMembers[i] = vmMembers[i] with { WhenDependents = new EquatableArray<string>(dependents.ToArray()) };
        }

        // Obsolete-attribute compatibility: those validators showed errors immediately (no display policy).
        if (showExpr is null && usesLegacy && !type.GetAttributes().Any(a => Is(a, StateNs, "ValidationOptionsAttribute")))
            showExpr = "global::YFex.State.ShowErrors.Immediately";

        ReportInheritedMatches(type, contracts, members, location, diagnostics);
        var contractModels = contracts.Select(c => BuildContractModel(c, type, memberNames, members, location, diagnostics)).ToList();

        string fqn = type.ToDisplayString(PlainTypeFormat);
        bool isGeneric = type.TypeParameters.Length > 0;
        (bool baseHasValidation, bool baseHasContracts) = BaseVmInfo(type);

        var vm = new VmModel(
            Members: new EquatableArray<VmMemberModel>(vmMembers.ToArray()),
            Contracts: new EquatableArray<VmContractModel>(contractModels.ToArray()),
            ShowExpr: showExpr,
            DefaultProfile: contracts.Count > 0 ? contracts[0].Profile : null,
            BaseHasValidation: baseHasValidation,
            BaseHasContracts: baseHasContracts,
            HasCqrs: compilation.GetTypeByMetadataName("YFex.Cqrs.QueueableResult`1") is not null);

        return new TypeModel(
            Namespace: type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
            Containers: EquatableArray<ContainerModel>.Empty,
            Keyword: "class",
            NameWithTypeParameters: NameWithTypeParameters(type),
            FullyQualifiedName: fqn,
            HintName: HintName(fqn),
            IsGeneric: isGeneric,
            IsRegistrable: false,   // VMs are not IValidatable (they validate through their bag)
            BaseValidatableFqn: null,
            BaseMemberCount: 0,
            FieldIdsClass: ClassName("FieldIds"),
            FieldsClass: ClassName("Fields"),
            RulesClass: ClassName("Rules"),
            InvariantIdsClass: ClassName("InvariantIds"),
            Members: new EquatableArray<MemberModel>(members.ToArray()),
            Invariants: new EquatableArray<InvariantModel>(invariants.ToArray()),
            Diagnostics: new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()),
            CanEmit: canEmit && type.ContainingType is null,
            Location: location,
            Vm: vm,
            MetadataName: MetadataNameOf(type));

        static string ShowName(byte b) => b switch { 0 => "Immediately", 2 => "AfterSubmit", _ => "AfterTouched" };
    }

    // ── Contracts ────────────────────────────────────────────────────────────────

    /// <summary>Resolves the contract member(s) a VM member borrows from and records the path maps.</summary>
    private static List<VmBorrowModel> BorrowFor(DiscoveredMember d, MemberModel mem, string? mapsTo, List<Contract> contracts,
        Compilation compilation, List<DiagnosticInfo> diagnostics, LocationInfo? loc)
    {
        var result = new List<VmBorrowModel>();
        var conventionMatches = new List<Contract>();

        foreach (var c in contracts)
        {
            string? path = null;
            if (mapsTo is not null) path = mapsTo;
            else if (c.Prefix is not null)
            {
                if (mem.Name.Length > c.Prefix.Length && mem.Name.StartsWith(c.Prefix, StringComparison.Ordinal))
                    path = mem.Name.Substring(c.Prefix.Length);
            }
            else path = mem.Name;
            if (path is null || ResolvePath(c.Type, path) is not { } target) continue;
            if (mapsTo is null && c.Prefix is null) conventionMatches.Add(c);

            // YFV0022: the VM value must convert implicitly to the contract member type.
            var conversion = compilation.ClassifyConversion(d.Type.WithNullableAnnotation(NullableAnnotation.None),
                target.Type.WithNullableAnnotation(NullableAnnotation.None));
            if (!conversion.IsImplicit)
            {
                diagnostics.Add(Diag("YFV0022", loc, mem.Name, d.Type.ToDisplayString(), target.Owner.ToDisplayString(),
                    target.Symbol.Name, target.Type.ToDisplayString()));
                continue;
            }

            bool bang = d.Type.IsReferenceType && d.Type.NullableAnnotation == NullableAnnotation.Annotated
                        && target.Type.NullableAnnotation != NullableAnnotation.Annotated;
            c.Map.Add(new PathMapEntry(path, mem.Name));
            c.Mapped[path] = (mem.Name, bang);

            if (IsValidatableType(target.Owner))
            {
                result.Add(new VmBorrowModel(c.Index,
                    target.Owner.ToDisplayString(PlainTypeFormat) + "." + RulesClassOf(target.Owner) + "." + target.Symbol.Name,
                    c.Profile));
                ReportLooser(mem, target, c.Type, loc, diagnostics);
            }
        }

        if (mapsTo is not null && contracts.All(c => ResolvePath(c.Type, mapsTo) is null))
            diagnostics.Add(Diag("YFV0021", loc, mapsTo, mem.Name));

        // YFV0024: two contracts that can be active together both claim the member by name.
        for (int i = 0; i < conventionMatches.Count; i++)
            for (int j = i + 1; j < conventionMatches.Count; j++)
            {
                string? p1 = conventionMatches[i].Profile, p2 = conventionMatches[j].Profile;
                if (p1 is null || p2 is null || p1 == p2)
                {
                    diagnostics.Add(Diag("YFV0024", loc, mem.Name,
                        conventionMatches[i].Type.Name + ", " + conventionMatches[j].Type.Name));
                    return result;
                }
            }
        return result;
    }

    /// <summary>Walks <c>"Address.City"</c> through the public input members of <paramref name="type"/>.</summary>
    private static ResolvedMember? ResolvePath(INamedTypeSymbol type, string path)
    {
        INamedTypeSymbol current = type;
        string[] segments = path.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            DiscoveredMember? found = null;
            foreach (var m in DiscoverMembers(current))
                if (m.Name == segments[i]) { found = m; break; }
            if (found is not { } f) return null;
            if (i == segments.Length - 1) return new ResolvedMember(current, f.Symbol, f.Type);
            var next = f.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
                ? n.TypeArguments[0] : f.Type;
            if (next is not INamedTypeSymbol nt || nt.SpecialType != SpecialType.None) return null;
            current = nt;
        }
        return null;
    }

    /// <summary>Name of the generated Rules class of a validatable type ("ValidationRules" on a clash, YFV0013).</summary>
    private static string RulesClassOf(INamedTypeSymbol owner)
    {
        if (!owner.Locations.Any(l => l.IsInSource))
            return owner.GetTypeMembers("Rules").Length > 0 || owner.GetTypeMembers("ValidationRules").Length == 0 ? "Rules" : "ValidationRules";
        return owner.GetMembers("Rules").Length > 0 ? "ValidationRules" : "Rules";
    }

    /// <summary>YFV0023: a VM rule accepting values the contract rejects (Length / integer Range).</summary>
    private static void ReportLooser(MemberModel vm, ResolvedMember target, INamedTypeSymbol contract, LocationInfo? loc,
        List<DiagnosticInfo> diagnostics)
    {
        var contractMember = BuildMember(new DiscoveredMember(target.Symbol, target.Type, target.Symbol.GetAttributes()),
            0, target.Owner, new HashSet<string>(StringComparer.Ordinal), new List<DiagnosticInfo>());

        var vmLen = vm.Rules.FirstOrDefault(r => r.Kind == RuleKind.Length && !r.IsConditional);
        var cLen = contractMember.Rules.FirstOrDefault(r => r.Kind == RuleKind.Length && !r.IsConditional);
        if (vmLen is not null && cLen is not null && (vmLen.MaxLength > cLen.MaxLength || vmLen.MinLength < cLen.MinLength))
        {
            diagnostics.Add(Diag("YFV0023", loc, vm.Name, $"Length({vmLen.MinLength}, {Max(vmLen.MaxLength)})",
                contract.Name, $"Length({cLen.MinLength}, {Max(cLen.MaxLength)})"));
        }

        var vmRange = vm.Rules.FirstOrDefault(r => r.Kind == RuleKind.Range && !r.IsConditional && r.MinConstLiteral is not null);
        var cRange = contractMember.Rules.FirstOrDefault(r => r.Kind == RuleKind.Range && !r.IsConditional && r.MinConstLiteral is not null);
        if (vmRange is not null && cRange is not null
            && TryInt(vmRange.MinConstLiteral!, out decimal vMin) && TryInt(vmRange.MaxConstLiteral!, out decimal vMax)
            && TryInt(cRange.MinConstLiteral!, out decimal cMin) && TryInt(cRange.MaxConstLiteral!, out decimal cMax)
            && (vMin < cMin || vMax > cMax))
        {
            diagnostics.Add(Diag("YFV0023", loc, vm.Name, $"Range({vMin}, {vMax})", contract.Name, $"Range({cMin}, {cMax})"));
        }

        static string Max(int v) => v == int.MaxValue ? "∞" : v.ToString(CultureInfo.InvariantCulture);

        static bool TryInt(string literal, out decimal value)
        {
            string digits = new string(literal.Where(ch => char.IsDigit(ch) || ch == '-').ToArray());
            return decimal.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>YFV0025: a contract member matches a property inherited from a base VM (its hook is private there).</summary>
    private static void ReportInheritedMatches(INamedTypeSymbol type, List<Contract> contracts, List<MemberModel> own,
        LocationInfo? location, List<DiagnosticInfo> diagnostics)
    {
        if (contracts.Count == 0) return;
        var ownNames = new HashSet<string>(own.Select(m => m.Name), StringComparer.Ordinal);
        for (var b = type.BaseType; b is not null && !IsFrameworkStateBase(b); b = b.BaseType)
        {
            foreach (var m in b.GetMembers())
            {
                if (!HasObservable(m)) continue;
                string name = m is IFieldSymbol ? DerivePropertyName(m.Name) : m.Name;
                if (ownNames.Contains(name)) continue;
                foreach (var c in contracts)
                {
                    string path = c.Prefix is null ? name
                        : name.StartsWith(c.Prefix, StringComparison.Ordinal) ? name.Substring(c.Prefix.Length) : "";
                    if (path.Length == 0 || ResolvePath(c.Type, path) is null) continue;
                    diagnostics.Add(Diag("YFV0025", location, name,
                        $"it is inherited from '{b.Name}' — declare [ValidatesAs<{c.Type.Name}>] on '{b.Name}' to borrow '{path}' (hooks are private to the declaring class, D22)"));
                    break;
                }
            }
        }
    }

    private static bool IsFrameworkStateBase(INamedTypeSymbol t)
    {
        string ns = t.ContainingNamespace.ToDisplayString();
        return ns is "YFex.State" or "YFex.State.Mvvm" or "YFex.Mvvm" || t.SpecialType == SpecialType.System_Object;
    }

    /// <summary>Does a base VM (up to the framework bases) carry generated validation / contracts?</summary>
    private static (bool Validation, bool Contracts) BaseVmInfo(INamedTypeSymbol type)
    {
        bool validation = false, contracts = false;
        for (var b = type.BaseType; b is not null && !IsFrameworkStateBase(b); b = b.BaseType)
        {
            if (b.Locations.Any(l => l.IsInSource))
            {
                if (!IsPartial(b) || b.ContainingType is not null) continue;
                validation |= HasVmValidationSurface(b);
                contracts |= b.GetAttributes().Any(a => Is(a, Ns, "ValidatesAsAttribute`1"));
            }
            else
            {
                validation |= b.GetMembers("__vValidateAll").Length > 0;
                contracts |= b.GetMembers("ApplyServer").Length > 0;
            }
        }
        return (validation, contracts);
    }

    private static VmContractModel BuildContractModel(Contract c, INamedTypeSymbol vm, HashSet<string> vmMemberNames,
        List<MemberModel> members, LocationInfo? location, List<DiagnosticInfo> diagnostics)
    {
        string shortName = c.Type.Name;
        string toName = "To" + shortName;
        var missing = new List<string>();
        string? toExpr = BuildConstruction(c.Type, "", c, missing, depth: 0);

        bool userDefines = false, emitDeclaration = false;
        foreach (var m in vm.GetMembers(toName))
        {
            if (m is not IMethodSymbol { Parameters.Length: 0 } method) continue;
            userDefines = true;
            // `public partial C ToC() { … }` written by the user → we emit the declaration part.
            bool isPartialImplementation = method.DeclaringSyntaxReferences.Any(r =>
                r.GetSyntax() is MethodDeclarationSyntax md && md.Modifiers.Any(SyntaxKind.PartialKeyword) && (md.Body is not null || md.ExpressionBody is not null));
            emitDeclaration = isPartialImplementation && method.PartialDefinitionPart is null;
        }
        if (userDefines) toExpr = null;
        else if (toExpr is null) emitDeclaration = true;

        // YFV0020: top-level contract members nobody maps (info — optional members are fine when ToX is generated).
        var unmapped = new List<string>();
        foreach (var m in DiscoverMembers(c.Type))
            if (!c.Mapped.Keys.Any(k => k == m.Name || k.StartsWith(m.Name + ".", StringComparison.Ordinal)))
                unmapped.Add(m.Name);
        if (unmapped.Count > 0)
        {
            string consequence = userDefines ? $"{toName}() is yours"
                : toExpr is not null ? $"{toName}() leaves them at their defaults"
                : $"implement 'public partial {c.Type.ToDisplayString()} {toName}()' (required: {string.Join(", ", missing)})";
            diagnostics.Add(Diag("YFV0020", location, vm.Name, c.Type.Name, string.Join(", ", unmapped), consequence));
        }

        return new VmContractModel(
            Index: c.Index,
            Fqn: c.Type.ToDisplayString(PlainTypeFormat),
            ShortName: shortName,
            Profile: c.Profile,
            IsValidatable: IsValidatableType(c.Type),
            PathMap: new EquatableArray<PathMapEntry>(c.Map.ToArray()),
            ToMethodName: toName,
            ToExpr: toExpr,
            EmitToDeclaration: emitDeclaration);
    }

    /// <summary>
    /// Construction expression for <paramref name="type"/> at <paramref name="prefix"/> from mapped VM members:
    /// positional parameters by name (defaults may be omitted), other settable/init members in an initializer.
    /// Null (and <paramref name="missing"/> filled) when a required input has no mapping.
    /// </summary>
    private static string? BuildConstruction(INamedTypeSymbol type, string prefix, Contract c, List<string> missing, int depth)
    {
        if (depth > 4) return null;
        IMethodSymbol? primary = null;
        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public && ctor.DeclaredAccessibility != Accessibility.Internal) continue;
            if (ctor.Parameters.Length > 0 && ctor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax))
                primary = ctor;
        }
        if (primary is null && type.IsRecord && type.Locations.All(l => !l.IsInSource))
        {
            // Metadata record: take the constructor whose parameters all match properties (positional record).
            primary = type.InstanceConstructors.FirstOrDefault(ctor => ctor.Parameters.Length > 0
                && ctor.Parameters.All(p => type.GetMembers(p.Name).OfType<IPropertySymbol>().Any()));
        }
        bool hasParameterless = type.TypeKind == TypeKind.Struct
            || type.InstanceConstructors.Any(ctor => ctor.Parameters.Length == 0 && ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal);
        if (primary is null && !hasParameterless)
        {
            missing.Add(prefix.Length == 0 ? type.Name + " (no usable constructor)" : prefix.TrimEnd('.') + " (no usable constructor)");
            return null;
        }

        string? Value(string name, ITypeSymbol memberType)
        {
            string path = prefix + name;
            if (c.Mapped.TryGetValue(path, out var mapped)) return mapped.Bang ? mapped.VmMember + "!" : mapped.VmMember;
            if (c.Mapped.Keys.Any(k => k.StartsWith(path + ".", StringComparison.Ordinal)))
            {
                var nestedType = memberType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
                    ? n.TypeArguments[0] as INamedTypeSymbol : memberType as INamedTypeSymbol;
                return nestedType is null ? null : BuildConstruction(nestedType, path + ".", c, missing, depth + 1);
            }
            return null;
        }

        var args = new List<string>();
        var ctorNames = new HashSet<string>(StringComparer.Ordinal);
        bool ok = true;
        if (primary is not null)
        {
            foreach (var p in primary.Parameters)
            {
                ctorNames.Add(p.Name);
                string? v = Value(p.Name, p.Type);
                if (v is not null) args.Add(p.Name + ": " + v);
                else if (!p.HasExplicitDefaultValue) { missing.Add(prefix + p.Name); ok = false; }
            }
        }

        var inits = new List<string>();
        foreach (var m in type.GetMembers())
        {
            if (m.IsStatic || m.DeclaredAccessibility != Accessibility.Public || ctorNames.Contains(m.Name)) continue;
            ITypeSymbol? mt = m switch
            {
                IPropertySymbol { IsIndexer: false, SetMethod: not null } p => p.Type,
                IFieldSymbol { IsReadOnly: false, IsConst: false, IsImplicitlyDeclared: false } f => f.Type,
                _ => null,
            };
            if (mt is null) continue;
            bool required = m is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true };
            string? v = Value(m.Name, mt);
            if (v is not null) inits.Add(m.Name + " = " + v);
            else if (required) { missing.Add(prefix + m.Name); ok = false; }
        }
        if (!ok) return null;

        string fqn = type.ToDisplayString(PlainTypeFormat);
        string ctorCall = primary is not null ? $"new {fqn}({string.Join(", ", args)})" : $"new {fqn}()";
        return inits.Count == 0 ? ctorCall : $"{ctorCall} {{ {string.Join(", ", inits)} }}";
    }
}
