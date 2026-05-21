using YFex.State.Collections;

namespace YFex.State.Mvvm.Collections;

public static class StateListExtensions
{
    /// <summary>
    /// Returns an <see cref="StateCollection{T}"/> that wraps <paramref name="list"/>
    /// and exposes <c>INotifyCollectionChanged</c> + <c>IList</c> for XAML binding engines.
    /// Dispose the view when the binding is no longer needed to stop listening.
    /// </summary>
    public static StateCollection<T> ToStateCollection<T>(this StateList<T> list)
        => new(list);
}
