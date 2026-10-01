using YFex.Validation.BuiltIn;

namespace YFex.Validation.Tests;

// Stand-in for the Cqrs builders: any `Validation(Action<ValidationBuilder<T>>)` is parsed by the generator.
public sealed class Cfg<T>
{
    public Cfg<T> Validation(Action<ValidationBuilder<T>> rules)
    {
        ValidationBuilder<T>.Apply(rules);
        return this;
    }
}

public sealed partial record FluentCustomer(string Name, int Age, string Nick, bool IsCompany, string? Document);

public sealed record LooseCustomer(string Name, string Email);   // not partial → everything runtime-only (YFV0003)

public sealed record LooseWith(string Name);

public sealed record LooseEach(IReadOnlyList<FluentCustomer> Items);

internal static class FluentConfigs
{
    public static int MaxNick = 5;

    public static void Customer() => new Cfg<FluentCustomer>().Validation(v =>
    {
        v.Field(c => c.Name).Required().Length(2, 80);
        v.Field(c => c.Age).Range(0, 150);
        v.Field(c => c.Nick).Length(1, MaxNick);                          // non-constant → runtime
        v.Field(c => c.Document).Required().When(c => c.IsCompany);       // compiled When
        v.Invariant(static (in FluentCustomer c, ref ViolationWriter w) =>
        {
            if (c.Age < 18 && c.IsCompany) w.Add(FieldPath.Of("Age"), "customer.minor-company");
        });
    });

#pragma warning disable YFV0003
    public static void Loose() => new Cfg<LooseCustomer>().Validation(v =>
    {
        v.Field(c => c.Name).Required().Length(2, 80);
        v.Field(c => c.Email).Email();
    });

    public static void LooseWithRule() => new Cfg<LooseWith>().Validation(v => v.Field(c => c.Name).With<Email>());

    public static void LooseEachItems() => new Cfg<LooseEach>().Validation(v => v.Each(c => c.Items));
#pragma warning restore YFV0003
}

/// <summary>D27: compiled fluent rules are listed; the runtime builder registers only the leftovers.</summary>
public class FluentRuntimeTests
{
    private static ValidationResult Validate<T>(in T value)
    {
        using var scope = ViolationWriter.Begin(out var w);
        ValidationSlot<T>.Validate(in value, ref w);
        return w.ToResult();
    }

    private static int CountOf(in ValidationResult r, string code)
    {
        int n = 0;
        foreach (ref readonly var v in r.Violations) if (v.Code.Value == code) n++;
        return n;
    }

    [Fact]
    public void Generator_lists_exactly_the_constant_rules()
    {
        CompiledFluentRules<FluentCustomer>.Contains("Name|required").Should().BeTrue();
        CompiledFluentRules<FluentCustomer>.Contains("Name|len:2:80").Should().BeTrue();
        CompiledFluentRules<FluentCustomer>.Contains("Age|range:0:150:0").Should().BeTrue();
        CompiledFluentRules<FluentCustomer>.Contains("Document|required|when:c => c.IsCompany").Should().BeTrue();
        CompiledFluentRules<FluentCustomer>.Contains("Nick|len:1:5").Should().BeFalse();
        FluentCustomer.Rules.Name.MaxLength.Should().Be(80, "constants reach the UI like attribute rules");
    }

    [Fact]
    public void Every_rule_runs_exactly_once()
    {
        FluentConfigs.Customer();
        FluentConfigs.Customer();   // registry rebuild: keyed registration, still once

        var r = Validate(new FluentCustomer("", 200, "toolong", IsCompany: true, Document: null));

        CountOf(r, "required").Should().Be(2, "Name + Document (When IsCompany) — compiled, never duplicated");
        CountOf(r, "range").Should().Be(1);
        CountOf(r, "len").Should().Be(1, "Nick's runtime Length(1, MaxNick)");
        r.Violations.For(FieldPath.Of("Nick"))[0].Args[1].Should().Be(RawValue.From(5));
    }

    [Fact]
    public void Runtime_invariant_runs_through_the_generated_Validate()
    {
        FluentConfigs.Customer();

        var r = Validator.Validate(new FluentCustomer("Ana", 10, "a", IsCompany: true, Document: "1"));

        r.Violations.Contains("customer.minor-company").Should().BeTrue();
    }

    [Fact]
    public void Valid_value_has_no_violations()
    {
        FluentConfigs.Customer();

        Validate(new FluentCustomer("Ana", 30, "ana", IsCompany: false, Document: null)).Violations.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Not_partial_message_is_validated_by_the_slot_fallback()
    {
        FluentConfigs.Loose();

        ValidationSlot<LooseCustomer>.IsSet.Should().BeFalse();
        var r = Validate(new LooseCustomer("A", "not-an-email"));

        r.IsValid.Should().BeFalse();
        CountOf(r, "len").Should().Be(1);
        CountOf(r, "email").Should().Be(1);
        RuntimeRules<LooseCustomer>.TryGetConstraints("Name", out var c).Should().BeTrue();
        c.Required.Should().BeTrue();
        c.MaxLength.Should().Be(80);
    }

    [Fact]
    public void Runtime_required_stops_the_member_chain_like_generated_code()
    {
        FluentConfigs.Loose();

        var r = Validate(new LooseCustomer(" ", "a@b.co"));

        r.Violations.Count.Should().Be(1);
        r.Violations[0].Code.Value.Should().Be("required");
    }

    [Fact]
    public void With_rule_that_was_not_compiled_fails_fast()
    {
        var act = FluentConfigs.LooseWithRule;
        act.Should().Throw<NotSupportedException>().WithMessage("*With<*Email>*partial*");
    }

    [Fact]
    public void Runtime_each_validates_elements_with_their_rules()
    {
        FluentConfigs.LooseEachItems();

        var r = Validate(new LooseEach([new FluentCustomer("Ana", 30, "a", false, null), new FluentCustomer("", 30, "a", false, null)]));

        r.Violations.Count.Should().Be(1);
        r.Violations[0].Path.ToString().Should().Be("Items[1].Name");
    }

    [Theory]
    [InlineData("c => c.Name", "Name")]
    [InlineData("static (x) => x.Address.City", "Address.City")]
    [InlineData("(Customer c) => c.Name", "Name")]
    public void PathOf_reads_member_lambdas(string text, string path)
        => FluentRuleKey.PathOf(text).Should().Be(path);

    [Theory]
    [InlineData("c => c.Name.Trim()")]
    [InlineData("c => other.Name")]
    public void PathOf_rejects_non_member_lambdas(string text)
        => FluentAssertions.FluentActions.Invoking(() => FluentRuleKey.PathOf(text)).Should().Throw<ArgumentException>();
}
