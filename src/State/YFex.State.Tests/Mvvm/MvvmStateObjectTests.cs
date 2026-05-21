using System.Collections;
using System.ComponentModel;
using YFex.State.Mvvm;
using YFex.State.Notification;
using YFex.State.Tests.Helpers;
using YFex.State.Validation;

namespace YFex.State.Tests.Mvvm;

public class MvvmStateObjectTests
{
    [Fact]
    public void PropertyChanged_FiresForObservableProperty()
    {
        var vm = new MvvmPersonVm();
        string? lastName = null;
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => lastName = e.PropertyName;

        vm.FirstName = "Alice";

        lastName.Should().Be(nameof(MvvmPersonVm.FirstName));
    }

    [Fact]
    public void PropertyChanging_FiresBeforeValueAssigned()
    {
        var vm = new MvvmPersonVm { FirstName = "before" };
        string? observedAtChanging = null;
        ((INotifyPropertyChanging)vm).PropertyChanging += (_, _) =>
            observedAtChanging = vm.FirstName;

        vm.FirstName = "after";

        observedAtChanging.Should().Be("before",
            "PropertyChanging fires while old value is still in place");
    }

    [Fact]
    public void Implements_AllExpectedInterfaces()
    {
        var vm = new MvvmPersonVm();
        vm.Should().BeAssignableTo<INotifyPropertyChanged>();
        vm.Should().BeAssignableTo<INotifyPropertyChanging>();
        vm.Should().BeAssignableTo<INotifyDataErrorInfo>();
        vm.Should().BeAssignableTo<IChangedHandler>();
        vm.Should().BeAssignableTo<INotifyChanged>();
        vm.Should().BeAssignableTo<IActivatable>();
    }

    [Fact]
    public void INotifyDataErrorInfo_HasErrors_FalseWhenNoValidationBag()
    {
        var vm = new MvvmPersonVm();

        ((INotifyDataErrorInfo)vm).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void INotifyDataErrorInfo_GetErrors_EmptyArrayWhenNoValidationBag()
    {
        var vm = new MvvmPersonVm();

        var errors = ((INotifyDataErrorInfo)vm).GetErrors("X");

        errors.Should().NotBeNull();
        errors.Cast<object>().Should().BeEmpty();
    }

    [Fact]
    public void INotifyDataErrorInfo_HasErrors_TrueAfterValidationSet()
    {
        var vm = new MvvmPersonVm();
        vm.Validation.Set("FirstName", "required");

        ((INotifyDataErrorInfo)vm).HasErrors.Should().BeTrue();
    }

    [Fact]
    public void INotifyDataErrorInfo_GetErrors_ReturnsMessagesForProperty()
    {
        var vm = new MvvmPersonVm();
        vm.Validation.Set("FirstName", "required");

        var msgs = ((INotifyDataErrorInfo)vm).GetErrors("FirstName");

        msgs.Cast<string>().Should().Contain("required");
    }

    [Fact]
    public void Multiple_PropertyChanged_Subscribers_AllReceive()
    {
        var vm = new MvvmPersonVm();
        int a = 0, b = 0;
        vm.PropertyChanged += (_, _) => a++;
        vm.PropertyChanged += (_, _) => b++;

        vm.FirstName = "x";

        a.Should().Be(1);
        b.Should().Be(1);
    }

    [Fact]
    public void Inheritance_DerivedAndBaseProperties_BothFire()
    {
        var vm = new DerivedMvvmPersonVm();
        int firstNameFired = 0, emailFired = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DerivedMvvmPersonVm.FirstName)) firstNameFired++;
            if (e.PropertyName == nameof(DerivedMvvmPersonVm.Email)) emailFired++;
        };

        vm.FirstName = "x";
        vm.Email = "x@y";

        firstNameFired.Should().Be(1);
        emailFired.Should().Be(1);
    }

    [Fact]
    public void EnsureValidationWired_BridgesValidationChangedToErrorsChanged()
    {
        var vm = new ValidationWiredVm();
        vm.WireValidationManually();

        string? observed = null;
        ((INotifyDataErrorInfo)vm).ErrorsChanged += (_, e) => observed = e.PropertyName;

        vm.Validation.Set("FirstName", "required");

        observed.Should().Be("FirstName");
    }

    [Fact]
    public void INDEI_FullRoundTrip_SetThenClear_FiresErrorsChangedTwice_AndGetErrorsReflectsState()
    {
        var vm = new ValidationWiredVm();
        vm.WireValidationManually();
        var indei = (INotifyDataErrorInfo)vm;

        var firedFor = new System.Collections.Generic.List<string?>();
        indei.ErrorsChanged += (_, e) => firedFor.Add(e.PropertyName);

        // Set
        vm.Validation.Set("Email", "invalid");
        indei.HasErrors.Should().BeTrue();
        indei.GetErrors("Email").Cast<string>().Should().Contain("invalid");
        firedFor.Should().ContainSingle().Which.Should().Be("Email");

        // Clear
        vm.Validation.Clear("Email");
        indei.HasErrors.Should().BeFalse();
        indei.GetErrors("Email").Cast<string>().Should().BeEmpty();
        firedFor.Should().HaveCount(2).And.EndWith("Email");
    }

    [Fact]
    public void INDEI_ClearAll_FiresEntityLevelErrorsChanged()
    {
        var vm = new ValidationWiredVm();
        vm.WireValidationManually();
        var indei = (INotifyDataErrorInfo)vm;
        vm.Validation.Set("A", "msgA");
        vm.Validation.Set("B", "msgB");

        string? observed = "<not-set>";
        indei.ErrorsChanged += (_, e) => observed = e.PropertyName;

        vm.Validation.ClearAll();

        // OnValidationChanged maps "" → null so WPF treats it as entity-level reset.
        observed.Should().BeNull("ClearAll fires with empty string which is mapped to null by the bridge");
        indei.HasErrors.Should().BeFalse();
    }

    private partial class ValidationWiredVm : MvvmStateObject
    {
        public void WireValidationManually() => EnsureValidationWired();
    }
}
