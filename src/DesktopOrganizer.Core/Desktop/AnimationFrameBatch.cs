namespace DesktopOrganizer.Core.Desktop;

/// <summary>
/// Which items one animation frame sends to Explorer. Each Shell positioning call costs about 3 ms plus ~0.17 ms per
/// item, and a frame waits for it; items that are already where Explorer has them (still parked, or settled) are
/// skipped, so frames with few moving items reach the screen sooner. The final frame always sends the complete set,
/// which re-asserts the exact positions of the transitioning group.
/// </summary>
public static class AnimationFrameBatch
{
    /// <param name="targets">Positions of this frame.</param>
    /// <param name="known">Where Explorer currently has an item, or null if unknown (then it is always sent).</param>
    /// <param name="final">Final frame of the transition.</param>
    /// <param name="send">Receives the items to position this frame.</param>
    /// <returns>True when at least one item changed (or the frame is final).</returns>
    public static bool Select(IReadOnlyList<DesktopIconTarget> targets, Func<string, DesktopPoint?> known, bool final,
        List<DesktopIconTarget> send)
    {
        ArgumentNullException.ThrowIfNull(targets); ArgumentNullException.ThrowIfNull(known); ArgumentNullException.ThrowIfNull(send);
        send.Clear();
        var changed = false;
        foreach (var target in targets)
        {
            if (known(target.StableId) == target.Position) continue;
            changed = true;
            if (!final) send.Add(target);
        }
        if (final) send.AddRange(targets);
        return changed;
    }
}
