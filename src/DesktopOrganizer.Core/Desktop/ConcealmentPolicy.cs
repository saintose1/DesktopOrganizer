namespace DesktopOrganizer.Core.Desktop;

/// <summary>
/// When items of collapsed groups carry the Hidden attribute.
/// <para>
/// Hidden items cannot be reflowed by a native Explorer drop (no flash), but Explorer needs about 0.6 s to show
/// them again, so an expand waits for them. Two modes:
/// </para>
/// <list type="bullet">
/// <item><b>Instant expand</b> (default): idle items stay parked offscreen and visible to Explorer, so an expand
/// animates them at once. They are hidden only for an Explorer drag of desktop items (the drop is what reflows
/// them) and return to parking after the desktop has been quiet for <see cref="QuietBeforeReturn"/>.</item>
/// <item><b>Always hidden</b>: the previous behaviour; hidden while idle, a hover near the group prepares them.</item>
/// </list>
/// </summary>
public static class ConcealmentPolicy
{
    /// <summary>A follow-up drag usually starts within this time; returning earlier would only churn Explorer.</summary>
    public static readonly TimeSpan QuietBeforeReturn = TimeSpan.FromSeconds(1.5);

    /// <summary>A press may start a drag. In "always hidden" mode groups prepared by a hover leave Explorer now.</summary>
    public static bool HideOnPress(bool concealWhenIdle) => concealWhenIdle;

    /// <summary>
    /// A drag of desktop items started (not a background marquee). In instant mode this is the moment to hide:
    /// a typical drag lasts 0.6 s or more and Explorer drops hidden items within 0.2–0.55 s. A click or a
    /// double-click never reaches this point, so launching items does not make Explorer re-enumerate.
    /// </summary>
    public static bool HideOnDragStart(bool concealWhenIdle) => !concealWhenIdle;

    /// <summary>Idle settling hides parked items only in "always hidden" mode.</summary>
    public static bool HideWhenIdle(bool concealWhenIdle) => concealWhenIdle;

    /// <summary>In instant mode hidden items of collapsed groups go back to parking once the desktop is quiet.</summary>
    public static bool ReturnToParking(bool concealWhenIdle, bool explorerBusy, TimeSpan sinceLastGesture) =>
        !concealWhenIdle && !explorerBusy && sinceLastGesture >= QuietBeforeReturn;
}
