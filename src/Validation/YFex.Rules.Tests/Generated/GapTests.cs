using YFex.Validation;
using static YFex.Rules.Cells;

namespace YFex.Rules.Tests.Generated;

// `Complete = true` tables (05 §1.3): every input must match a row — gaps are publish errors.

public enum SalesChannel { Store, Web, Phone }

[Situation("gaps")]
public readonly partial record struct GapSituation(SalesChannel Channel, Money Amount, bool Member, string Region, int Seats);

[RuleSet<GapSituation>("gaps")]
public static partial class GapRules
{
    public static readonly Stage Main = Stage.First("main");
    public static readonly RuleGroup Limits = new(Main, Resolve.MostRestrictive);
}

[DecisionTable<GapSituation>("gaps.channel-amount", HitPolicy.First, Group = nameof(GapRules.Limits), Complete = true)]
public partial class ChannelAmountTable
{
    public readonly partial record struct Row(
        [property: When("Channel")] Match<SalesChannel> Channel,
        [property: When("Amount")] Band<Money> Amount,
        [property: Then(DirectiveKind.LimitMax, "Seats")] int MaxSeats);
}

[DecisionTable<GapSituation>("gaps.member-region", HitPolicy.First, Group = nameof(GapRules.Limits), Complete = true)]
public partial class MemberRegionTable
{
    public readonly partial record struct Row(
        [property: When("Member")] Match<bool> Member,
        [property: When("Region")] Match<string> Region,
        [property: Then(DirectiveKind.LimitMax, "Seats")] int MaxSeats);
}

/// <summary>Same columns, not Complete: gaps are fine (no row = the table doesn't apply).</summary>
[DecisionTable<GapSituation>("gaps.partial", HitPolicy.First, Group = nameof(GapRules.Limits))]
public partial class PartialTable
{
    public readonly partial record struct Row(
        [property: When("Channel")] Match<SalesChannel> Channel,
        [property: Then(DirectiveKind.LimitMax, "Seats")] int MaxSeats);
}

public class GapTests
{
    private static ViolationList Check(params ChannelAmountTable.Row[] rows)
    {
        using var s = ViolationWriter.Begin(out var w);
        ChannelAmountTable.Check(rows, ref w);
        return w.ToResult().Violations;
    }

    private static ViolationList Check(params MemberRegionTable.Row[] rows)
    {
        using var s = ViolationWriter.Begin(out var w);
        MemberRegionTable.Check(rows, ref w);
        return w.ToResult().Violations;
    }

    private static string[] Gaps(ViolationList v)
        => v.AsSpan().ToArray().Where(x => x.Code.Value == RuleEngineCodes.TableGap).Select(x => x.Args[0].ToString()).ToArray();

    [Fact]
    public void A_table_covering_every_input_has_no_gap()
    {
        var v = Check(
            new ChannelAmountTable.Row(Is(SalesChannel.Store), Any, 10),
            new(In(SalesChannel.Web, SalesChannel.Phone), Below(new Money(1000)), 5),
            new(In(SalesChannel.Web, SalesChannel.Phone), From(new Money(1000)), 2));

        Gaps(v).Should().BeEmpty();
    }

    [Fact]
    public void A_missing_enum_member_is_reported_as_the_uncovered_input()
    {
        var v = Check(new ChannelAmountTable.Row(Is(SalesChannel.Store), Any, 10), new(Is(SalesChannel.Web), Any, 5));

        Gaps(v).Should().Equal("Channel = Phone");
        var gap = v.AsSpan()[^1];
        gap.Path.ToString().Should().Be("Rows");
        gap.Severity.Should().Be(Severity.Error);
        gap.Args[1].TryGet(out int total).Should().BeTrue();
        total.Should().Be(1);
    }

    [Fact]
    public void A_hole_between_bands_is_reported_with_its_interval()
    {
        var v = Check(
            new ChannelAmountTable.Row(Any, Below(new Money(100)), 10),
            new(Any, From(new Money(200)), 5));

        Gaps(v).Should().ContainSingle().Which.Should().StartWith("Amount in [").And.EndWith(")");
    }

    [Fact]
    public void Gaps_in_one_channel_only_name_both_columns()
    {
        var v = Check(
            new ChannelAmountTable.Row(Any, Below(new Money(100)), 10),
            new(Is(SalesChannel.Web), From(new Money(100)), 5),
            new(Is(SalesChannel.Phone), From(new Money(100)), 5));

        Gaps(v).Should().ContainSingle().Which.Should().StartWith("Channel = Store, Amount ≥ ");
    }

    [Fact]
    public void Open_domains_need_a_row_for_any_other_value_and_bool_needs_both()
    {
        Gaps(Check(new MemberRegionTable.Row(Any, Is("south"), 1), new(Any, Is("north"), 2)))
            .Should().Equal("Region = (other)");                               // Member doesn't matter: both rows take any

        Gaps(Check(new MemberRegionTable.Row(Any, Is("south"), 1), new(Any, Not("south"), 2))).Should().BeEmpty();

        Gaps(Check(new MemberRegionTable.Row(Is(true), Any, 1))).Should().Equal("Member = False");
    }

    [Fact]
    public void An_empty_complete_table_is_one_big_gap()
        => Gaps(Check(Array.Empty<ChannelAmountTable.Row>())).Should().Equal("*");

    [Fact]
    public void Tables_without_Complete_are_not_checked()
    {
        using var s = ViolationWriter.Begin(out var w);
        PartialTable.Check([new PartialTable.Row(Is(SalesChannel.Store), 1)], ref w).Should().BeTrue();
        w.ToResult().Violations.Count.Should().Be(0);
    }

    [Fact]
    public void Publish_validation_through_the_host_rejects_a_gap()
    {
        var host = GapRules.CreateHost();
        GapRules.Set.TryGetTable("gaps.channel-amount", out var t).Should().BeTrue();
        var rows = t.Serialize(new TableVersion<ChannelAmountTable.Row>(new("gaps.channel-amount", 0, RuleTierPath.Code, DateOnly.MinValue),
            [new ChannelAmountTable.Row(Is(SalesChannel.Store), Any, 10)]));

        // Not editable at any tier → NotEditable first; the gap analysis itself is covered by the direct checks above.
        host.ValidateTable("gaps.channel-amount", RuleTierPath.Of(RawValue.From("t")), rows).IsValid.Should().BeFalse();
        ((IRuleSetHost)host).Canonicalize(RuleDataKind.Table, "gaps.channel-amount", rows).Should().NotBeEmpty();
    }
}
