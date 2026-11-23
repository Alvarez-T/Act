using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace YFex.Reasons;

/// <summary>What a <see cref="WhyItem"/> is about; a UI picks its icon and color from it (⚑WH1, WH6).</summary>
public enum WhyKind : byte
{
    /// <summary>Neutral information.</summary>
    Info = 0,
    /// <summary>Something that acted: an applied discount, surcharge or rule.</summary>
    Applied = 1,
    /// <summary>Something that could have acted and did not (a condition was false, it lost to another, it is not in force).</summary>
    NotApplied = 2,
    /// <summary>A minimum or maximum on a value.</summary>
    Limit = 3,
    /// <summary>The allowed choices of a value.</summary>
    Options = 4,
    /// <summary>A warning: the action may go on.</summary>
    Warning = 5,
    /// <summary>A denial: the action cannot go on as it is.</summary>
    Denied = 6,
    /// <summary>The action needs someone's approval.</summary>
    NeedsApproval = 7,
}

/// <summary>State of one <see cref="WhyCondition"/> of a checklist.</summary>
public enum WhyCheck : byte
{
    /// <summary>The condition holds.</summary>
    Passed = 0,
    /// <summary>The condition that decided it: it does not hold.</summary>
    Failed = 1,
    /// <summary>Not evaluated (after the deciding condition, and not safe to evaluate on its own).</summary>
    NotChecked = 2,
    /// <summary>Not evaluated by the producer, but compared on its own: it would hold.</summary>
    WouldPass = 3,
    /// <summary>Not evaluated by the producer, but compared on its own: it would not hold either.</summary>
    WouldFail = 4,
    /// <summary>The producer cannot tell outside its own evaluation.</summary>
    Unknown = 5,
}

/// <summary>One level of an item's breadcrumb: a stable <see cref="Key"/> to filter and link by, and its display text.</summary>
/// <param name="Key">The level's identity (<c>group:sale/Installments</c>); its scheme names the level.</param>
/// <param name="Text">Display text ("Parcelamento").</param>
public readonly record struct WhyStep(WhyKey Key, string Text);

/// <summary>
/// One condition of a checklist, pre-formatted: "requer Total ≥ R$ 1.000,00" with "atual R$ 940,00" and "faltam R$ 60,00".
/// </summary>
/// <param name="Text">The condition.</param>
/// <param name="State">Whether it holds.</param>
/// <param name="Actual">The current value, when the producer knows it ("atual R$ 940,00").</param>
/// <param name="Missing">How far it is from holding ("faltam R$ 60,00").</param>
public sealed record WhyCondition(string Text, WhyCheck State, string? Actual = null, string? Missing = null);

/// <summary>A related item, pre-formatted: "exclusiva com Black Friday" pointing at that item's <see cref="WhyItem.Key"/>.</summary>
/// <param name="Text">Display text.</param>
/// <param name="Target">The other item's key, or a level's key (a group).</param>
public sealed record WhyLink(string Text, WhyKey Target);

/// <summary>
/// One line of a "why" answer (⚑WH1): every text already formatted by the producer for one culture, every identity a
/// <see cref="WhyKey"/>. A UI only renders it; it never interprets the producer's rules.
/// </summary>
/// <param name="Title">"Parcelas", "Cliente Gold".</param>
/// <param name="Kind">What the line is about.</param>
/// <example><code>
/// new WhyItem("Parcelas", WhyKind.Limit) { Detail = "até 10x", Field = "Installments", Key = WhyKey.Create("rule", "installments.first-line") };
/// </code></example>
public sealed record WhyItem(string Title, WhyKind Kind)
{
    /// <summary>What happened: "até 10x", "requer Total ≥ R$ 1.000,00 (atual R$ 940,00 · faltam R$ 60,00)".</summary>
    public string? Detail { get; init; }

    /// <summary>A longer description of the source (a rule's description), when it has one.</summary>
    public string? Description { get; init; }

    /// <summary>The size of an applied change: "− R$ 57,50".</summary>
    public string? Amount { get; init; }

    /// <summary>The path of the field the line acts on (<c>"Installments"</c>, <c>"Lines[2].Quantity"</c>); null when none.</summary>
    public string? Field { get; init; }

    /// <summary>The step of the process the line belongs to ("Limites"), for step and waterfall layouts.</summary>
    public string? Stage { get; init; }

    /// <summary>Where the acting data comes from, for admins and support: "installments.card.12x · v3 · Loja 12".</summary>
    public string? Reference { get; init; }

    /// <summary>Breadcrumb from the outermost level down to the source.</summary>
    public ImmutableArray<WhyStep> Path { get; init; } = [];

    /// <summary>The checklist of a source that did not act because a condition was false.</summary>
    public ImmutableArray<WhyCondition> Conditions { get; init; } = [];

    /// <summary>Related items.</summary>
    public ImmutableArray<WhyLink> Links { get; init; } = [];

    /// <summary>The line's identity (the source rule, row …); what actions and links use.</summary>
    public WhyKey Key { get; init; }

    /// <summary>
    /// A what-if the producer can run for this line ("Simular", validation/14 ⚑RX37): pass it to
    /// <see cref="IWhySource.TryWhatIf"/>; empty when there is none (the UI hides the action).
    /// </summary>
    public WhyKey WhatIf { get; init; }
}

/// <summary>
/// A complete "why" answer (⚑WH1): a title, the items, and for amounts the value before and after. Producers (YFex.Rules,
/// and later Authorization, Licensing, Forms) build it; UIs render it; <see cref="WhyJson"/> writes it for AI agents.
/// </summary>
/// <param name="Title">"Parcelas" for one field, the process name for a whole decision.</param>
/// <param name="Items">The lines, in the producer's order.</param>
/// <example><code>
/// if (source.TryGetWhy(FieldPath.Of("Installments"), catalog, culture, out var report))
///     foreach (var item in report.Items) Console.WriteLine($"{item.Title}: {item.Detail}");
/// </code></example>
public sealed record WhyReport(string Title, ImmutableArray<WhyItem> Items)
{
    /// <summary>The path of the field the report is about; null for a whole decision or a receipt.</summary>
    public string? Field { get; init; }

    /// <summary>The field's current value, formatted ("10"); null when the report is not about one field.</summary>
    public string? Value { get; init; }

    /// <summary>Receipt: the amount before any change ("R$ 1.200,00").</summary>
    public string? Initial { get; init; }

    /// <summary>Receipt: the amount after every change.</summary>
    public string? Final { get; init; }

    /// <summary>The worst kind among the items (Denied, NeedsApproval, Warning, Limit/Options, Applied, NotApplied, Info), for one icon per field.</summary>
    [JsonIgnore]
    public WhyKind Worst
    {
        get
        {
            var worst = WhyKind.Info;
            foreach (var item in Items) if (Rank(item.Kind) > Rank(worst)) worst = item.Kind;
            return worst;
        }
    }

    private static int Rank(WhyKind kind) => kind switch
    {
        WhyKind.Denied => 6,
        WhyKind.NeedsApproval => 5,
        WhyKind.Warning => 4,
        WhyKind.Limit or WhyKind.Options => 3,
        WhyKind.Applied => 2,
        WhyKind.NotApplied => 1,
        _ => 0,
    };
}

/// <summary>
/// Something a UI can ask "why is this field like this?" (⚑WH1, WH3): generated for view models with a
/// <c>[ConstraintsFrom]</c> decision (YFex.Validation), or written by hand. The caller passes the catalog and culture (⚑LZ26),
/// so the source holds no catalog that would go stale when the culture changes.
/// </summary>
/// <example><code>
/// if (vm is IWhySource why &amp;&amp; why.TryGetWhy(FieldPath.Of("Installments"), catalog, culture, out var report))
///     flyout.Show(report);
/// </code></example>
public interface IWhySource
{
    /// <summary>
    /// The answer for the field at <paramref name="path"/> (a view-model property path; <see cref="FieldPath.Empty"/> = the
    /// whole decision). False when nothing concerns that field, so the UI shows no "?".
    /// </summary>
    bool TryGetWhy(in FieldPath path, global::YFex.Localization.IMessageCatalog catalog, System.Globalization.CultureInfo culture,
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out WhyReport report);

    /// <summary>
    /// Runs the what-if behind an item's <see cref="WhyItem.WhatIf"/> key and answers for the field at <paramref name="path"/> in
    /// the changed state: the report's title is the one-line summary ("Com Pagamento = CreditCard: até 12"). False when the key is
    /// not this source's or the what-if cannot run.
    /// </summary>
    bool TryWhatIf(WhyKey key, in FieldPath path, global::YFex.Localization.IMessageCatalog catalog, System.Globalization.CultureInfo culture,
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out WhyReport report);

    /// <summary>Raised with a path (<c>""</c> = any) when an answer may have changed: an open "why" view asks again.</summary>
    event Action<string>? WhyChanged;
}
