using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using YFex.State.Notification;
using YFex.State.Timing;

namespace YFex.State.Blazor;

/// <summary>
/// Blazor component base that subscribes to a <typeparamref name="TViewModel"/> and
/// coalesces <see cref="Microsoft.AspNetCore.Components.ComponentBase.StateHasChanged"/> calls
/// via an 8 ms debounce to prevent render thrash when multiple properties change at once.
/// </summary>
public abstract class StateComponent<TViewModel>
    : ComponentBase, IDisposable, IChangedHandler
    where TViewModel : INotifyChanged
{
    [Inject]
    public required TViewModel ViewModel { get; set; }

    private DebounceState _renderDebounce;

    protected override void OnInitialized()
    {
        ViewModel.Subscribe(this);
        base.OnInitialized();
    }

    public void OnChanged(object source, in ChangedNotification n)
    {
        _ = InvokeStateHasChangedAsync(_renderDebounce.NextToken());
    }

    private async Task InvokeStateHasChangedAsync(CancellationToken ct)
    {
        try
        {
            // 8 ms coalescing window — multiple rapid property changes fire a single render
            await Task.Delay(8, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        ViewModel.Unsubscribe(this);
        _renderDebounce.Dispose();
    }
}
