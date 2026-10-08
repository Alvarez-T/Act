using System.Collections.Immutable;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using YFex;
using YFex.Rules;
using YFex.Rules.Dynamic;

namespace YFex.Expressions.Benchmarks;

// Phase X performance guard (phase-x-extraction.md §7). The scenarios mirror the numbers measured in the Phase 6 log
// (validation/13): code-only baseline, a dynamic rule whose condition is false, an applied simple rule, a complex rule
// (sum over lines + 2 functions), and compiling one rule. Evaluations use a pre-resolved CompiledRuleSet so only the
// engine is measured.

public static class Program
{
    // BenchmarkDotNet 0.15.8 can't resolve a net11 SDK for its out-of-process toolchain ("GetRuntimeVersion not
    // implemented"), so benchmarks run in-process (same process, Release build, still with warmup/pilot/statistics).
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args,
            DefaultConfig.Instance.AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance)));
}

public enum PayMethod { Cash, Card, Pix }

[Situation("bench.line")]
public readonly partial record struct BenchLine(string Sku, string Category, int Quantity, Money UnitPrice);

[Situation("bench")]
public readonly partial record struct BenchSituation(
    [property: RuleTier] string Tenant,
    [property: RuleEffectiveDate] DateOnly Date,
    PayMethod Payment,
    bool Member,
    [property: Requested] int Installments,
    Money Total,
    ImmutableArray<BenchLine> Lines);

[RuleSet<BenchSituation>("bench")]
public static partial class BenchRules
{
    public static readonly Stage Items = Stage.First("items");
    public static readonly Stage Totals = Stage.After(Items, "totals");
    public static readonly Stage Limits = Stage.After(Totals, "limits");

    public static readonly RuleGroup Promo = new(Totals, Resolve.All)
    {
        Dynamic = new([nameof(BenchSituation.Tenant)],
            DynamicDirectives.Decrease | DynamicDirectives.Increase | DynamicDirectives.Warn | DynamicDirectives.Deny,
            maxPercent: Percentual.FromPercentage(10), maxAmount: 100m),
    };

    public static readonly RuleGroup Legal = new(Limits, Resolve.MostRestrictive);

    public static Money LineBase(in BenchSituation s, in BenchLine l) => l.UnitPrice * l.Quantity;
}

[Sealed, Rule("bench.legal", Group = nameof(BenchRules.Legal))]
public readonly struct BenchLegal : IRule<BenchSituation>
{
    public static bool Applies(in BenchSituation s) => true;
    public static void Evaluate(in BenchSituation s, ref DirectiveWriter d) => d.Limit(BenchSituation.Fields.Installments, max: 12);
}

[MemoryDiagnoser]
public class DynamicRuleBenchmarks
{
    public const string PixBonus = """
        { "code": "tenant.pix-bonus", "group": "Promo", "priority": 10,
          "when": { "and": [ { "==": [ { "var": "Payment" }, "Pix" ] }, { ">=": [ { "var": "Total" }, 500 ] } ] },
          "then": [ { "decrease": { "target": "total", "percent": 3 } } ] }
        """;

    public const string Capped = """
        { "code": "tenant.capped", "group": "Promo",
          "when": { ">": [ { "sum": { "*": [ { "line": "UnitPrice" }, { "line": "Quantity" } ] } }, 100 ] },
          "then": [ { "decrease": { "target": "total", "amount": { "min": [ { "percentOf": [ { "var": "Total" }, 5 ] }, 30 ] } } } ] }
        """;

    private static readonly DateOnly Today = new(2026, 9, 30);
    private static readonly RuleTierPath Tenant = RuleTierPath.Of(RawValue.From("t1"));

    private CompiledRuleSet<BenchSituation> _codeOnly = null!;
    private CompiledRuleSet<BenchSituation> _pixBonus = null!;
    private CompiledRuleSet<BenchSituation> _capped = null!;
    private BenchSituation _pix, _cash;
    private DynamicRules<BenchSituation> _dynamic = null!;
    private byte[] _pixBonusJson = null!;
    private long _version;

    [GlobalSetup]
    public void Setup()
    {
        ImmutableArray<BenchLine> lines = [new("beer", "drinks", 6, new Money(10)), new("tv", "electronics", 1, new Money(900))];
        _pix = new("t1", Today, PayMethod.Pix, false, 1, new Money(960), lines);
        _cash = _pix with { Payment = PayMethod.Cash };

        _codeOnly = BenchRules.CreateHost().Resolve(Tenant, Today);
        _pixBonus = HostWith(PixBonus).Resolve(Tenant, Today);
        _capped = HostWith(Capped).Resolve(Tenant, Today);

        _dynamic = new DynamicRules<BenchSituation>();
        _pixBonusJson = Encoding.UTF8.GetBytes(PixBonus);
    }

    private RuleSetHost<BenchSituation> HostWith(string json)
    {
        var host = BenchRules.CreateHost().UseRuntimeRules(new DynamicRules<BenchSituation>());
        var payload = Encoding.UTF8.GetBytes(json);
        var spec = RuleSpecJson.Read(payload);
        var check = host.ValidateRule(spec.Code, Tenant, payload);
        if (!check.IsValid) throw new InvalidOperationException(check.ToString());
        var info = new RuleDataInfo(new RuleDataKey("bench", RuleDataKind.Rule, spec.Code, Tenant), ++_version, DateOnly.MinValue, "bench", default, null);
        host.Apply(new RuleDataRecord(info, host.Canonicalize(RuleDataKind.Rule, spec.Code, payload)));
        return host;
    }

    [Benchmark(Baseline = true, Description = "code rules only")]
    public Decision<BenchSituation> CodeOnly() => _codeOnly.Evaluate(in _pix);

    [Benchmark(Description = "dynamic rule, condition false")]
    public Decision<BenchSituation> FalseCondition() => _pixBonus.Evaluate(in _cash);

    [Benchmark(Description = "dynamic rule, applied (simple)")]
    public Decision<BenchSituation> AppliedSimple() => _pixBonus.Evaluate(in _pix);

    [Benchmark(Description = "dynamic rule, sum over lines + 2 functions")]
    public Decision<BenchSituation> Complex() => _capped.Evaluate(in _pix);

    [Benchmark(Description = "parse + bind + compile one rule")]
    public bool CompileOne() => _dynamic.Validate(BenchRules.Set, "tenant.pix-bonus", in Tenant, _pixBonusJson).IsValid;
}
