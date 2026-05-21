using System.Threading;
using System.Threading.Tasks;

namespace YFex.State.Commands;

/// <summary>
/// Work item enqueued into a <see cref="CommandQueue"/>.
/// Implements both interfaces so it can be:
///   - type-safely enqueued into the bounded Channel
///   - dispatched to the ThreadPool via UnsafeQueueUserWorkItem without a wrapper allocation
/// </summary>
internal interface ICommandWorkItem : System.Threading.IThreadPoolWorkItem
{
    ValueTask ExecuteAsync(CancellationToken ct);
}
