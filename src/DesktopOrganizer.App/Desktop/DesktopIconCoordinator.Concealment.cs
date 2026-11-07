using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Core.Storage;
using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.Desktop;

/// <summary>Hidden-attribute layer for collapsed groups and hover preparation.</summary>
public sealed partial class DesktopIconCoordinator
{
    private HashSet<string> ConcealedIds() => (_concealment?.Concealed ?? []).ToHashSet(FenceIconLayout.IdentityComparer);
    private bool OnMonitors(DesktopIcon icon) => _monitors.Any(m => icon.Bounds.X < m.X + m.Width && icon.Bounds.X + icon.Bounds.Width > m.X &&
        icon.Bounds.Y < m.Y + m.Height && icon.Bounds.Y + icon.Bounds.Height > m.Y);
    /// <summary>
    /// Settled parked items of collapsed groups get the Hidden attribute (nothing left for Explorer to reflow on a
    /// drop); items this application hid that no longer belong to a collapsed group are returned to their place.
    /// </summary>
    private async Task SettleConcealmentAsync()
    {
        if (_concealment is null || Busy() || _monitors.Count == 0) return;
        var concealed = ConcealedIds();
        var collapsed = _fences.Where(f => f.IsCollapsed).SelectMany(f => f.Icons).Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
        // A group the pointer is approaching stays in Explorer (parked) so its hover expand can animate it.
        var approached = _fences.Where(f => f.IsCollapsed && RecentlyApproached(f.Id)).SelectMany(f => f.Icons)
            .Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
        var reveal = concealed.Where(id => !collapsed.Contains(id)).Select(id => (id, RevealPosition(id))).ToArray();
        if (reveal.Length != 0)
        {
            await _concealment.RevealAsync(reveal);
            var snapshot = await _icons.ReadAsync(_shutdown.Token);
            if (snapshot.Connected && snapshot.Error is null) ApplySnapshot(snapshot);
            _logger.LogInformation("Returned {Count} hidden items that no longer belong to a collapsed group", reveal.Length);
        }
        if (Busy()) return;
        // While the pointer works with the Fence row, hiding makes Explorer re-enumerate for about a second and
        // the next expand/collapse frames would wait for it. Hide once the pointer has left all groups.
        if (_approachedAt.Values.Any(at => Stopwatch.GetElapsedTime(at) < ApproachLease)) return;
        var conceal = collapsed.Where(id => !concealed.Contains(id) && !approached.Contains(id) &&
            _matched.TryGetValue(id, out var icon) && !OnMonitors(icon)).ToArray();
        if (conceal.Length != 0) await _concealment.ConcealAsync(conceal, _shutdown.Token);
    }
    private async Task ConcealParkedNowAsync()
    {
        if (_concealment is null || _monitors.Count == 0 || InteractionActive) return;
        try
        {
            var concealed = ConcealedIds();
            var parked = _fences.Where(f => f.IsCollapsed).SelectMany(f => f.Icons).Select(i => i.StableId)
                .Where(id => !concealed.Contains(id) && _matched.TryGetValue(id, out var icon) && !OnMonitors(icon)).ToArray();
            if (parked.Length != 0) await _concealment.ConcealAsync(parked, _shutdown.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _logger.LogWarning(error, "Could not hide parked items at gesture start"); }
    }
    private DesktopPoint? RevealPosition(string id)
    {
        foreach (var fence in _fences)
            foreach (var icon in fence.Icons)
                if (FenceIconLayout.IdentityComparer.Equals(icon.StableId, id))
                    return fence.IsCollapsed ? null : IconPlacement.Normal(fence, icon);
        foreach (var target in _recovery)
            if (FenceIconLayout.IdentityComparer.Equals(target.StableId, id)) return target.Position;
        return null;
    }
    private async Task RevealInPlaceAsync(Fence expanded, IReadOnlyList<DesktopIconAssignment> items)
    {
        try
        {
            try { await _prewarm; } catch { } // an approach reveal already in flight finishes first
            var concealed = ConcealedIds();
            var hidden = items.Where(i => concealed.Contains(i.StableId)).ToArray();
            _host.Trace.Mark("Reveal.inPlace", hidden.Length);
            if (hidden.Length != 0)
                await _concealment!.RevealAsync(hidden.Select(i => (i.StableId, (DesktopPoint?)IconPlacement.Normal(expanded, i))).ToArray());
            // Items the approach reveal returned to parking join the Fence if it is still expanded; never in the
            // middle of another Fence transition (that one owns the positions and verifies them).
            var waited = Stopwatch.StartNew();
            while ((InteractionActive || _moving) && waited.Elapsed < TimeSpan.FromSeconds(1) && !_disposed) await Task.Delay(16);
            var current = _fences.FirstOrDefault(f => f.Id == expanded.Id);
            var parked = items.Where(i => !concealed.Contains(i.StableId)).Select(i => new DesktopIconTarget(i.StableId, IconPlacement.Normal(expanded, i))).ToArray();
            if (parked.Length != 0 && current is { IsCollapsed: false } && !InteractionActive && !_disposed) await _icons.PositionAsync(parked, _shutdown.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _logger.LogWarning(error, "Could not return hidden items into an expanding Fence"); }
        finally { if (!_disposed) RequestObservation(); }
    }
    private Task _prewarm = Task.CompletedTask;
    private Guid? _prewarmFence;
    private readonly Dictionary<Guid, long> _approachedAt = [];
    private static readonly TimeSpan ApproachLease = TimeSpan.FromSeconds(4); // no hide/reveal churn while the pointer works the Fence row
    private bool RecentlyApproached(Guid id) => _approachedAt.TryGetValue(id, out var at) && Stopwatch.GetElapsedTime(at) < ApproachLease;
    /// <summary>
    /// The pointer approaches a collapsed group: return its hidden items into Explorer (still parked offscreen)
    /// so a hover expand animates them with the Fence. Not during Explorer gestures, so a drop never meets them.
    /// </summary>
    public void PrewarmGroup(Fence fence)
    {
        if (_disposed || _stopping || _concealment is null || !_initialized || !_positioningAvailable || !Enabled ||
            InteractionActive || ExplorerBusy()) return;
        _approachedAt[fence.Id] = Stopwatch.GetTimestamp();
        if (!_approachExpiry.IsEnabled) _approachExpiry.Start();
        if (!_prewarm.IsCompleted) return;
        if (!ConcealedIds().Overlaps(fence.Icons.Select(i => i.StableId))) return;
        _prewarmFence = fence.Id;
        _prewarm = PrewarmAsync(fence);
    }
    private async Task PrewarmAsync(Fence fence)
    {
        try { _host.Trace.Mark("Reveal.approach", fence.Icons.Length); await RevealGroupAsync(fence); }
        catch (Exception error) { _logger.LogWarning(error, "Could not prepare a collapsed group for hover"); }
    }
    private void OnApproachExpiry(object? sender, EventArgs args)
    {
        // Once the pointer has left every approach zone, settle hides the prepared groups again.
        if (_approachedAt.Values.Any(at => Stopwatch.GetElapsedTime(at) < ApproachLease)) return;
        _approachExpiry.Stop(); _approachedAt.Clear(); RequestObservation();
    }
    /// <summary>Before an expand starts, hidden items return to their parking slots within one Shell redraw lease.</summary>
    private async Task RevealGroupAsync(Fence target)
    {
        if (_concealment is null) return;
        var concealed = ConcealedIds();
        var ids = target.Icons.Select(i => i.StableId).Where(concealed.Contains).ToHashSet(FenceIconLayout.IdentityComparer);
        if (ids.Count == 0) return;
        var parkedGroups = _fences.Select(f => f.Id == target.Id && !f.IsCollapsed ? f.ToggleCollapsed() : f).ToArray();
        var parking = IconPlacement.Targets(parkedGroups, _monitors, Metrics).Where(t => ids.Contains(t.StableId))
            .Select(t => (t.StableId, (DesktopPoint?)t.Position)).ToArray();
        _host.Trace.Mark("Reveal.group", parking.Length);
        await _concealment.RevealAsync(parking);
    }
}
