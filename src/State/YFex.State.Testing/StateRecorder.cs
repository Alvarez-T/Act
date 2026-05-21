using System;
using System.Collections.Generic;
using YFex.State.Notification;

namespace YFex.State.Testing;

/// <summary>
/// Records all <see cref="ChangedNotification"/> events fired by a <typeparamref name="TVm"/>
/// instance during a test. Attach via <c>vm.Subscribe(recorder)</c>.
/// </summary>
public sealed class StateRecorder<TVm> : IChangedHandler, IDisposable
    where TVm : INotifyChanged
{
    private readonly TVm _vm;
    private readonly List<ChangedNotification> _events = new();
    private readonly List<ChangedNotification> _changingEvents = new();

    public StateRecorder(TVm vm)
    {
        _vm = vm;
        _vm.Subscribe(this);
    }

    public IReadOnlyList<ChangedNotification> Events => _events;

    /// <summary>Pre-change events captured via <see cref="IChangedHandler.OnChanging"/>.</summary>
    public IReadOnlyList<ChangedNotification> ChangingEvents => _changingEvents;

    public void OnChanged(object source, in ChangedNotification n) => _events.Add(n);

    void IChangedHandler.OnChanging(object source, in ChangedNotification n) => _changingEvents.Add(n);

    public void Clear()
    {
        _events.Clear();
        _changingEvents.Clear();
    }

    // ── Post-change assertions ─────────────────────────────────────────────────

    public void AssertChanged(string propertyName)
    {
        foreach (var e in _events)
            if (e.PropertyName == propertyName) return;
        throw new InvalidOperationException(
            $"Expected property '{propertyName}' to have changed, but it did not. " +
            $"Recorded changes: [{string.Join(", ", _events.ConvertAll(e => e.PropertyName))}]");
    }

    public void AssertNeverChanged(string propertyName)
    {
        foreach (var e in _events)
            if (e.PropertyName == propertyName)
                throw new InvalidOperationException(
                    $"Expected property '{propertyName}' to NOT have changed, but it did.");
    }

    /// <summary>
    /// Asserts that the specified properties changed, in the given order (as a subsequence).
    /// </summary>
    public void AssertChangedInOrder(params ReadOnlySpan<string> names)
    {
        int matchIndex = 0;
        foreach (var e in _events)
        {
            if (matchIndex < names.Length && e.PropertyName == names[matchIndex])
                matchIndex++;
        }
        if (matchIndex < names.Length)
            throw new InvalidOperationException(
                $"Expected properties to change in order [{string.Join(", ", names.ToArray())}] " +
                $"but stopped matching at '{names[matchIndex]}'. " +
                $"Recorded: [{string.Join(", ", _events.ConvertAll(e => e.PropertyName))}]");
    }

    public void AssertChangeCount(string propertyName, int expected)
    {
        int actual = 0;
        foreach (var e in _events)
            if (e.PropertyName == propertyName) actual++;
        if (actual != expected)
            throw new InvalidOperationException(
                $"Expected '{propertyName}' to change {expected} time(s), but it changed {actual} time(s).");
    }

    // ── Pre-change assertions ──────────────────────────────────────────────────

    public void AssertChanging(string propertyName)
    {
        foreach (var e in _changingEvents)
            if (e.PropertyName == propertyName) return;
        throw new InvalidOperationException(
            $"Expected PropertyChanging for '{propertyName}', but it did not fire. " +
            $"Recorded changing: [{string.Join(", ", _changingEvents.ConvertAll(e => e.PropertyName))}]");
    }

    public void AssertNeverChanging(string propertyName)
    {
        foreach (var e in _changingEvents)
            if (e.PropertyName == propertyName)
                throw new InvalidOperationException(
                    $"Expected PropertyChanging for '{propertyName}' to NOT fire, but it did.");
    }

    /// <summary>
    /// Asserts that <see cref="OnChanging"/> fired before <see cref="OnChanged"/> for
    /// <paramref name="propertyName"/> in the recorded sequence.
    /// </summary>
    public void AssertChangingThenChanged(string propertyName)
    {
        AssertChanging(propertyName);
        AssertChanged(propertyName);
    }

    public void AssertChangingCount(string propertyName, int expected)
    {
        int actual = 0;
        foreach (var e in _changingEvents)
            if (e.PropertyName == propertyName) actual++;
        if (actual != expected)
            throw new InvalidOperationException(
                $"Expected PropertyChanging for '{propertyName}' to fire {expected} time(s), but it fired {actual} time(s).");
    }

    public void Dispose() => _vm.Unsubscribe(this);
}
