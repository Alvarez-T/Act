using YFex.State;
using YFex.State.Collections;
using YFex.State.History;

// Top-level test ViewModels — must NOT be nested inside test classes because
// Roslyn source generators do not support nested types in non-partial containers.

namespace YFex.State.History.Tests;

// Mode A: property-level isolation — each property gets its own UndoContext.
public partial class PersonVm : StateObject
{
    [YFex.State.Observable, YFex.State.History.Undoable]
    public partial string Name { get; set; }

    [YFex.State.Observable, YFex.State.History.Undoable]
    public partial int Age { get; set; }
}

// Mode B: named shared scope — both properties share one UndoContext.
// HasSinglePrimaryScope=true → generator also emits IUndoable.
public partial class ContactVm : StateObject
{
    [YFex.State.Observable, YFex.State.History.Undoable(Scope = "Contact")]
    public partial string FirstName { get; set; }

    [YFex.State.Observable, YFex.State.History.Undoable(Scope = "Contact")]
    public partial string LastName { get; set; }
}

// Mode C: explicit injected UndoContext.
public partial class DocumentVm : StateObject
{
    [YFex.State.History.UndoContext]
    public UndoContext History { get; } = new();

    [YFex.State.Observable, YFex.State.History.Undoable(Context = nameof(History))]
    public partial string Title { get; set; }

    [YFex.State.Observable, YFex.State.History.Undoable(Context = nameof(History))]
    public partial string Body { get; set; }
}

// Collection tests ViewModel.
public partial class ListVm : StateObject
{
    [YFex.State.Observable]
    public partial StateList<string> Items { get; set; }
}
