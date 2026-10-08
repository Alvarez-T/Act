using System.Runtime.CompilerServices;

namespace YFex.Rules.Dynamic;

/// <summary>
/// The <see cref="ExprSchema"/> of a situation type, built once from <see cref="ISituation{TSelf}.Schema"/>: top-level fields 1:1
/// with the situation's field ids (so programs read through <c>TS.Read</c> without remapping), the lines field as the default
/// collection named after it (its id = the lines field id), enum fields with their options, <c>Exposed</c> copied.
/// </summary>
internal static class SituationExprSchema<TS> where TS : ISituation<TS>
{
    public static ExprSchema Schema { get; } = Build(TS.Schema);

    private static ExprSchema Build(SituationSchema s)
    {
        var fields = new ExprField[s.Count];
        for (int i = 0; i < fields.Length; i++) fields[i] = Field(s[i]);
        ExprCollection[]? collections = null;
        int defaultCollection = -1;
        if (s.LinesFieldId >= 0 && s.LineSchema is { } lines)
        {
            var items = new ExprField[lines.Count];
            for (int i = 0; i < items.Length; i++) items[i] = Field(lines[i]);
            collections = [new ExprCollection((ushort)s.LinesFieldId, s[s.LinesFieldId].Name, new ExprSchema(lines.Name, items, noun: "situation"))];
            defaultCollection = 0;
        }
        return new ExprSchema(s.Name, fields, collections, defaultCollection, noun: "situation");
    }

    private static ExprField Field(SituationFieldInfo f)
        => new(f.Id, f.Name, f.Type, f.Exposed,
            f.Kind == ValueKind.EnumCode ? new ExprEnumDomain(f.Name, f.ClrType, [.. f.EnumOptions]) : null);
}

/// <summary>
/// Reads a situation for expression programs: a ref struct over <c>ref readonly TS</c> (no copy), so
/// <see cref="ExprProgram.Run{TReader}"/> is specialized per situation type and calls <c>TS.Read/ReadLine/LineCount</c> directly.
/// The only collection is the lines field, so the collection id is not needed.
/// </summary>
internal readonly ref struct SituationReader<TS> : IFieldReader where TS : ISituation<TS>
{
    private readonly ref readonly TS _s;

    public SituationReader(ref readonly TS s) => _s = ref s;

    public RawValue Read(ushort fieldId) => TS.Read(in _s, fieldId);

    public int Count(ushort collectionId) => TS.LineCount(in _s);

    public RawValue ReadItem(ushort collectionId, int index, ushort fieldId) => TS.ReadLine(in _s, index, fieldId);
}

/// <summary>The binder options of one rule set (cached per rule set by <see cref="DynamicRules{TSituation}"/>).</summary>
internal static class RuleExpressions
{
    /// <summary>Slot of <c>$base</c> / <c>$effectiveDate</c> in <see cref="ExprEnv"/>.</summary>
    public const byte BaseSlot = 0, DateSlot = 1;

    public static ExprCompileOptions Options<TS>(RuleSetDefinition<TS> set, FunctionCatalog functions) where TS : ISituation<TS>
    {
        var specials = ExprSpecials.None
            .With(new ExprSpecial("$effectiveDate", ExprType.Date, DateSlot, "the business date the situation is evaluated for (never the clock)"))
            .WithItemIndex("$lineIndex", "index of the current line (per-line rules)");
        specials = set.AmountType is { } amount
            ? specials.With(new ExprSpecial("$base", new ExprType(amount), BaseSlot, "the running amount at the rule's stage"))
            : specials.WithUnavailable("$base", DynamicRuleCodes.NoAmount, $"rule set '{set.Name}' has no amount ($base)");
        return new ExprCompileOptions(SituationExprSchema<TS>.Schema, functions)
        {
            Specials = specials,
            Codes = DynamicRuleCodes.Expression,
            RequireDeterministic = true,
        };
    }

    /// <summary>The environment of one evaluation: running base + effective date.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ExprEnv Env(in RawValue @base, DateOnly date) => ExprEnv.Of(in @base, RawValue.From(date));
}
