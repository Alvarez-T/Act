using System.Globalization;
using YFex.Localization;

namespace YFex.Validation.Tests;

public class ViolationWriterTests
{
    [Fact]
    public void Begin_ScopeDisposesWriter_AndWriterPassesByRef()
    {
        int count;
        {
            using var scope = ViolationWriter.Begin(out var w);
            for (int i = 0; i < 40; i++) AddOne(ref w, i);     // inline → spill (16) → grow (32, 64)
            count = w.Count;
            w.AsSpan()[39].Path.Should().Be(FieldPath.Of("Lines[].Qty", 39));
            scope.Dispose();
            w.Count.Should().Be(0, "the scope disposes the caller's writer through its ref");
            scope.Dispose();                                  // idempotent
        }
        count.Should().Be(40);

        static void AddOne(ref ViolationWriter w, int i) => w.Add(FieldPath.Of("Lines[].Qty", i), "x");
    }

    [Fact]
    public void Begin_DisposesOnException()
    {
        var act = () =>
        {
            using var scope = ViolationWriter.Begin(out var w);
            for (int i = 0; i < 10; i++) w.Add(FieldPath.Of("A"), "x");
            throw new InvalidOperationException("boom");
        };
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Begin_ValidPath_AllocatesNothing()
    {
        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Run();
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);

        static bool Run()
        {
            using var scope = ViolationWriter.Begin(out var w);
            return CustomerDto.Validate(in CustomerDto.ValidSample, ref w);
        }
    }

    [Fact]
    public void InlineThenSpill_KeepsOrderAndCounts()
    {
        var w = new ViolationWriter();
        try
        {
            for (int i = 0; i < 10; i++)
                w.Add(FieldPath.Of("Lines[].Qty", i), "x", i % 2 == 0 ? Severity.Error : Severity.Warning);

            w.Count.Should().Be(10);
            w.ErrorCount.Should().Be(5);
            var span = w.AsSpan();
            for (int i = 0; i < 10; i++) span[i].Path.Should().Be(FieldPath.Of("Lines[].Qty", i));
            w.HasErrorFor(FieldPath.Of("Lines[].Qty", 8)).Should().BeTrue();
            w.HasErrorFor(FieldPath.Of("Lines[].Qty", 9)).Should().BeFalse("index 9 is a warning");

            var result = w.ToResult();
            result.Violations.Count.Should().Be(10);
            result.IsValid.Should().BeFalse();
            result.HasWarnings.Should().BeTrue();
        }
        finally
        {
            w.Dispose();
        }
    }

    [Fact]
    public void CurrentOrigin_IsStamped()
    {
        var w = new ViolationWriter();
        w.CurrentOrigin = new(ViolationSource.Invariant, 7);
        w.Add(FieldPath.Of("A"), "a");
        w.Add(new Violation(FieldPath.Of("B"), "b", origin: new(ViolationSource.Manual, 0)));
        w.Add(new Violation(FieldPath.Of("C"), "c"));
        w.AsSpan()[0].Origin.Should().Be(new ViolationOrigin(ViolationSource.Invariant, 7));
        w.AsSpan()[1].Origin.Source.Should().Be(ViolationSource.Manual, "explicit origins are kept");
        w.AsSpan()[2].Origin.OwnerId.Should().Be(7);
        w.Dispose();
    }

    [Fact]
    public void Empty_ToResult_IsValidDefault()
    {
        var w = new ViolationWriter();
        var r = w.ToResult();
        r.IsValid.Should().BeTrue();
        r.Violations.IsEmpty.Should().BeTrue();
        w.Dispose();
    }
}

public class ValidatorTests
{
    [Fact]
    public void Valid_ObjectPasses_AndCheckReturnsValid()
    {
        var v = Validator.Check(CustomerDto.ValidSample);
        v.IsValid.Should().BeTrue();
        v.TryGetValue(out Valid<CustomerDto> ok).Should().BeTrue();
        ok.Value.Should().Be(CustomerDto.ValidSample);
        ok.Warnings.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Warnings_TravelWithValid()
    {
        var v = Validator.Check(CustomerDto.ValidSample with { Credit = -1m });
        v.TryGetValue(out Valid<CustomerDto> ok).Should().BeTrue();
        ok.Warnings.Count.Should().Be(1);
        ok.Warnings[0].Severity.Should().Be(Severity.Warning);
    }

    [Fact]
    public void Invalid_ReportsCodePathArgs()
    {
        var result = Validator.Validate(CustomerDto.ValidSample with { Name = "A", Age = 200 });
        result.IsValid.Should().BeFalse();
        result.Violations.Count.Should().Be(2);

        ref readonly Violation name = ref result.Violations[0];
        name.Path.ToString().Should().Be("Name");
        name.Code.Should().Be(new RuleCode("len"));
        name.Args.Count.Should().Be(2);
        name.Args[1].TryGet(out int max).Should().BeTrue();
        max.Should().Be(80);

        Validator.Check(CustomerDto.ValidSample with { Name = "" }).TryGetValue(out Invalid bad).Should().BeTrue();
        bad.Result.Violations[0].Code.Value.Should().Be("required");
    }

    [Fact]
    public void Invariant_SkippedWhenInputsFailed_AndStampedWhenRun()
    {
        var run = Validator.Validate(CustomerDto.ValidSample with { Age = 10, Document = "12345678000199" });
        run.Violations.Count.Should().Be(1);
        run.Violations[0].Origin.Should().Be(new ViolationOrigin(ViolationSource.Invariant, 1));

        var skipped = Validator.Validate(CustomerDto.ValidSample with { Age = 999, Document = "12345678000199" });
        skipped.Violations.Contains("customer.minor-cnpj").Should().BeFalse("Age already failed");
    }

    [Fact]
    public void ValidateMember_RunsOnlyThatMember()
    {
        var bad = CustomerDto.ValidSample with { Name = "", Email = "nope" };
        var w = new ViolationWriter();
        CustomerDto.TryGetFieldId("Email", out FieldId id).Should().BeTrue();
        CustomerDto.ValidateMember(id, in bad, ref w).Should().BeFalse();
        w.Count.Should().Be(1);
        w.AsSpan()[0].Code.Value.Should().Be("email");
        w.Dispose();
    }

    [Fact]
    public void Allocation_ValidObject_IsZero()
    {
        var sample = CustomerDto.ValidSample;
        Validator.IsValid(in sample); // warm up (JIT, statics)
        Validator.Check(in sample);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Validator.IsValid(in sample);
            Validator.Check(in sample);
            Validator.Validate(in sample);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
    }

    [Fact]
    public void Allocation_InvalidObject_IsOnlyTheResultArray()
    {
        var sample = CustomerDto.ValidSample with { Name = "" };
        Validator.Validate(in sample);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = Validator.Validate(in sample);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        r.Violations.Count.Should().Be(1);
        int arraySize = 24 + System.Runtime.CompilerServices.Unsafe.SizeOf<Violation>();
        allocated.Should().BeLessThanOrEqualTo(arraySize + 8, "one exact-size Violation[1]");
    }

    [Fact]
    public void RuntimeRules_RunAndProvideConstraints()
    {
        try
        {
            RuntimeRules<CustomerDto>.Register(static (in CustomerDto c, ref ViolationWriter w) =>
            {
                if (c.Credit > 1000m) w.Add(in CustomerDto.Fields.Credit, "credit.max", RawValue.From(1000m));
            });
            RuntimeRules<CustomerDto>.RegisterConstraints("Credit", FieldConstraints.Create(max: RawValue.From(1000m)));

            Validator.Validate(CustomerDto.ValidSample with { Credit = 5000m }).Violations[0].Code.Value.Should().Be("credit.max");
            CustomerDto.TryGetConstraints("Credit", out var c).Should().BeTrue();
            c.Has(ConstraintFlags.Max).Should().BeTrue();
        }
        finally
        {
            RuntimeRules<CustomerDto>.Clear();
        }
    }

    [Fact]
    public unsafe void ValidationSlot_DispatchesThroughFunctionPointer()
    {
        var sample = CustomerDto.ValidSample with { Email = "x" };
        var w = new ViolationWriter();
        ValidationSlot<SlotOnly>.IsSet.Should().BeFalse();
        ValidationSlot<SlotOnly>.Validate(default, ref w).Should().BeTrue("unset slot = nothing to validate");

        ValidationSlot<CustomerDto>.Set(&CustomerDto.Validate);
        ValidationSlot<CustomerDto>.Validate(in sample, ref w).Should().BeFalse();
        w.Count.Should().Be(1);
        w.Dispose();
    }

    private struct SlotOnly;
}

public class ResultAndListTests
{
    private readonly struct PrefixMap : IPathMap
    {
        public bool TryMap(in FieldPath from, out FieldPath to)
        {
            if (from.Template == "Unknown") { to = default; return false; }
            to = from.Prefixed("Customer");
            return true;
        }
    }

    [Fact]
    public void MapPaths_PrefixesAndMovesUnknownToObjectLevel()
    {
        var r = ValidationResult.Of(ViolationList.From(
        [
            new Violation(FieldPath.Of("Name"), "required"),
            new Violation(FieldPath.Of("Unknown"), "x"),
        ]));
        var map = new PrefixMap();
        var mapped = r.MapPaths(ref map);
        mapped.Violations[0].Path.ToString().Should().Be("Customer.Name");
        mapped.Violations[1].Path.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Merge_And_For()
    {
        var a = ValidationResult.Of(new Violation(FieldPath.Of("Lines[].Qty", 1), "min"));
        var b = ValidationResult.Of(new Violation(FieldPath.Of("Name"), "required", Severity.Warning));
        var merged = a.Merge(b);
        merged.Violations.Count.Should().Be(2);
        merged.HasWarnings.Should().BeTrue();
        ValidationResult.Valid.Merge(b).Violations.Count.Should().Be(1);

        merged.Violations.For(FieldPath.Of("Lines[].Qty", 1)).Count.Should().Be(1);
        merged.Violations.For("Lines[1].Qty").Count.Should().Be(1);
        merged.Violations.For("Lines[2].Qty").IsEmpty.Should().BeTrue();

        int n = 0;
        foreach (ref readonly Violation v in merged.Violations) n += v.Args.Count + 1;
        n.Should().Be(2);
    }

    [Fact]
    public void Collector_PoolsAndAccumulates()
    {
        var c = ViolationCollector.Rent();
        for (int i = 0; i < 9; i++) c.Add(FieldPath.Of("Lines[].Qty", i), "stock.out", RawValue.From(i));
        c.HasErrors.Should().BeTrue();
        c.ToResult().Violations.Count.Should().Be(9);
        c.Dispose();

        var again = ViolationCollector.Rent();
        again.Count.Should().Be(0);
        again.Dispose();
    }

    [Fact]
    public void FieldConstraints_MergeIsMostRestrictive()
    {
        var attr = FieldConstraints.Create(required: true, minLength: 2, maxLength: 80, max: RawValue.From(12));
        var rule = FieldConstraints.Create(maxLength: 40, min: RawValue.From(2), max: RawValue.From(6),
            options: OptionSet.Range(1, 6), format: "br.document", maxSeverity: Severity.Warning);

        var m = attr.Merge(rule);
        m.Required.Should().BeTrue();
        m.MinLength.Should().Be(2);
        m.MaxLength.Should().Be(40);
        m.Min.Should().Be(RawValue.From(2));
        m.Max.Should().Be(RawValue.From(6));
        m.Options.Should().Be(OptionSet.Range(1, 6));
        m.Format.Value.Should().Be("br.document");
        m.MaxSeverity.Should().Be(Severity.Error);
        FieldConstraints.None.Merge(rule).Should().Be(rule);
    }

    [Fact]
    public void Catalog_FormatsViolations_AndDetailBypasses()
    {
        var catalog = new MessageCatalog(new Source());
        var v = new Violation(FieldPath.Of("Name"), "len", args: new RuleArgs(RawValue.From(2), RawValue.From(80)));
        catalog.Format(in v, CultureInfo.InvariantCulture, "Customer.Name").Should().Be("Name: 2..80");

        var d = new Violation(FieldPath.Of("Name"), "dyn", detail: "Custom text");
        catalog.Format(in d, CultureInfo.InvariantCulture).Should().Be("Custom text");
    }

    private sealed class Source : IMessageSource
    {
        public bool TryGetTemplate(string code, string cultureName, out string template)
        {
            template = code == "len" && cultureName.Length == 0 ? "{field}: {0}..{1}" : "";
            return template.Length > 0;
        }

        public bool TryGetFieldName(string fieldKey, string cultureName, out string name)
        {
            name = "";
            return false;
        }
    }
}
