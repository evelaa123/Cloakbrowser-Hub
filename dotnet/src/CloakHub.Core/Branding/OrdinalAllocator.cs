namespace CloakHub.Core.Branding;

/// <summary>
/// Assigns the 1, 2, 3… numbers shown on launched browser icons.
/// <para>
/// The interesting question is what happens after a session ends. Two designs
/// are possible and they behave very differently:
/// </para>
/// <list type="bullet">
///   <item><b>Monotonic counter</b> — never reuses a number, so the fifth launch
///   of the day is "5" even if nothing else is running. Simple, but the badge
///   stops answering the question a user actually asks ("how many do I have
///   open, and which is which"), and after a long session the numbers grow
///   unbounded and overflow the badge.</item>
///   <item><b>Lowest free slot</b> — a closed session releases its number for
///   the next launch. The set of visible badges is therefore always
///   <c>1..n</c> for <c>n</c> running sessions, which is what the numbers are
///   for.</item>
/// </list>
/// <para>
/// This implements the second. The trade-off is honest and worth stating: a
/// number is <i>not</i> a stable identifier for a profile across restarts —
/// close "2" and the next launch becomes "2" even if it is a different profile.
/// Anyone needing a stable mark should use the profile colour or name, which is
/// why both remain in the UI.
/// </para>
/// <para>
/// Not thread-safe by itself; callers hold the session-manager lock.
/// </para>
/// </summary>
public sealed class OrdinalAllocator
{
    private readonly SortedSet<int> _inUse = [];

    /// <summary>Ordinals currently held, ascending.</summary>
    public IReadOnlyCollection<int> InUse => _inUse;

    /// <summary>
    /// Take the lowest free ordinal, starting at 1.
    /// </summary>
    public int Acquire()
    {
        var candidate = 1;
        foreach (var taken in _inUse)
        {
            if (taken > candidate) break;   // found a gap
            if (taken == candidate) candidate++;
        }
        _inUse.Add(candidate);
        return candidate;
    }

    /// <summary>
    /// Return an ordinal to the pool. Releasing one that was never taken is a
    /// no-op rather than an error: session teardown runs on several paths
    /// (explicit stop, crash, app exit) and must stay idempotent, or a double
    /// release would throw during cleanup and mask the real failure.
    /// </summary>
    public void Release(int ordinal) => _inUse.Remove(ordinal);

    /// <summary>Drop all ordinals — used when every session has been stopped.</summary>
    public void Clear() => _inUse.Clear();
}
