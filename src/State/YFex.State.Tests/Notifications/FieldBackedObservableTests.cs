using YFex.State.Testing;
using YFex.State.Tests.Helpers;
using YFex.Validation;

namespace YFex.State.Tests.Notifications;

/// <summary>
/// [Observable] on a private field: the generator emits a regular (non-partial) property named after the
/// field (<c>_title</c> → <c>Title</c>) that reads and writes the user's field directly.
/// </summary>
public class FieldBackedObservableTests
{
    [Fact]
    public void Getter_ReturnsFieldInitializer()
    {
        var vm = new FieldBackedVm();

        vm.Title.Should().Be("t");
        vm.Count.Should().Be(3);
    }

    [Fact]
    public void Setter_StoresValueInUserField_AndNotifies()
    {
        var vm = new FieldBackedVm();
        var handler = new CountingHandler();
        vm.Subscribe(handler);

        vm.Title = "x";

        vm.Title.Should().Be("x");
        vm.RawTitle.Should().Be("x");
        handler.OnChangingCount.Should().Be(1);
        handler.OnChangedCount.Should().Be(1);
    }

    [Fact]
    public void Setter_SameValue_DoesNotNotify()
    {
        var vm = new FieldBackedVm();
        var handler = new CountingHandler();
        vm.Subscribe(handler);

        vm.Title = "t";

        handler.OnChangedCount.Should().Be(0);
    }

    [Fact]
    public void Setter_InvokesPartialChangeHooks()
    {
        var vm = new FieldBackedVm();

        vm.Title = "x";

        vm.Log.Should().Equal("changing t→x", "changed t→x");
    }

    [Fact]
    public async Task TaskNotifierField_NotifiesOnAssignmentAndCompletion()
    {
        var vm = new FieldBackedVm();
        using var recorder = new StateRecorder<FieldBackedVm>(vm);
        var tcs = new TaskCompletionSource<int>();

        vm.Load = tcs.Task;
        recorder.AssertChanged(nameof(FieldBackedVm.Load));
        int afterAssign = recorder.Events.Count;

        tcs.SetResult(42);
        await Task.Delay(50);

        recorder.Events.Should().HaveCount(afterAssign + 1);
        (await vm.Load!).Should().Be(42);
    }

    [Fact]
    public void ValidationRule_OnField_RunsThroughSetterHook()
    {
        var vm = new FieldBackedValidatedVm();

        vm.Title = "";

        vm.Validation.Get("Title").Contains(RuleCodes.Required).Should().BeTrue();

        vm.Title = "ok";

        vm.Validation.Get("Title").IsEmpty.Should().BeTrue();
    }
}

public partial class FieldBackedVm : StateObject
{
    [Observable] private string _title = "t";
    [Observable] private readonly int _count = 3;
    /// <summary>The pending load.</summary>
    [Observable, NotifyOnTaskCompletion] private Task<int>? _load;

    public List<string> Log { get; } = new();
    public string RawTitle => _title;

    partial void OnTitleChanging(string oldValue, string newValue) => Log.Add($"changing {oldValue}→{newValue}");
    partial void OnTitleChanged(string oldValue, string newValue) => Log.Add($"changed {oldValue}→{newValue}");
}

public partial class FieldBackedValidatedVm : StateObject
{
    [Observable, Required] private string _title = "t";
}
