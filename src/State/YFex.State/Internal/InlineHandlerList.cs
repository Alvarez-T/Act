using System.Buffers;
using System.Runtime.CompilerServices;
using YFex.State.Notification;

namespace YFex.State.Internal;

/// <summary>
/// Inline-buffered list of <see cref="IChangedHandler"/> instances.
/// Two slots are stored without any heap allocation (the typical case: one adapter + one parent observer).
/// Overflow beyond two uses ArrayPool to avoid per-add allocation.
/// </summary>
[System.Runtime.CompilerServices.InlineArray(2)]
internal struct HandlerSlot
{
#pragma warning disable IDE0044 // InlineArray requires exactly one private field
    private IChangedHandler? _e0;
#pragma warning restore IDE0044
}

internal struct InlineHandlerList
{
    private HandlerSlot _inline;
    private byte _inlineCount;        // 0, 1, or 2
    private IChangedHandler[]? _overflow;
    private int _overflowCount;

    public void Add(IChangedHandler handler)
    {
        if (_inlineCount < 2)
        {
            _inline[_inlineCount] = handler;
            _inlineCount++;
            return;
        }

        if (_overflow is null)
        {
            _overflow = ArrayPool<IChangedHandler>.Shared.Rent(4);
            _overflowCount = 0;
        }
        else if (_overflowCount == _overflow.Length)
        {
            var grown = ArrayPool<IChangedHandler>.Shared.Rent(_overflow.Length * 2);
            _overflow.AsSpan(0, _overflowCount).CopyTo(grown);
            ArrayPool<IChangedHandler>.Shared.Return(_overflow, clearArray: true);
            _overflow = grown;
        }

        _overflow[_overflowCount++] = handler;
    }

    public void Remove(IChangedHandler handler)
    {
        for (int i = 0; i < _inlineCount; i++)
        {
            if (ReferenceEquals(_inline[i], handler))
            {
                // Shift remaining inline slots left
                for (int j = i; j < _inlineCount - 1; j++)
                    _inline[j] = _inline[j + 1]!;
                _inline[--_inlineCount] = null;
                return;
            }
        }

        if (_overflow is null) return;
        for (int i = 0; i < _overflowCount; i++)
        {
            if (ReferenceEquals(_overflow[i], handler))
            {
                _overflow.AsSpan(i + 1, _overflowCount - i - 1).CopyTo(_overflow.AsSpan(i));
                _overflow[--_overflowCount] = null!;
                return;
            }
        }
    }

    /// <summary>
    /// Dispatches to all registered handlers. Enumerator is inlined to avoid virtual dispatch overhead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void NotifyAll(object source, in ChangedNotification notification)
    {
        for (int i = 0; i < _inlineCount; i++)
            _inline[i]!.OnChanged(source, in notification);

        if (_overflow is null) return;
        for (int i = 0; i < _overflowCount; i++)
            _overflow[i].OnChanged(source, in notification);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void NotifyChangingAll(object source, in ChangedNotification notification)
    {
        for (int i = 0; i < _inlineCount; i++)
            _inline[i]!.OnChanging(source, in notification);

        if (_overflow is null) return;
        for (int i = 0; i < _overflowCount; i++)
            _overflow[i].OnChanging(source, in notification);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void NotifyBatchFlushStarting(object source)
    {
        for (int i = 0; i < _inlineCount; i++)
            _inline[i]!.OnBatchFlushStarting(source);

        if (_overflow is null) return;
        for (int i = 0; i < _overflowCount; i++)
            _overflow[i].OnBatchFlushStarting(source);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void NotifyBatchFlushCompleted(object source)
    {
        for (int i = 0; i < _inlineCount; i++)
            _inline[i]!.OnBatchFlushCompleted(source);

        if (_overflow is null) return;
        for (int i = 0; i < _overflowCount; i++)
            _overflow[i].OnBatchFlushCompleted(source);
    }
}
