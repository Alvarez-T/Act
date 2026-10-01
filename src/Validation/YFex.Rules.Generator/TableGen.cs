using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace YFex.Rules.Generator;

/// <summary>[DecisionTable&lt;TS&gt;] → IDecisionTable implementation (05 §2).</summary>
internal static class TableGen
{
    private static readonly string[] DirectiveNames =
        ["None", "Deny", "Warn", "LimitMin", "LimitMax", "Grant", "DecreasePercent", "DecreaseAmount", "RequireApproval",
         "IncreasePercent", "IncreaseAmount"];
    private static readonly string[] ExceedNames = ["Deny", "Warn", "RequireApproval"];
    private static readonly string[] HitPolicies = ["First", "Unique", "Priority", "Collect"];

    public static TableModel? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;
        var a = ctx.Attributes[0];
        var diags = new List<DiagnosticInfo>();
        var loc = type.Locations.FirstOrDefault();
        if (!Names.IsPartial(type)) diags.Add(DiagnosticInfo.Create("YFR0103", loc, type.Name));
        var situation = a.AttributeClass!.TypeArguments[0] as INamedTypeSymbol;
        string code = a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is string c0 ? c0 : type.Name;
        int hit = a.ConstructorArguments.Length > 1 ? Names.EnumValue(a.ConstructorArguments[1]) : 0;
        var editableAt = new List<string>();
        foreach (var n in a.NamedArguments)
            if (n.Key == "EditableAt" && n.Value.Kind == TypedConstantKind.Array && !n.Value.IsNull)
                foreach (var v in n.Value.Values)
                    if (v.Value is string s) editableAt.Add(s);
        string editableExpr = editableAt.Count == 0 ? "null" : "[" + string.Join(", ", editableAt.Select(Names.Literal)) + "]";
        string? guard = Names.Named<INamedTypeSymbol>(a, "Guardrail") is { } g ? Names.Of(g) : null;

        var row = type.GetTypeMembers("Row").FirstOrDefault();
        if (row is null || situation is null)
        {
            diags.Add(DiagnosticInfo.Create("YFR0104", loc, type.Name));
            return Empty();
        }

        string schemaName = Names.Find(situation, Names.Situation)?.ConstructorArguments.FirstOrDefault().Value as string ?? situation.Name;
        var (linesMember, lineType) = Shapes.LinesOf(situation);
        var situationFields = Shapes.FieldsOf(situation, out _);
        var lineFields = lineType is null ? [] : Shapes.FieldsOf(lineType, out _);
        foreach (var tier in editableAt)
            if (!situationFields.Any(p => p.Name == tier && Names.Find(p, Names.RuleTier) is not null))
                diags.Add(DiagnosticInfo.Create("YFR0107", loc, type.Name, tier, situation.Name));

        var columns = new List<ColumnModel>();
        foreach (var m in Shapes.FieldsOf(row, out _))
        {
            ct.ThrowIfCancellationRequested();
            var when = Names.Find(m, Names.When);
            var then = Names.Find(m, Names.Then);
            if (when is null && then is null)
            {
                diags.Add(DiagnosticInfo.Create("YFR0106", m.Locations.FirstOrDefault(), m.Name, row.Name));
                continue;
            }
            var mloc = m.Locations.FirstOrDefault() ?? loc;
            string field = when is not null
                ? when.ConstructorArguments.FirstOrDefault().Value as string ?? m.Name
                : then!.ConstructorArguments.Length > 1 ? then.ConstructorArguments[1].Value as string ?? "" : "";

            // Cell shape
            CellKind cell = CellKind.Value;
            ITypeSymbol cellType = m.Type;
            if (m.Type is INamedTypeSymbol mt && Names.Full(mt) == "YFex.Rules.Match`1") { cell = CellKind.Match; cellType = mt.TypeArguments[0]; }
            else if (m.Type is INamedTypeSymbol bt && Names.Full(bt) == "YFex.Rules.Band`1") { cell = CellKind.Band; cellType = bt.TypeArguments[0]; }

            // Bound field
            bool isLine = false;
            IPropertySymbol? bound;
            string name = field;
            if (field.StartsWith("Lines[].", System.StringComparison.Ordinal) || (linesMember is not null && field.StartsWith(linesMember + "[].", System.StringComparison.Ordinal)))
            {
                isLine = true;
                name = field.Substring(field.IndexOf("[].", System.StringComparison.Ordinal) + 3);
                bound = lineFields.FirstOrDefault(p => p.Name == name);
            }
            else bound = situationFields.FirstOrDefault(p => p.Name == name);

            string directive = "None";
            string? onExceed = null, permission = null;
            if (then is not null)
            {
                int k = Names.EnumValue(then.ConstructorArguments.FirstOrDefault());
                directive = k >= 0 && k < DirectiveNames.Length ? DirectiveNames[k] : "None";
                if (Names.HasNamed(then, "OnExceed")) onExceed = ExceedNames[Names.NamedEnum(then, "OnExceed")];
                permission = Names.Named<string>(then, "ApprovalPermission");
                if (field.Length == 0) bound = null;
            }

            string? access = null, fieldRef = null;
            string kind = Shapes.Classify(cellType).Kind;
            var enumMembers = cellType.TypeKind == TypeKind.Enum && cellType is INamedTypeSymbol ce ? Shapes.EnumMembers(ce) : [];
            if (bound is not null)
            {
                access = (isLine ? "__line." : "s.") + bound.Name;
                if (!isLine) fieldRef = $"{Names.Of(situation)}.Fields.{bound.Name}";
                var boundClass = Shapes.Classify(bound.Type);
                kind = boundClass.Kind;
                if (when is not null && !SymbolEqualityComparer.Default.Equals(Unwrap(bound.Type), cellType))
                    diags.Add(DiagnosticInfo.Create("YFR0102", mloc, m.Name, type.Name,
                        $"cell type '{cellType.ToDisplayString()}' does not match '{field}' of type '{bound.Type.ToDisplayString()}'"));
            }
            else if (when is not null)
                diags.Add(DiagnosticInfo.Create("YFR0101", mloc, m.Name, type.Name, field, situation.Name));
            // Then + unresolved name → maybe a rule-set derived field; resolved when assembled.

            if (then is not null)
            {
                string? problem = directive switch
                {
                    "LimitMin" or "LimitMax" when cell != CellKind.Value => "limit columns hold plain values",
                    "LimitMin" or "LimitMax" when isLine => "limits on line fields are not supported",
                    "LimitMin" or "LimitMax" when field.Length == 0 => "limit columns must name the limited field",
                    "LimitMin" or "LimitMax" when Shapes.Classify(m.Type) is { Supported: false } or { Shape: FieldShape.Lines } => $"type '{m.Type.ToDisplayString()}' has no raw form",
                    "Grant" when !Names.Is(m.Type, "YFex.OptionSet") => "Grant columns must be OptionSet",
                    "Grant" when field.Length == 0 => "Grant columns must name the field",
                    "DecreasePercent" or "IncreasePercent" when !Names.Is(m.Type, "YFex.Percentual") => $"{directive} columns must be Percentual",
                    "DecreaseAmount" or "IncreaseAmount" when !Shapes.IsAmountType(m.Type)
                        => $"{directive} columns must be an amount (an IAmount<T> type such as Money or Mass, or decimal/int/long/double)",
                    "Deny" or "Warn" or "RequireApproval" when m.Type.SpecialType != SpecialType.System_String => "Deny/Warn/RequireApproval columns must be string",
                    "None" => "unknown DirectiveKind",
                    _ => null,
                };
                if (problem is not null) diags.Add(DiagnosticInfo.Create("YFR0105", mloc, m.Name, type.Name, problem));
            }

            columns.Add(new ColumnModel(columns.Count, m.Name, when is not null, cell, Names.Of(cellType), Names.Of(m.Type),
                cellType.TypeKind == TypeKind.Enum, field, directive, onExceed, permission, access, fieldRef, kind, isLine,
                new EquatableArray<EnumMemberModel>(enumMembers)));
        }

        // Row JSON codec (RowsJson): every row member, in constructor order.
        var rowMembers = new List<RowMemberModel>();
        foreach (var m in Shapes.FieldsOf(row, out _))
        {
            CellKind cell = CellKind.Value;
            ITypeSymbol valueType = m.Type;
            if (m.Type is INamedTypeSymbol mt2 && Names.Full(mt2) == "YFex.Rules.Match`1") { cell = CellKind.Match; valueType = mt2.TypeArguments[0]; }
            else if (m.Type is INamedTypeSymbol bt2 && Names.Full(bt2) == "YFex.Rules.Band`1") { cell = CellKind.Band; valueType = bt2.TypeArguments[0]; }
            bool isEnum = valueType.TypeKind == TypeKind.Enum;
            bool supported = isEnum || Shapes.ScalarKind(valueType) is not null;
            rowMembers.Add(new RowMemberModel(m.Name, m.Type.ToDisplayString(Names.Fqn), cell, Names.Of(valueType), isEnum, supported));
        }
        Shapes.FieldsOf(row, out bool rowPositional);

        // DefaultRows: ReadOnlySpan<Row> property (implicit impl) or Row[] field (explicit impl emitted).
        bool hasDefault = false, isSpan = false;
        foreach (var dm in type.GetMembers("DefaultRows"))
        {
            if (dm is IPropertySymbol { IsStatic: true } dp && dp.Type is INamedTypeSymbol st && Names.Full(st) == "System.ReadOnlySpan`1") { hasDefault = true; isSpan = true; }
            if (dm is IFieldSymbol { IsStatic: true } df && df.Type is IArrayTypeSymbol) hasDefault = true;
            if (dm is IPropertySymbol { IsStatic: true } dap && dap.Type is IArrayTypeSymbol) hasDefault = true;
        }

        return new TableModel(Names.Namespace(type), type.Name, Names.Of(type), Names.Containing(type), code,
            HitPolicies[hit < HitPolicies.Length && hit >= 0 ? hit : 0], Names.Named<string>(a, "Group"), Names.Named<string>(a, "RuleSet"),
            Names.Named<int>(a, "Priority"), editableExpr, Names.Named<string>(a, "PublishPermission"), guard, Names.Named<bool>(a, "Complete"),
            Names.Of(situation), schemaName, row.Name, Names.Of(row), Names.Find(row, Names.Validatable) is not null,
            hasDefault, isSpan, linesMember, lineType is null ? null : Names.Of(lineType),
            new EquatableArray<ColumnModel>(columns.ToArray()), LocationInfo.From(loc), new EquatableArray<DiagnosticInfo>(diags.ToArray()),
            new EquatableArray<RowMemberModel>(rowMembers.ToArray()), rowPositional);

        TableModel Empty() => new(Names.Namespace(type), type.Name, Names.Of(type), Names.Containing(type), code, "First", null, null, 0,
            editableExpr, null, guard, false, situation is null ? "" : Names.Of(situation), "", "Row", "", false, false, false, null, null,
            default, LocationInfo.From(loc), new EquatableArray<DiagnosticInfo>(diags.ToArray()));
    }

    private static ITypeSymbol Unwrap(ITypeSymbol t)
        => t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : t;

    /// <summary>Derived-field refs of Then columns, resolved against the owning rule set.</summary>
    public static TableModel Resolve(TableModel t, RuleSetModel? owner, List<DiagnosticInfo> diags)
    {
        var cols = t.Columns.AsSpan().ToArray();
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c.IsWhen || c.FieldRefExpr is not null || c.Field.Length == 0 || c.IsLineField || c.Directive is "Deny" or "Warn" or "RequireApproval" or "DecreasePercent" or "DecreaseAmount" or "IncreasePercent" or "IncreaseAmount") continue;
            var d = owner?.Derived.AsSpan().ToArray().FirstOrDefault(x => x.Name == c.Field);
            if (d is null)
            {
                diags.Add(new DiagnosticInfo("YFR0101", t.Location, new EquatableArray<string>([c.Member, t.TypeName, c.Field, t.SituationFqn.Substring(t.SituationFqn.LastIndexOf('.') + 1)])));
                continue;
            }
            cols[i] = c with { FieldRefExpr = $"{owner!.FullyQualified}.{d.Member}.Ref" };
        }
        return t with { Columns = new EquatableArray<ColumnModel>(cols) };
    }

    public static void Emit(SourceProductionContext spc, TableModel t)
    {
        foreach (var d in t.Diagnostics) if (d.Id is "YFR0101" or "YFR0102" or "YFR0103" or "YFR0104" or "YFR0105" or "YFR0107") return;
        foreach (var c in t.Columns) if (!c.IsWhen && c.Field.Length > 0 && c.FieldRefExpr is null && c.Directive is "LimitMin" or "LimitMax" or "Grant") return;

        string ts = t.SituationFqn, self = t.FullyQualified, row = t.RowFqn;
        var when = t.Columns.AsSpan().ToArray().Where(c => c.IsWhen).ToArray();
        var then = t.Columns.AsSpan().ToArray().Where(c => !c.IsWhen).ToArray();
        bool lineTable = t.IsLineTable;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> YFex.Rules.Generator — decision table");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591");
        if (t.Namespace.Length > 0) sb.Append("namespace ").Append(t.Namespace).AppendLine(";");
        foreach (var c in t.Containing) sb.Append("partial ").Append(c.Keyword).Append(' ').Append(c.Name).AppendLine(" {");
        sb.Append("partial class ").Append(t.TypeName).Append(" : global::YFex.Rules.IDecisionTable<").Append(ts).Append(", ").Append(row).AppendLine(">");
        sb.AppendLine("{");
        sb.Append("    public static string Code => ").Append(Names.Literal(t.Code)).AppendLine(";");
        sb.AppendLine();

        // Schema
        sb.Append("    public static global::YFex.Rules.TableSchema Schema { get; } = new(").Append(Names.Literal(t.Code)).Append(", ").Append(Names.Literal(t.SchemaName))
          .Append(", global::YFex.Rules.HitPolicy.").Append(t.HitPolicy).Append(", ").Append(t.Editable).Append(", ")
          .Append(t.PublishPermission is null ? "null" : Names.Literal(t.PublishPermission)).Append(", isLineTable: ").Append(lineTable ? "true" : "false")
          .Append(", complete: ").Append(t.Complete ? "true" : "false").AppendLine(",");
        sb.AppendLine("    [");
        foreach (var c in t.Columns)
        {
            sb.Append("        new(").Append(c.Index).Append(", ").Append(Names.Literal(c.Member)).Append(", global::YFex.Rules.ColumnRole.").Append(c.IsWhen ? "When" : "Then")
              .Append(", global::YFex.Rules.CellShape.").Append(c.Cell).Append(", ").Append(Names.Literal(c.Field)).Append(", ").Append(Shapes.RawTypeOf(c.ValueKind))
              .Append(", global::YFex.Rules.DirectiveKind.").Append(c.Directive);
            if (c.EnumMembers.Length > 0)
            {
                sb.Append(", enumOptions: [");
                for (int k = 0; k < c.EnumMembers.Length; k++)
                    sb.Append(k > 0 ? ", " : "").Append("new(").Append(c.EnumMembers[k].Value).Append("L, ").Append(Names.Literal(c.EnumMembers[k].Code))
                      .Append(", ").Append(Names.Literal(c.EnumMembers[k].Name)).Append(')');
                sb.Append(']');
            }
            if (t.RowValidatable) sb.Append(", constraints: ").Append(row).Append(".TryGetConstraints(").Append(Names.Literal(c.Member)).Append(", out var __c").Append(c.Index).Append(") ? __c").Append(c.Index).Append(" : default");
            if (c.IsLineField) sb.Append(", lineField: true");
            sb.AppendLine("),");
        }
        sb.AppendLine("    ]);");
        sb.AppendLine();

        if (!t.HasDefaultRows)
            sb.Append("    static global::System.ReadOnlySpan<").Append(row).Append("> global::YFex.Rules.IDecisionTable<").Append(ts).Append(", ").Append(row).AppendLine(">.DefaultRows => default;");
        else if (!t.DefaultRowsIsSpan)
            sb.Append("    static global::System.ReadOnlySpan<").Append(row).Append("> global::YFex.Rules.IDecisionTable<").Append(ts).Append(", ").Append(row).AppendLine(">.DefaultRows => DefaultRows;");
        sb.Append("    public static int ConditionCount => ").Append(when.Length).AppendLine(";");
        sb.AppendLine();

        string LineLocal() => lineTable && t.LinesMember is not null
            ? $"        ref readonly var __line = ref s.{t.LinesMember}.ItemRef(lineIndex);\n" : "";

        // Match
        sb.Append("    public static int Match(in ").Append(row).Append(" row, in ").Append(ts).AppendLine(" s, int lineIndex)");
        sb.AppendLine("    {");
        sb.Append(LineLocal());
        for (int i = 0; i < when.Length; i++)
        {
            var c = when[i];
            string test = c.Cell switch
            {
                CellKind.Match => $"row.{c.Member}.Matches({c.Access})",
                CellKind.Band => $"row.{c.Member}.Contains({c.Access})",
                _ => $"global::System.Collections.Generic.EqualityComparer<{c.CellTypeFqn}>.Default.Equals(row.{c.Member}, {c.Access})",
            };
            sb.Append("        if (!").Append(test).Append(") return ").Append(i).AppendLine(";");
        }
        sb.AppendLine("        return -1;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Emit
        sb.Append("    public static void Emit(in ").Append(row).Append(" row, in ").Append(ts).AppendLine(" s, int lineIndex, ref global::YFex.Rules.DirectiveWriter d)");
        sb.AppendLine("    {");
        string target = lineTable ? "global::YFex.Rules.AdjustTarget.Line" : "global::YFex.Rules.AdjustTarget.Total";
        foreach (var c in then)
        {
            string raw = RawOf(c);
            string exceed = "global::YFex.Rules.Exceed." + (c.OnExceed ?? "Deny");
            string perm = c.Permission is null ? "null" : Names.Literal(c.Permission);
            string path = c.Field.Length == 0 ? "global::YFex.FieldPath.Empty" : $"global::YFex.FieldPath.Of({Names.Literal(c.Field)})";
            switch (c.Directive)
            {
                case "LimitMax": sb.Append("        d.LimitRaw(").Append(c.FieldRefExpr).Append(", default, ").Append(raw).Append(", ").Append(exceed).Append(", ").Append(perm).AppendLine(");"); break;
                case "LimitMin": sb.Append("        d.LimitRaw(").Append(c.FieldRefExpr).Append(", ").Append(raw).Append(", default, ").Append(exceed).Append(", ").Append(perm).AppendLine(");"); break;
                case "Grant": sb.Append("        d.Grant(").Append(c.FieldRefExpr).Append(", row.").Append(c.Member).AppendLine(");"); break;
                case "DecreasePercent" or "DecreaseAmount": sb.Append("        d.Decrease(").Append(target).Append(", row.").Append(c.Member).AppendLine(");"); break;
                case "IncreasePercent" or "IncreaseAmount": sb.Append("        d.Increase(").Append(target).Append(", row.").Append(c.Member).AppendLine(");"); break;
                case "Deny": sb.Append("        if (!string.IsNullOrEmpty(row.").Append(c.Member).Append(")) d.Deny(").Append(path).Append(", row.").Append(c.Member).AppendLine("!);"); break;
                case "Warn": sb.Append("        if (!string.IsNullOrEmpty(row.").Append(c.Member).Append(")) d.Warn(").Append(path).Append(", row.").Append(c.Member).AppendLine("!);"); break;
                case "RequireApproval":
                    sb.Append("        if (!string.IsNullOrEmpty(row.").Append(c.Member).Append(")) d.RequireApproval(row.").Append(c.Member).Append("!, ").Append(path)
                      .Append(", ").Append(Names.Literal(t.Code + ".approval")).AppendLine(");");
                    break;
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // CheckRow
        sb.Append("    public static void CheckRow(in ").Append(row).AppendLine(" row, ref global::YFex.Validation.ViolationWriter w)");
        sb.AppendLine("    {");
        if (t.RowValidatable) sb.Append("        ").Append(row).AppendLine(".Validate(in row, ref w);");
        if (t.GuardrailFqn is not null) sb.Append("        ").Append(t.GuardrailFqn).AppendLine(".Check(in row, ref w);");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Intersects / Covers
        foreach (var (method, op) in new[] { ("Intersects", "Intersects"), ("Covers", "Covers") })
        {
            sb.Append("    public static bool ").Append(method).Append("(in ").Append(row).Append(" a, in ").Append(row).AppendLine(" b, int column) => column switch");
            sb.AppendLine("    {");
            for (int i = 0; i < when.Length; i++)
            {
                var c = when[i];
                string e = c.Cell == CellKind.Value
                    ? $"global::System.Collections.Generic.EqualityComparer<{c.CellTypeFqn}>.Default.Equals(a.{c.Member}, b.{c.Member})"
                    : $"a.{c.Member}.{op}(b.{c.Member})";
                sb.Append("        ").Append(i).Append(" => ").Append(e).AppendLine(",");
            }
            sb.AppendLine("        _ => true,");
            sb.AppendLine("    };");
        }
        sb.AppendLine();

        EmitConditions(sb, row, when);
        EmitRowJson(sb, t);

        // Conveniences (05 §2 names)
        sb.Append("    public static global::YFex.Rules.FrozenTable<").Append(row).Append("> Freeze(global::System.ReadOnlySpan<").Append(row)
          .Append("> rows, in global::YFex.Rules.TableVersionInfo info) => global::YFex.Rules.DecisionTable.Freeze<").Append(ts).Append(", ").Append(self).Append(", ").Append(row)
          .AppendLine(">(rows, default, in info);");
        sb.Append("    public static bool Check(global::System.ReadOnlySpan<").Append(row).Append("> rows, ref global::YFex.Validation.ViolationWriter w) => global::YFex.Rules.DecisionTable.Check<")
          .Append(ts).Append(", ").Append(self).Append(", ").Append(row).AppendLine(">(rows, default, ref w);");
        sb.AppendLine("}");
        foreach (var _ in t.Containing) sb.AppendLine("}");
        spc.AddSource(SituationGen.Hint(t.FullyQualified) + ".Table.g.cs", sb.ToString());
    }

    /// <summary>
    /// Untyped condition cells + closed domains (enum members, bool) for publish analysis (<c>Complete</c> gap check) and
    /// non-generic editors.
    /// </summary>
    private static void EmitConditions(StringBuilder sb, string row, ColumnModel[] when)
    {
        sb.Append("    public static global::YFex.Rules.ConditionCell GetCondition(in ").Append(row).AppendLine(" row, int column) => column switch");
        sb.AppendLine("    {");
        for (int i = 0; i < when.Length; i++)
        {
            var c = when[i];
            string e = c.Cell switch
            {
                CellKind.Match when c.CellIsEnum => $"global::YFex.Rules.ConditionCell.OfEnum(row.{c.Member})",
                CellKind.Match or CellKind.Band => $"global::YFex.Rules.ConditionCell.Of(row.{c.Member})",
                _ => $"global::YFex.Rules.ConditionCell.OfValue({RawOf(c)})",
            };
            sb.Append("        ").Append(i).Append(" => ").Append(e).AppendLine(",");
        }
        sb.AppendLine("        _ => global::YFex.Rules.ConditionCell.Unknown,");
        sb.AppendLine("    };");
        sb.AppendLine();

        var domains = new List<(int Index, string Values)>();
        for (int i = 0; i < when.Length; i++)
        {
            var c = when[i];
            if (c.Cell == CellKind.Band) continue;
            if (c.CellIsEnum && c.EnumMembers.Length > 0)
                domains.Add((i, string.Join(", ", c.EnumMembers.AsSpan().ToArray().Select(m => $"global::YFex.RawValue.FromEnum({c.CellTypeFqn}.{m.Name})"))));
            else if (c.ValueKind == "Bool")
                domains.Add((i, "global::YFex.RawValue.From(false), global::YFex.RawValue.From(true)"));
        }
        foreach (var (index, values) in domains)
            sb.Append("    private static readonly global::YFex.RawValue[] __domain").Append(index).Append(" = [").Append(values).AppendLine("];");
        sb.AppendLine("    public static global::YFex.RawValue[]? ConditionDomain(int column) => column switch");
        sb.AppendLine("    {");
        foreach (var (index, _) in domains) sb.Append("        ").Append(index).Append(" => __domain").Append(index).AppendLine(",");
        sb.AppendLine("        _ => null,");
        sb.AppendLine("    };");
        sb.AppendLine();
    }

    /// <summary>RowsJson (05 §2): WriteRow / ReadRow over every row member; unsupported member types → NotSupported.</summary>
    private static void EmitRowJson(StringBuilder sb, TableModel t)
    {
        string row = t.RowFqn;
        var members = t.RowMembers.AsSpan().ToArray();
        var unsupported = members.FirstOrDefault(m => !m.Supported);
        sb.Append("    public static void WriteRow(global::System.Text.Json.Utf8JsonWriter writer, in ").Append(row).AppendLine(" row)");
        sb.AppendLine("    {");
        if (unsupported is not null)
            sb.Append("        throw new global::System.NotSupportedException(").Append(Names.Literal($"Row member '{unsupported.Name}' ({unsupported.ValueTypeFqn.Replace("global::", "")}) has no JSON form.")).AppendLine(");");
        else
            foreach (var m in members)
            {
                sb.Append("        writer.WritePropertyName(").Append(Names.Literal(m.Name)).AppendLine(");");
                sb.Append("        ").Append(m.Cell switch
                {
                    CellKind.Match => $"global::YFex.Rules.TableJson.WriteMatch(writer, row.{m.Name}, {WriteFn(m)});",
                    CellKind.Band => $"global::YFex.Rules.TableJson.WriteBand(writer, row.{m.Name}, {WriteFn(m)});",
                    _ => m.IsEnum ? $"global::YFex.Rules.EnumJson<{m.ValueTypeFqn}>.Write(writer, row.{m.Name});"
                                  : $"global::YFex.Rules.JsonScalar.Write(writer, row.{m.Name});",
                }).AppendLine();
            }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.Append("    public static ").Append(row).AppendLine(" ReadRow(ref global::System.Text.Json.Utf8JsonReader reader)");
        sb.AppendLine("    {");
        if (unsupported is not null)
        {
            sb.Append("        throw new global::System.NotSupportedException(").Append(Names.Literal($"Row member '{unsupported.Name}' has no JSON form.")).AppendLine(");");
        }
        else
        {
            for (int i = 0; i < members.Length; i++)
                sb.Append("        ").Append(members[i].TypeFqn).Append(" __m").Append(i).AppendLine(" = default!;");
            sb.AppendLine("        while (global::YFex.Rules.TableJson.NextProperty(ref reader, out var __name))");
            sb.AppendLine("        {");
            sb.AppendLine("            switch (__name)");
            sb.AppendLine("            {");
            for (int i = 0; i < members.Length; i++)
            {
                var m = members[i];
                string read = m.Cell switch
                {
                    CellKind.Match => $"global::YFex.Rules.TableJson.ReadMatch(ref reader, {ReadFn(m)})",
                    CellKind.Band => $"global::YFex.Rules.TableJson.ReadBand(ref reader, {ReadFn(m)})",
                    _ => m.IsEnum ? $"global::YFex.Rules.EnumJson<{m.ValueTypeFqn}>.Read(ref reader)" : $"global::YFex.Rules.JsonScalar.Read<{m.TypeFqn}>(ref reader)",
                };
                sb.Append("                case ").Append(Names.Literal(m.Name)).Append(": __m").Append(i).Append(" = ").Append(read).AppendLine("; break;");
            }
            sb.AppendLine("                default: reader.Skip(); break;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            if (t.RowPositional)
                sb.Append("        return new ").Append(row).Append('(').Append(string.Join(", ", Enumerable.Range(0, members.Length).Select(i => "__m" + i))).AppendLine(");");
            else
                sb.Append("        return new ").Append(row).Append(" { ").Append(string.Join(", ", members.Select((m, i) => m.Name + " = __m" + i))).AppendLine(" };");
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        static string WriteFn(RowMemberModel m) => m.IsEnum ? $"global::YFex.Rules.EnumJson<{m.ValueTypeFqn}>.Write" : $"global::YFex.Rules.JsonScalar.Write<{m.ValueTypeFqn}>";
        static string ReadFn(RowMemberModel m) => m.IsEnum ? $"global::YFex.Rules.EnumJson<{m.ValueTypeFqn}>.Read" : $"global::YFex.Rules.JsonScalar.Read<{m.ValueTypeFqn}>";
    }

    private static string RawOf(ColumnModel c)
    {
        string e = "row." + c.Member;
        if (c.CellIsEnum) return $"global::YFex.RawValue.FromEnum({e})";
        return $"global::YFex.RawValueConvert.From({e})";
    }
}
