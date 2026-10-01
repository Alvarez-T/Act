using Microsoft.CodeAnalysis;

namespace YFex.Validation.Generator.Tests;

/// <summary>View models (07, D22): hooks implemented against the State generator's declarations; YFV0020–0025.</summary>
public class ViewModelGeneratorTests
{
    private const string Usings = "using System; using YFex; using YFex.Validation; using YFex.State;\n";

    private const string Contract = """
        namespace App;
        public sealed partial record CreateCustomer
        {
            [Required, Length(2, 80)] public string Name { get; init; } = "";
            [Required, Length(11, 14)] public string Document { get; init; } = "";
            public AddressDto? Address { get; init; }
        }
        public sealed partial record AddressDto { [Required] public string? City { get; init; } }
        """;

    [Fact]
    public void ViewModel_WithContract_CompilesClean_AndEmitsHooksMapsAndToX()
    {
        var run = GeneratorHarness.RunWithState(Usings + Contract + """

            [ValidatesAs<CreateCustomer>]
            public partial class CustomerVm : StateObject
            {
                [Observable] public partial string Name { get; set; }
                [Observable, MapsTo("Document")] public partial string CpfCnpj { get; set; }
                [Observable, MapsTo("Address.City")] public partial string City { get; set; }
                [Observable, Length(max: 60)] public partial string Nickname { get; set; }
                [Observable] public partial bool Flag { get; set; }
                [Invariant] void NickNotName(ref ViolationWriter w) { if (Nickname == Name) w.Add(Fields.Nickname, "nick.same"); }
            }
            """);

        run.CompileProblems.Should().BeEmpty();
        run.Ids(DiagnosticSeverity.Warning).Should().BeEmpty();
        string src = run.Source("App.CustomerVm.Validation");
        src.Should().Contain("partial void __OnValidate_CpfCnpj(in string value)");
        src.Should().Contain("global::App.CreateCustomer.Rules.Document.Check(this.CpfCnpj, ref w, in Fields.CpfCnpj, __profile)");
        src.Should().Contain("global::App.AddressDto.Rules.City.Check(this.City, ref w, in Fields.City, __profile)");
        src.Should().Contain("case \"Address.City\": to = Fields.City; return true;");
        src.Should().Contain("public global::App.CreateCustomer ToCreateCustomer() => new global::App.CreateCustomer() { Name = Name, Document = CpfCnpj, Address = new global::App.AddressDto() { City = City } };");
        src.Should().Contain("__RunInvariants(0x1UL);", "Name and Nickname are read by the invariant");
        src.Should().Contain("partial void __OnBatchValidated()");
        src.Should().Contain("public global::YFex.State.ServerOutcome ApplyServer<TContract>");
    }

    [Fact]
    public void ViewModel_WithoutRules_GetsNoValidationSource()
    {
        var run = GeneratorHarness.RunWithState(Usings + "namespace App; public partial class Plain : StateObject { [Observable] public partial int X { get; set; } }");

        run.CompileProblems.Should().BeEmpty();
        run.Sources.Keys.Should().NotContain(k => k.Contains("Plain.Validation"));
        run.Source("App.Plain.Observable").Should().Contain("partial void __OnValidate_X(in int value);");
    }

    [Fact]
    public void DerivedViewModel_ChainsToBase()
    {
        var run = GeneratorHarness.RunWithState(Usings + """
            namespace App;
            public partial class BaseVm : StateObject { [Observable, Required] public partial string? A { get; set; } }
            public partial class DerivedVm : BaseVm { [Observable, Range(1, 5)] public partial int B { get; set; } }
            """);

        run.CompileProblems.Should().BeEmpty();
        string derived = run.Source("App.DerivedVm.Validation");
        derived.Should().Contain("protected override void __vValidateAll");
        derived.Should().Contain("base.__vValidateAll(__profile);");
        derived.Should().NotContain("public global::YFex.Validation.ValidationResult ValidateAll(", "only the root declares it");
    }

    [Fact]
    public void YFV0020_UnmappedRequiredMember_EmitsPartialToX()
    {
        var run = GeneratorHarness.RunWithState(Usings + """
            namespace App;
            public sealed partial record Cmd(string Name, string Code) { [Required] public string Name { get; init; } = Name; }
            [ValidatesAs<Cmd>]
            public partial class Vm : StateObject { [Observable] public partial string Name { get; set; } }
            """);

        run.Ids().Should().Contain("YFV0020");
        run.Source("App.Vm.Validation").Should().Contain("public partial global::App.Cmd ToCmd();");
    }

    [Fact]
    public void YFV0021_MapsToUnknownPath()
    {
        var run = GeneratorHarness.RunWithState(Usings + Contract + """

            [ValidatesAs<CreateCustomer>]
            public partial class Vm : StateObject { [Observable, MapsTo("Nope")] public partial string X { get; set; } }
            """);

        run.Ids().Should().Contain("YFV0021");
    }

    [Fact]
    public void YFV0022_IncompatibleType()
    {
        var run = GeneratorHarness.RunWithState(Usings + Contract + """

            [ValidatesAs<CreateCustomer>]
            public partial class Vm : StateObject { [Observable] public partial int Name { get; set; } }
            """);

        run.Ids().Should().Contain("YFV0022");
    }

    [Fact]
    public void YFV0023_LooserThanContract()
    {
        var run = GeneratorHarness.RunWithState(Usings + Contract + """

            [ValidatesAs<CreateCustomer>]
            public partial class Vm : StateObject { [Observable, Length(2, 100)] public partial string Name { get; set; } }
            """);

        run.Ids().Should().Contain("YFV0023");
    }

    [Fact]
    public void YFV0024_AmbiguousContracts_UnlessProfilesDiffer()
    {
        const string contracts = """
            namespace App;
            public sealed partial record A { [Required] public string? Name { get; init; } }
            public sealed partial record B { [Required] public string? Name { get; init; } }
            """;
        var ambiguous = GeneratorHarness.RunWithState(Usings + contracts + """

            [ValidatesAs<A>, ValidatesAs<B>]
            public partial class Vm : StateObject { [Observable] public partial string Name { get; set; } }
            """);
        var profiled = GeneratorHarness.RunWithState(Usings + contracts + """

            [ValidatesAs<A>(Profile = "draft"), ValidatesAs<B>(Profile = "final")]
            public partial class Vm : StateObject { [Observable] public partial string Name { get; set; } }
            """);

        ambiguous.Ids().Should().Contain("YFV0024");
        profiled.Ids().Should().NotContain("YFV0024");
        profiled.CompileProblems.Should().BeEmpty();
    }

    [Fact]
    public void YFV0025_RuleWithoutSetterHook_AndInheritedMatch()
    {
        var run = GeneratorHarness.RunWithState(Usings + Contract + """

            public partial class BaseVm : StateObject { [Observable] public partial string Name { get; set; } }
            [ValidatesAs<CreateCustomer>]
            public partial class Vm : BaseVm
            {
                [Required] public string? NotObservable { get; set; }
                [Observable] public partial string Document { get; set; }
            }
            """);

        var warnings = run.GeneratorDiagnostics.Where(d => d.Id == "YFV0025").Select(d => d.GetMessage()).ToList();
        warnings.Should().Contain(m => m.Contains("NotObservable"));
        warnings.Should().Contain(m => m.Contains("inherited from 'BaseVm'"));
    }
}
