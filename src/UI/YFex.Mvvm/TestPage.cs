using YFex.NavigatR;
using YFex.State;

namespace YFex.Mvvm;

// Development test page — demonstrates [Observable] on a PageViewModel subclass.
// Uses the parameterless constructor (no DI) for simplicity.
public partial class TestPage : PageViewModel
{
    [Observable] public partial string Testes { get; set; }

    public override Task OnNavigation(NavigationContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}
