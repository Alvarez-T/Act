using YFex.State.History;

namespace YFex.State.History.Tests;

/// <summary>
/// Integration tests for generated [Observable, Undoable] property hooks.
/// Exercises real generated code from both YFex.State.Generator (Observable) and
/// YFex.State.History.Generator (Undoable). ViewModels are defined in TestViewModels.cs.
/// </summary>
public sealed class GeneratedPropertyTests
{
    // ── Mode A: property-level isolation ─────────────────────────────────────

    [Fact]
    public void ModeA_Change_ThenUndo_RestoresDefaultValue()
    {
        var vm = new PersonVm();
        vm.Name = "Alice";

        vm.NameUndoCommand.CanExecute().Should().BeTrue();
        vm.NameUndoCommand.Execute();

        vm.Name.Should().BeNullOrEmpty();
    }

    [Fact]
    public void ModeA_UndoThenRedo_ReappliesValue()
    {
        var vm = new PersonVm();
        vm.Name = "Bob";
        vm.NameUndoCommand.Execute();
        vm.NameRedoCommand.Execute();
        vm.Name.Should().Be("Bob");
    }

    [Fact]
    public void ModeA_TwoProperties_HaveIsolatedContexts()
    {
        var vm = new PersonVm();
        vm.Name = "Carol";
        vm.Age  = 30;

        vm.NameUndoCommand.Execute();
        vm.Name.Should().BeNullOrEmpty();
        vm.Age.Should().Be(30); // unaffected — separate context
    }

    [Fact]
    public void ModeA_AgeContext_IndependentOfNameContext()
    {
        var vm = new PersonVm();
        vm.Age = 25;
        vm.AgeUndoCommand.Execute();
        vm.Age.Should().Be(0);

        vm.NameRedoCommand.CanExecute().Should().BeFalse();
    }

    // ── Mode B: named shared scope ────────────────────────────────────────────

    [Fact]
    public void ModeB_UndoCommand_ExposedWithScopeName()
    {
        var vm = new ContactVm();
        vm.FirstName = "Jane";

        vm.ContactUndoCommand.Should().NotBeNull();
        vm.ContactUndoCommand.CanExecute().Should().BeTrue();
    }

    [Fact]
    public void ModeB_ImplementsIUndoable_ExposesSharedContext()
    {
        var vm       = new ContactVm();
        var undoable = (IUndoable)vm;

        undoable.UndoHistory.Should().NotBeNull();
        undoable.UndoCommand.Should().NotBeNull();
    }

    [Fact]
    public void ModeB_Transaction_GroupsBothProperties()
    {
        var vm  = new ContactVm();
        var ctx = ((IUndoable)vm).UndoHistory;

        using (ctx.BeginTransaction("Set full name"))
        {
            vm.FirstName = "John";
            vm.LastName  = "Doe";
        }

        ctx.ShouldHaveUndoDepth(1);
        ctx.UndoHistory[0].Label.Should().Be("Set full name");
    }

    [Fact]
    public void ModeB_Transaction_UndoReverts_BothProperties()
    {
        var vm  = new ContactVm();
        var ctx = ((IUndoable)vm).UndoHistory;

        using (ctx.BeginTransaction()) { vm.FirstName = "Alice"; vm.LastName = "Smith"; }

        vm.ContactUndoCommand.Execute();
        vm.FirstName.Should().BeNullOrEmpty();
        vm.LastName.Should().BeNullOrEmpty();
    }

    // ── Mode C: explicit injected context ────────────────────────────────────

    [Fact]
    public void ModeC_BothChanges_RecordedInSharedContext()
    {
        var vm = new DocumentVm();
        vm.Title = "Draft";
        vm.Body  = "Hello world";

        vm.History.UndoDepth.Should().Be(2);
    }

    [Fact]
    public void ModeC_Undo_RevertsMostRecentChange()
    {
        var vm = new DocumentVm();
        // Use explicit transactions to prevent the 500ms merge window from coalescing the two
        // rapid changes into a single "null→Second" undo entry.
        using (vm.History.BeginTransaction()) { vm.Title = "First"; }
        using (vm.History.BeginTransaction()) { vm.Title = "Second"; }

        vm.HistoryUndoCommand.Execute();
        vm.Title.Should().Be("First");
    }

    [Fact]
    public void ModeC_UndoCommands_ExposedWithContextPropertyName()
    {
        var vm = new DocumentVm();
        vm.HistoryUndoCommand.Should().NotBeNull();
        vm.HistoryRedoCommand.Should().NotBeNull();
    }

    // ── Redo stack cleared on new change after undo ───────────────────────────

    [Fact]
    public void NewChangeAfterUndo_ClearsRedoStack()
    {
        var vm = new PersonVm();
        vm.Name = "Alice";
        vm.NameUndoCommand.Execute();

        vm.NameRedoCommand.CanExecute().Should().BeTrue();

        vm.Name = "Charlie"; // new change
        vm.NameRedoCommand.CanExecute().Should().BeFalse();
    }

    // ── IsReplaying guard (no re-recording during undo) ───────────────────────

    [Fact]
    public void Undo_DoesNotCreateNewUndoEntry()
    {
        var vm = new PersonVm();
        vm.Name = "X";

        vm.NameUndoCommand.Execute();

        // Stack should now be empty — undo application did not record a new entry
        vm.NameUndoCommand.CanExecute().Should().BeFalse();
    }
}
