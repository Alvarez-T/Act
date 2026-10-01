using System.Collections.Immutable;
using System.Globalization;
using YFex.Localization;
using YFex.Validation.BuiltIn;

namespace YFex.Validation.Tests.Generated;

public class GeneratedBehaviourTests
{
    private static ValidationResult Run<T>(T value) where T : IValidatable<T> => Validator.Validate(in value);

    private static IEnumerable<string> Codes(ValidationResult r)
    {
        var list = new List<string>();
        foreach (ref readonly Violation v in r.Violations) list.Add(v.Path + ":" + v.Code);
        return list;
    }

    [Fact]
    public void ValidSample_HasNoViolations()
    {
        var r = Run(CustomerDto.Valid);
        Codes(r).Should().BeEmpty();
        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public void MemberChain_StopsAtFirstError_InDeclarationOrder()
    {
        var r = Run(CustomerDto.Valid with { Name = "", Email = "nope", Age = 200 });
        Codes(r).Should().Equal("Name:required", "Email:email", "Age:range");
    }

    [Fact]
    public void Length_ReportsArgs_AndVariantCodes()
    {
        var r = Run(CustomerDto.Valid with { Name = "A" });
        r.Violations[0].Code.Value.Should().Be("len");
        r.Violations[0].Args.Count.Should().Be(2);
    }

    [Fact]
    public void ValidateWith_TextAndValueRules_WithAndWithoutArgument()
    {
        Codes(Run(CustomerDto.Valid with { Document = "12a" })).Should().Equal("Document:digits");
        var few = Run(CustomerDto.Valid with { Document = "123" });
        Codes(few).Should().Equal("Document:digits.min");
        few.Violations[0].Args[0].TryGet(out int min).Should().BeTrue();
        min.Should().Be(11);

        Codes(Run(CustomerDto.Valid with { Slots = 3 })).Should().Equal("Slots:even");
        Codes(Run(CustomerDto.Valid with { Slots = 6 })).Should().Equal("Slots:multiple");
    }

    [Fact]
    public void AsyncRule_IsMetadataOnly()
    {
        CustomerDto.Rules.Email.HasAsyncRules.Should().BeTrue();
        Run(CustomerDto.Valid with { Email = "taken@x.com" }).IsValid.Should().BeTrue("async rules run in the context stage, not in Validate");
    }

    [Fact]
    public void WarningSeverity_DoesNotInvalidate()
    {
        var r = Run(CustomerDto.Valid with { Nickname = null });
        r.IsValid.Should().BeTrue();
        r.HasWarnings.Should().BeTrue();
        r.Violations[0].Severity.Should().Be(Severity.Warning);
    }

    [Fact]
    public void When_AppliesOnlyIfBoolMemberIsTrue()
    {
        Run(CustomerDto.Valid with { CompanyName = null }).IsValid.Should().BeTrue();
        Codes(Run(CustomerDto.Valid with { IsCompany = true, CompanyName = null }))
            .Should().Equal("CompanyName:customer.company-name");
    }

    [Fact]
    public void Profile_RulesRunForAllOrMatchingProfile()
    {
        var unsigned = CustomerDto.Valid with { SignedBy = null };
        Run(unsigned).IsValid.Should().BeFalse("default = all profiles");

        using var scope = ViolationWriter.Begin(out var w);
        CustomerDto.Validate(in unsigned, ref w, new ProfileId("draft")).Should().BeTrue();
        CustomerDto.Validate(in unsigned, ref w, "final").Should().BeFalse();
    }

    [Fact]
    public void Ranges_OnDecimalMoneyAndNullableDate()
    {
        Codes(Run(CustomerDto.Valid with { CreditLimit = 100000m })).Should().Equal("CreditLimit:range");
        Codes(Run(CustomerDto.Valid with { Balance = new Money(-1m) })).Should().Equal("Balance:range");
        Codes(Run(CustomerDto.Valid with { BirthDate = new DateOnly(1800, 1, 1) })).Should().Equal("BirthDate:range");
        Run(CustomerDto.Valid with { BirthDate = null }).IsValid.Should().BeTrue("null skips Range");

        CustomerDto.Rules.Balance.Constraints.Max.TryGet(out Money max).Should().BeTrue();
        max.Should().Be(new Money(1_000_000m));
    }

    [Fact]
    public void Coerce_ClampsInsteadOfFailing()
    {
        Run(CustomerDto.Valid with { PageSize = 5000 }).IsValid.Should().BeTrue();
        CustomerDto.Rules.PageSize.Coerce(5000).Should().Be(100);
        CustomerDto.Rules.PageSize.Max.Should().Be(100);
    }

    [Fact]
    public void Each_ValidatesElementsWithIndexedPaths()
    {
        var bad = CustomerDto.Valid with
        {
            Addresses = [new AddressDto { City = "Curitiba" }, new AddressDto { City = "X", Zip = "123" }],
            Tags = ["ok", "this-tag-is-too-long"],
        };
        Codes(Run(bad)).Should().Equal("Addresses[1].City:len", "Addresses[1].Zip:pattern", "Tags[1]:len");
    }

    [Fact]
    public void Each_CollectionLevelPresence_UsesMemberPath()
    {
        Codes(Run(CustomerDto.Valid with { Tags = [] })).Should().Equal("Tags:notempty");
    }

    [Fact]
    public void Nested_ValidatesWithPrefix_AndSkipsNull()
    {
        Codes(Run(CustomerDto.Valid with { Billing = new AddressDto { City = "" } })).Should().Equal("Billing.City:required");
        Run(CustomerDto.Valid with { Billing = null }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Invariants_RunLast_GatedByInputs_AndStamped()
    {
        var minor = Run(CustomerDto.Valid with { Age = 10, Document = "12345678000199" });
        Codes(minor).Should().Equal("Document:customer.minor-cnpj");
        minor.Violations[0].Origin.Should().Be(new ViolationOrigin(ViolationSource.Invariant, CustomerDto.InvariantIds.MinorCannotHaveCnpj));

        Codes(Run(CustomerDto.Valid with { Age = 999, Document = "12345678000199" }))
            .Should().BeEquivalentTo(new[] { "Age:range" }, "gated: Age already failed");

        Codes(Run(CustomerDto.Valid with { Tags = ["a", "a"] })).Should().Equal("Tags:tags.duplicated");
    }

    [Fact]
    public void ReadsAllInvariant_AlwaysRuns()
    {
        Codes(Run(new Order { Count = 7 })).Should().Equal(":order.bad");
    }

    [Fact]
    public void DataAnnotations_AreMappedAtCompileTime()
    {
        var r = Run(new LegacyDto { Code = " ", Short = "x", Level = 0, Mail = "bad", Upper = "abc" });
        Codes(r).Should().Equal("Code:required", "Short:len", "Level:range", "Mail:email", "Upper:pattern");
        LegacyDto.Rules.Short.MaxLength.Should().Be(5);
    }

    [Fact]
    public void PositionalRecord_AndStruct()
    {
        Codes(Run(new Point3(11, 0, ""))).Should().Equal("X:range", "Label:required");
        Codes(Run(new Interval(50, 10))).Should().Equal(":interval.order");
        Codes(Run(new Interval(-1, 10))).Should().Equal("From:range");
    }

    [Fact]
    public void Inheritance_RunsBaseRulesFirst_AndContinuesIds()
    {
        Codes(Run(new Employee())).Should().Equal("FirstName:required", "Badge:required");
        Employee.FieldIds.Badge.Should().Be(1);
        Employee.TryGetFieldId("FirstName", out var id).Should().BeTrue();
        id.Value.Should().Be(0);
        Employee.TryGetConstraints("FirstName", out var c).Should().BeTrue();
        c.Required.Should().BeTrue();
    }

    [Fact]
    public void NestedContainers_CqrsShape()
    {
        var sale = new Sales.Commands.CloseSale(0, [new Sales.Commands.SaleLine("", 0m)]);
        Codes(Run(sale)).Should().Equal("Installments:range", "Lines[0].Sku:required", "Lines[0].Quantity:range");
    }

    [Fact]
    public void ValidateMember_And_TryGetFieldId()
    {
        var bad = CustomerDto.Valid with { Name = "", Tags = ["x", "way-too-long-tag"] };
        using var scope = ViolationWriter.Begin(out var w);
        CustomerDto.TryGetFieldId("Tags", out var tags).Should().BeTrue();
        CustomerDto.ValidateMember(tags, in bad, ref w).Should().BeFalse();
        w.Count.Should().Be(1);
        w.AsSpan()[0].Path.ToString().Should().Be("Tags[1]");
    }

    [Fact]
    public void TryGetConstraints_ForMembersNestedAndElements()
    {
        CustomerDto.TryGetConstraints("Name", out var name).Should().BeTrue();
        name.MaxLength.Should().Be(80);
        name.Required.Should().BeTrue();

        CustomerDto.TryGetConstraints("Billing.City", out var city).Should().BeTrue();
        city.MinLength.Should().Be(2);

        CustomerDto.TryGetConstraints("Addresses[3].City", out var elemCity).Should().BeTrue();
        elemCity.MaxLength.Should().Be(60);

        CustomerDto.TryGetConstraints("Tags[0]", out var tag).Should().BeTrue();
        tag.MaxLength.Should().Be(10);

        CustomerDto.TryGetConstraints("Tags", out var tagsC).Should().BeTrue();
        tagsC.MinLength.Should().Be(1, "NotEmpty projects as minLength 1");

        CustomerDto.TryGetConstraints("Unknown", out _).Should().BeFalse();
        CustomerDto.Rules.Document.Constraints.Format.Value.Should().Be("digits");
    }

    [Fact]
    public void ModuleInitializer_FillsValidationSlots()
    {
        ValidationSlot<CustomerDto>.IsSet.Should().BeTrue();
        ValidationSlot<Sales.Commands.CloseSale>.IsSet.Should().BeTrue();
        using var scope = ViolationWriter.Begin(out var w);
        var bad = CustomerDto.Valid with { Name = "" };
        ValidationSlot<CustomerDto>.Validate(in bad, ref w).Should().BeFalse();
    }

    [Fact]
    public void Allocation_ValidGeneratedObject_IsZero()
    {
        var sample = new Point3(1, 2, "p");
        Validator.IsValid(in sample);
        var addr = new AddressDto { City = "Curitiba", Zip = "80000-000" };
        Validator.IsValid(in addr);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Validator.IsValid(in sample);
            Validator.IsValid(in addr);
        }
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void GeneratedModuleCatalog_And_BuiltIns_FormatViolations()
    {
        var catalog = new MessageCatalog(YFex.Validation.Tests.TestsMessages.Instance, BuiltInMessages.Instance);
        var pt = CultureInfo.GetCultureInfo("pt-BR");

        var minor = Run(CustomerDto.Valid with { Age = 10, Document = "12345678000199" });
        catalog.Format(in minor.Violations[0], pt).Should().Be("Menor de idade não pode ter CNPJ");
        catalog.Format(in minor.Violations[0], CultureInfo.InvariantCulture).Should().Be("Minors cannot have a CNPJ");

        var len = Run(CustomerDto.Valid with { Name = "A" });
        catalog.Format(in len.Violations[0], pt, "CustomerDto.Name").Should().Be("Nome completo deve ter entre 2 e 80 caracteres");
        catalog.Format(in len.Violations[0], CultureInfo.GetCultureInfo("en-US"), "CustomerDto.Name")
            .Should().Be("Full name must have between 2 and 80 characters");
    }
}
