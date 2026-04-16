namespace YFex.NavigatR;

/// <summary>
/// Controls what happens to the forward stack when the user navigates
/// to a new page while positioned mid-history (i.e. after going back).
/// </summary>
public enum NavigationHistoryPolicy
{
    /// <summary>
    /// Default. Forward entries are discarded when a new navigation branches off.
    /// History stays linear — same model as a web browser.
    ///
    /// <para>Use for: wizards, checkout flows, any flow where forward entries
    /// would be stale or invalidated by the new navigation.</para>
    ///
    /// Example:
    ///   <code>Cart → Address → Payment → Review
    ///                       ↑ go back, fix address, navigate forward
    ///   → Review is pruned — it was built on the old address</code>
    /// </summary>
    PruneForwardOnBranch,

    /// <summary>
    /// Forward entries are preserved when a new navigation branches off.
    /// The new entry is inserted immediately after the cursor; existing
    /// forward entries shift right and remain navigable.
    ///
    /// <para>Use for: document editors, research tools, workspaces where forward
    /// entries hold independent state that is still valid after branching.</para>
    ///
    /// Example:
    ///   <code>DocList → DocA (unsaved) → DocB (unsaved)
    ///                 ↑ go back, navigate to Settings
    ///   → DocB still in forward stack, resume with edits intact
    ///
    ///   History becomes: DocList → DocA → Settings  →  DocB
    ///                                        ↑ cursor     (still reachable forward)</code>
    /// </summary>
    PreserveForwardOnBranch
}
