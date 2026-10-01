using System.Collections.Concurrent;
using System.ComponentModel;
using YFex.State.Mvvm;
using YFex.State.Validation;
using YFex.Validation;
using ValidationResult = YFex.Validation.ValidationResult; // the obsolete YFex.State.Validation one still exists (one release)

namespace YFex.State.Tests.VmValidation;

/// <summary>
/// Phase 2 (07 §10): YFex.Validation.Generator implements the State generator's setter hooks (D22) for view
/// models with own rules, invariants and [ValidatesAs] contracts.
/// </summary>
public class ViewModelValidationTests
{
    // ── Own rules + display policy ─────────────────────────────────────────

    [Fact]
    public void OwnRule_InvalidValue_IsStored_ButHiddenUntilTouched()
    {
        var vm = new PrinterSettingsVm();
        var indei = (INotifyDataErrorInfo)vm;

        vm.Copies = 11; // 0 is the initial value → not a change, the setter returns early

        vm.Validation.HasErrors.Should().BeTrue("Gates see every error");
        vm.Validation.Get("Copies").Contains(RuleCodes.Range).Should().BeTrue();
        vm.Validation.GetVisible("Copies").IsEmpty.Should().BeTrue("AfterTouched is the default");
        indei.HasErrors.Should().BeFalse();
        vm.Validation.GetStatus("Copies").Should().Be(FieldStatus.Invalid);
    }

    [Fact]
    public void Touch_MakesErrorsVisible_AndRaisesErrorsChanged()
    {
        var vm = new PrinterSettingsVm();
        var source = (IValidationSource)vm;
        var indei = (INotifyDataErrorInfo)vm;
        vm.Copies = 11; // 0 is the initial value → not a change, the setter returns early
        var changed = new List<string?>();
        indei.ErrorsChanged += (_, e) => changed.Add(e.PropertyName);

        source.Touch("Copies");

        source.GetVisibleViolations("Copies").Count.Should().Be(1);
        indei.HasErrors.Should().BeTrue();
        indei.GetErrors("Copies").Cast<ErrorItem>().Single().Violation.Code.Value.Should().Be(RuleCodes.Range);
        changed.Should().Contain("Copies");
    }

    [Fact]
    public void ValidValue_ClearsError_AndStatusBecomesValid()
    {
        var vm = new PrinterSettingsVm();
        vm.Copies = 0;

        vm.Copies = 3;

        vm.Validation.HasErrors.Should().BeFalse();
        vm.Validation.GetStatus("Copies").Should().Be(FieldStatus.Valid);
        vm.Validation.GetStatus("Duplex").Should().Be(FieldStatus.Pristine);
    }

    [Fact]
    public void ValidateAll_ValidatesUntouchedMembers_AndMarksSubmitAttempted()
    {
        var vm = new PrinterSettingsVm { Copies = 2 };

        var result = vm.ValidateAll();

        result.IsValid.Should().BeFalse();
        result.Violations.For(FieldPath.Of("PrinterName")).Contains(RuleCodes.Required).Should().BeTrue();
        vm.Validation.SubmitAttempted.Should().BeTrue();
        vm.Validation.GetVisible("PrinterName").IsEmpty.Should().BeFalse("errors are visible after a submit attempt");
    }

    [Fact]
    public void ShowImmediately_ErrorsVisibleWithoutTouch()
    {
        var vm = new ImmediateVm();

        vm.Code = "";

        vm.Validation.GetVisible("Code").IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void ShowAfterSubmit_TouchIsNotEnough()
    {
        var vm = new AfterSubmitVm();
        vm.Code = "";
        ((IValidationSource)vm).Touch("Code");

        vm.Validation.GetVisible("Code").IsEmpty.Should().BeTrue();

        vm.ValidateAll();
        vm.Validation.GetVisible("Code").IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void ValidateOnCommit_SetterDefers_TouchValidates()
    {
        var vm = new CommitVm();

        vm.Code = "";
        vm.Validation.HasErrors.Should().BeFalse("Commit trigger: the setter only stores");

        ((IValidationSource)vm).Touch("Code");
        vm.Validation.Get("Code").Contains(RuleCodes.Required).Should().BeTrue();
    }

    [Fact]
    public void ValidateOnSubmit_OnlyValidateAllRuns()
    {
        var vm = new CommitVm();
        vm.Late = "";
        ((IValidationSource)vm).Touch("Late");
        vm.Validation.HasErrors.Should().BeFalse();

        vm.ValidateAll();
        vm.Validation.Get("Late").Contains(RuleCodes.Required).Should().BeTrue();
    }

    [Fact]
    public void Constraints_FromOwnRules()
    {
        var source = (IValidationSource)new PrinterSettingsVm();

        source.TryGetConstraints("PrinterName", out var c).Should().BeTrue();
        c.Required.Should().BeTrue();
        c.MaxLength.Should().Be(40);
        source.TryGetConstraints("Duplex", out _).Should().BeFalse();
    }

    // ── Invariants ─────────────────────────────────────────────────────────

    [Fact]
    public void Invariant_RunsWhenAReadMemberChanges_AndErrorLandsOnItsPath()
    {
        var vm = new PrinterSettingsVm { PagesPerSheet = 2 };

        vm.Duplex = true;
        vm.Validation.Get("PagesPerSheet").Contains("printer.duplex-single-page").Should().BeTrue();

        vm.Duplex = false;
        vm.Validation.Get("PagesPerSheet").IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Invariant_NotRun_ForMembersItDoesNotRead()
    {
        var vm = new PrinterSettingsVm();
        vm.Duplex = true;
        int before = vm.InvariantRuns;

        vm.Copies = 4;

        vm.InvariantRuns.Should().Be(before);
    }

    [Fact]
    public void Invariant_RerunKeepsItsVerdict_WhenFieldRulesPass()
    {
        // Regression guard: its own previous violation must not make the invariant skip itself.
        var vm = new PrinterSettingsVm { Duplex = true };
        vm.PagesPerSheet = 2;
        vm.Validation.Get("PagesPerSheet").Contains("printer.duplex-single-page").Should().BeTrue();

        vm.PagesPerSheet = 3;

        vm.Validation.Get("PagesPerSheet").Contains("printer.duplex-single-page").Should().BeTrue();
    }

    [Fact]
    public void Invariant_SkippedAndCleared_WhenAnInputFailsItsOwnRules()
    {
        var vm = new PrinterSettingsVm { Duplex = true };
        vm.PagesPerSheet = 2;

        vm.PagesPerSheet = 0; // Range(1, 16) fails → invariant result would be noise

        var list = vm.Validation.Get("PagesPerSheet");
        list.Contains(RuleCodes.Range).Should().BeTrue();
        list.Contains("printer.duplex-single-page").Should().BeFalse();
    }

    [Fact]
    public void Invariant_ReplacesOnlyItsOwnViolations()
    {
        var vm = new PrinterSettingsVm { Duplex = true };
        vm.PagesPerSheet = 2;
        vm.Validation.Replace("PagesPerSheet", ViolationSource.Manual, 0,
            [new Violation(FieldPath.Of("PagesPerSheet"), "manual.note", Severity.Warning)]);

        vm.Duplex = false;

        var list = vm.Validation.Get("PagesPerSheet");
        list.Contains("printer.duplex-single-page").Should().BeFalse();
        list.Contains("manual.note").Should().BeTrue();
    }

    [Fact]
    public void Batch_RunsInvariantsOnceAtScopeEnd()
    {
        var vm = new PrinterSettingsVm();
        int before = vm.InvariantRuns;

        using (vm.BeginUpdate())
        {
            vm.Duplex = true;
            vm.PagesPerSheet = 2;
            vm.Duplex = false;
            vm.Duplex = true;
            vm.InvariantRuns.Should().Be(before, "deferred inside the batch");
        }

        vm.InvariantRuns.Should().Be(before + 1);
        vm.Validation.Get("PagesPerSheet").Contains("printer.duplex-single-page").Should().BeTrue();
    }

    [Fact]
    public void WhenGuardedRule_RevalidatesWhenTheGuardChanges()
    {
        var vm = new CompanyVm();
        vm.CompanyName = "";
        vm.Validation.HasErrors.Should().BeFalse("not a company yet");

        vm.IsCompany = true;

        vm.Validation.Get("CompanyName").Contains(RuleCodes.Required).Should().BeTrue();
    }

    // ── [ValidatesAs] (CustomerFormVm, 07 §4.1) ────────────────────────────

    [Fact]
    public void Borrowed_SameName_UsesContractRules()
    {
        var vm = new CustomerFormVm();

        vm.Name = "A";

        vm.Validation.Get("Name").Contains(RuleCodes.Length).Should().BeTrue();
    }

    [Fact]
    public void Borrowed_MapsTo_AndNestedMapsTo_UseContractRulesAtVmPath()
    {
        var vm = new CustomerFormVm();

        vm.CpfCnpj = "123";
        vm.City = "";

        var doc = vm.Validation.Get("CpfCnpj");
        doc.Contains(RuleCodes.Length).Should().BeTrue();
        doc[0].Path.Template.Should().Be("CpfCnpj");
        vm.Validation.Get("City").Contains(RuleCodes.Required).Should().BeTrue();
    }

    [Fact]
    public void OwnRules_MergeWithBorrowed_AndInvariantOnVm()
    {
        var vm = new CustomerFormVm();
        vm.Nickname = new string('n', 61);
        vm.Email = "a@b.com";
        vm.ConfirmEmail = "x@b.com";

        vm.Validation.Get("Nickname").Contains(RuleCodes.LengthMax).Should().BeTrue();
        vm.Validation.Get("ConfirmEmail").Contains("customer.email-mismatch").Should().BeTrue();

        vm.ConfirmEmail = "a@b.com";
        vm.Validation.Get("ConfirmEmail").IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Constraints_BorrowedMergedWithOwn()
    {
        var source = (IValidationSource)new CustomerFormVm();

        source.TryGetConstraints("Name", out var name).Should().BeTrue();
        name.Required.Should().BeTrue();
        name.MinLength.Should().Be(2);
        name.MaxLength.Should().Be(80);
        source.TryGetConstraints("CpfCnpj", out var doc).Should().BeTrue();
        doc.MaxLength.Should().Be(14);
    }

    [Fact]
    public void ToContract_MapsBothFlatAndNestedMembers()
    {
        var vm = new CustomerFormVm { Name = "Ann", Email = "a@b.com", CpfCnpj = "12345678901", City = "Rio" };

        CreateCustomer cmd = vm.ToCreateCustomer();

        cmd.Name.Should().Be("Ann");
        cmd.Document.Should().Be("12345678901");
        cmd.Address!.City.Should().Be("Rio");
    }

    [Fact]
    public void ValidateAll_IncludesContractInvariant_MappedToVmPath()
    {
        var vm = new CustomerFormVm
        {
            Name = "same@x.com", Email = "same@x.com", ConfirmEmail = "same@x.com", CpfCnpj = "12345678901", City = "Rio",
        };
        vm.Validation.HasErrors.Should().BeFalse("per-field rules pass");

        var result = vm.ValidateAll();

        result.IsValid.Should().BeFalse();
        vm.Validation.Get("Email").Contains("customer.name-equals-email").Should().BeTrue();
        vm.Validation.Get("Name").IsEmpty.Should().BeTrue("field results are not duplicated by the contract pass");
    }

    [Fact]
    public void ValidateAll_ContractFieldErrorsAreNotDuplicated()
    {
        var vm = new CustomerFormVm { Name = "A" };

        vm.ValidateAll();

        vm.Validation.Get("Name").Count.Should().Be(1);
    }

    [Fact]
    public void ApplyServer_MapsContractPathsToFields_UnmappedToFormLevel_AlwaysVisible()
    {
        var vm = new CustomerFormVm();
        var server = ValidationResult.Of(ViolationList.From([
            new Violation(FieldPath.Of("Document"), "br.document"),
            new Violation(FieldPath.Of("Address.City"), "city.unknown"),
            new Violation(FieldPath.Of("Phone"), "phone.blocked"),
        ]));

        var outcome = vm.ApplyServer<CreateCustomer>(in server);

        outcome.Should().Be(ServerOutcome.Invalid);
        vm.Validation.GetVisible("CpfCnpj").Contains("br.document").Should().BeTrue("server violations are always visible");
        vm.Validation.GetVisible("City").Contains("city.unknown").Should().BeTrue();
        vm.Validation.FormLevel.Contains("phone.blocked").Should().BeTrue();
    }

    [Fact]
    public void ApplyServer_ViolationClearedWhenThePropertyChanges()
    {
        var vm = new CustomerFormVm();
        var server = ValidationResult.Of(new Violation(FieldPath.Of("Document"), "br.document"));
        vm.ApplyServer<CreateCustomer>(in server);

        vm.CpfCnpj = "12345678901";

        vm.Validation.Get("CpfCnpj").Contains("br.document").Should().BeFalse();
    }

    [Fact]
    public void ApplyServer_Valid_ReturnsOk_AndClearsPreviousServerResult()
    {
        var vm = new CustomerFormVm();
        var bad = ValidationResult.Of(new Violation(FieldPath.Of("Document"), "br.document"));
        vm.ApplyServer<CreateCustomer>(in bad);

        vm.ApplyServer<CreateCustomer>(ValidationResult.Valid).Should().Be(ServerOutcome.Ok);

        vm.Validation.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Prefix_BorrowsFromSecondContract_AndMapsBack()
    {
        var vm = new CustomerWithAddressVm();

        vm.AddressZip = "1234567890";
        vm.Validation.Get("AddressZip").Contains(RuleCodes.LengthMax).Should().BeTrue();

        var server = ValidationResult.Of(new Violation(FieldPath.Of("Zip"), "zip.unknown"));
        vm.ApplyServer<CreateAddress>(in server);
        vm.Validation.Get("AddressZip").Contains("zip.unknown").Should().BeTrue();
    }

    [Fact]
    public void Profile_DefaultIsFirstDeclared_AndCanBeSwitched()
    {
        var vm = new DealVm();
        vm.Validation.Profile.Name.Should().Be("draft");

        vm.Customer = "";
        vm.Validation.HasErrors.Should().BeFalse("draft: customer optional");

        vm.Validation.Profile = "final";
        vm.Customer = " ";
        vm.Validation.Get("Customer").Contains(RuleCodes.Required).Should().BeTrue();
    }

    [Fact]
    public void Profile_ValidateAllWithProfile()
    {
        var vm = new DealVm { Customer = "" };

        vm.ValidateAll("draft").IsValid.Should().BeTrue();
        vm.ValidateAll("final").IsValid.Should().BeFalse();
    }

    // ── Async rules ────────────────────────────────────────────────────────

    [Fact]
    public void AsyncRule_RunsAfterSyncRules_WithServices_AndStoresResult()
    {
        using var _ = new Helpers.SyncContextScope(null);
        var vm = new SignupVm();

        vm.Email = "taken@x.com";

        WaitUntil(() => vm.Validation.Get("Email").Contains("email.taken"));
        EmailNotTaken.LastServices.Should().BeSameAs(vm.Services);
    }

    [Fact]
    public void AsyncRule_SyncFailure_SkipsAsync()
    {
        using var _ = new Helpers.SyncContextScope(null);
        var vm = new SignupVm();
        int before = EmailNotTaken.Calls;

        vm.Email = "not-an-email";

        Thread.Sleep(30);
        EmailNotTaken.Calls.Should().Be(before);
        vm.Validation.Get("Email").Contains(RuleCodes.Email).Should().BeTrue();
    }

    [Fact]
    public void AsyncRule_Debounced_OnlyLastValueChecked_StatusValidating()
    {
        using var _ = new Helpers.SyncContextScope(null);
        var vm = new SignupVm();

        vm.Nick = "slow-a";
        vm.Nick = "slow-b";
        vm.Validation.GetStatus("Nick").Should().Be(FieldStatus.Validating);

        WaitUntil(() => vm.Validation.GetStatus("Nick") != FieldStatus.Validating);
        NickFree.Checked.Should().NotContain("slow-a", "the first value was superseded during the debounce delay");
        NickFree.Checked.Should().Contain("slow-b");
    }

    [Fact]
    public void AsyncRule_Throwing_RoutesToFaultHook()
    {
        using var _ = new Helpers.SyncContextScope(null);
        var vm = new SignupVm();

        vm.Email = "boom@x.com";

        WaitUntil(() => vm.Faults.Count == 1);
        vm.Faults[0].Should().Be("Email");
        vm.Validation.HasErrors.Should().BeFalse();
    }

    // ── INDEI ──────────────────────────────────────────────────────────────

    [Fact]
    public void Indei_ErrorItemIdentity_StableBetweenCalls()
    {
        var vm = new ImmediateVm();
        vm.Code = "";
        var indei = (INotifyDataErrorInfo)vm;

        var a = indei.GetErrors("Code").Cast<ErrorItem>().Single();
        var b = indei.GetErrors("Code").Cast<ErrorItem>().Single();

        a.Should().BeSameAs(b);
        a.ToString().Should().Be(a.Message);
        a.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Indei_NullName_ReturnsFormLevelAndAllVisible()
    {
        var vm = new ImmediateVm();
        vm.Code = "";
        vm.Validation.Replace("", ViolationSource.Manual, 0, [new Violation(FieldPath.Empty, "form.problem")]);

        var all = ((INotifyDataErrorInfo)vm).GetErrors(null).Cast<ErrorItem>().ToList();

        all.Should().HaveCount(2);
        all[0].Violation.Code.Value.Should().Be("form.problem");
    }

    // ── Allocation ─────────────────────────────────────────────────────────

    [Fact]
    public void SettingValidValue_OnValidatedProperty_AllocatesNothing()
    {
        var vm = new AllocationVm();
        for (int i = 0; i < 3; i++) { vm.Copies = 2; vm.Copies = 3; vm.Duplex = true; vm.Duplex = false; } // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            vm.Copies = 2;
            vm.Copies = 3;
            vm.Duplex = true;   // re-runs the invariant (valid → nothing stored)
            vm.Duplex = false;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 3000)
        => SpinWait.SpinUntil(condition, timeoutMs).Should().BeTrue("condition should be met in time");
}

// ── Contracts ──────────────────────────────────────────────────────────────

public sealed partial record AddressDto
{
    [Required] public string? City { get; init; }
}

public sealed partial record CreateCustomer
{
    [Required, Length(2, 80)] public string Name { get; init; } = "";
    [Required, Email] public string Email { get; init; } = "";
    [Required, Length(11, 14)] public string Document { get; init; } = "";
    public AddressDto? Address { get; init; }

    [Invariant]
    private static void NameIsNotEmail(in CreateCustomer c, ref ViolationWriter w)
    {
        if (c.Name == c.Email) w.Add(Fields.Email, "customer.name-equals-email");
    }
}

public sealed partial record CreateAddress
{
    [Required] public string? City { get; init; }
    [Length(max: 9)] public string? Zip { get; init; }
}

public sealed partial record SaveDealDraft
{
    [Length(max: 50)] public string? Customer { get; init; }
}

public sealed partial record CloseDeal
{
    [Required, Length(max: 50)] public string? Customer { get; init; }
}

// ── View models ────────────────────────────────────────────────────────────

public partial class PrinterSettingsVm : MvvmStateObject
{
    public int InvariantRuns;

    [Observable, Required, Length(1, 40)] public partial string PrinterName { get; set; }
    [Observable, Range(1, 10)] public partial int Copies { get; set; }
    [Observable] public partial bool Duplex { get; set; }
    [Observable, Range(1, 16)] public partial int PagesPerSheet { get; set; }

    [Invariant]
    private void DuplexNeedsSinglePage(ref ViolationWriter w)
    {
        InvariantRuns++;
        if (Duplex && PagesPerSheet > 1)
            w.Add(Fields.PagesPerSheet, "printer.duplex-single-page");
    }
}

[ValidationOptions(Show = ShowErrors.Immediately)]
public partial class ImmediateVm : MvvmStateObject
{
    [Observable, Required] public partial string Code { get; set; }
}

[ValidationOptions(Show = ShowErrors.AfterSubmit)]
public partial class AfterSubmitVm : StateObject
{
    [Observable, Required] public partial string Code { get; set; }
}

public partial class CommitVm : StateObject
{
    [Observable, Required, ValidateOn(ValidateOn.Commit)] public partial string Code { get; set; }
    [Observable, Required, ValidateOn(ValidateOn.Submit)] public partial string Late { get; set; }
}

public partial class CompanyVm : StateObject
{
    [Observable] public partial bool IsCompany { get; set; }
    [Observable, Required(When = nameof(IsCompany))] public partial string CompanyName { get; set; }
}

[ValidatesAs<CreateCustomer>]
public partial class CustomerFormVm : MvvmStateObject
{
    [Observable] public partial string Name { get; set; }
    [Observable] public partial string Email { get; set; }
    [Observable, MapsTo("Document")] public partial string CpfCnpj { get; set; }
    [Observable, MapsTo("Address.City")] public partial string City { get; set; }

    // UI-only rules merge with the borrowed ones
    [Observable, Required] public partial string ConfirmEmail { get; set; }
    [Observable, Length(max: 60)] public partial string Nickname { get; set; }

    [Invariant]
    private void EmailsMatch(ref ViolationWriter w)
    {
        if (ConfirmEmail != Email) w.Add(Fields.ConfirmEmail, "customer.email-mismatch");
    }
}

[ValidatesAs<CreateCustomer>]
[ValidatesAs<CreateAddress>(Prefix = "Address")]
public partial class CustomerWithAddressVm : StateObject
{
    [Observable] public partial string Name { get; set; }
    [Observable] public partial string Email { get; set; }
    [Observable, MapsTo("Document")] public partial string Document { get; set; }
    [Observable] public partial string AddressCity { get; set; }
    [Observable] public partial string AddressZip { get; set; }

    public CreateCustomer ToCreateCustomer() => new() { Name = Name, Email = Email, Document = Document };
}

[ValidatesAs<SaveDealDraft>(Profile = "draft")]
[ValidatesAs<CloseDeal>(Profile = "final")]
public partial class DealVm : StateObject
{
    [Observable] public partial string Customer { get; set; }
}

public sealed class Marker;

public readonly struct EmailNotTaken : IAsyncValueRule<string>
{
    public static int Calls;
    public static IServiceProvider? LastServices;

    public static RuleCode Code => "email.taken";

    public static async ValueTask<bool> IsValidAsync(string value, IServiceProvider services, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        LastServices = services;
        await Task.Yield();
        if (value.StartsWith("boom", StringComparison.Ordinal)) throw new InvalidOperationException("boom");
        return !value.StartsWith("taken", StringComparison.Ordinal);
    }
}

public readonly struct NickFree : IAsyncValueRule<string>
{
    public static readonly ConcurrentBag<string> Checked = new();

    public static RuleCode Code => "nick.taken";

    public static ValueTask<bool> IsValidAsync(string value, IServiceProvider services, CancellationToken ct)
    {
        Checked.Add(value);
        return ValueTask.FromResult(true);
    }
}

public partial class SignupVm : StateObject
{
    public List<string> Faults { get; } = new();
    public IServiceProvider Services { get; } = new ServiceBox();

    [Observable, Required, Email, ValidateWith<EmailNotTaken>] public partial string Email { get; set; }
    [Observable, ValidateWith<NickFree>, Debounce(60)] public partial string Nick { get; set; }

    protected override IServiceProvider ValidationServices => Services;

    protected override void OnAsyncValidationFaulted(string propertyName, Exception exception)
    {
        lock (Faults) Faults.Add(propertyName);
    }

    private sealed class ServiceBox : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(Marker) ? new Marker() : null;
    }
}

public partial class AllocationVm : StateObject
{
    [Observable, Range(1, 10)] public partial int Copies { get; set; }
    [Observable] public partial bool Duplex { get; set; }
    [Observable] public partial int PagesPerSheet { get; set; }

    [Invariant]
    private void DuplexNeedsSinglePage(ref ViolationWriter w)
    {
        if (Duplex && PagesPerSheet > 1) w.Add(Fields.PagesPerSheet, "printer.duplex-single-page");
    }
}
