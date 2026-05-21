namespace YFex.State;

/// <summary>
/// Lifecycle contract for view-models and state objects that can be paused when off-screen.
/// Implementations must be safe to call from the owner thread only.
/// </summary>
public interface IActivatable
{
    /// <summary>Transitions from inactive to active. Idempotent — calling on an already-active object is a no-op.</summary>
    void Activate();

    /// <summary>Transitions from active to inactive. Idempotent — calling on an already-inactive object is a no-op.</summary>
    void Deactivate();

    /// <summary>True when the object has been activated and not yet deactivated.</summary>
    bool IsActive { get; }
}
