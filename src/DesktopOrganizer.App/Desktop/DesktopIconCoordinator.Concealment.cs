using System.Windows.Threading;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.App.Desktop;

/// <summary>Hidden-attribute layer for collapsed groups.</summary>
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
        if (_concealment is null || Busy() || _monitors.Count == 0 || !Enabled) return;
        var concealed = ConcealedIds();
        var collapsed = _fences.Where(f => f.IsCollapsed).SelectMany(f => f.Icons).Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
        var revision = _revision;
        var reveal = concealed.Where(id => !collapsed.Contains(id)).Select(id => (id, RevealPosition(id))).ToArray();
        if (reveal.Length != 0)
        {
            await _concealment.RevealAsync(reveal);
            var snapshot = await _icons.ReadAsync(_shutdown.Token);
            if (snapshot.Connected && snapshot.Error is null) ApplySnapshot(snapshot);
            _logger.LogInformation("Returned {Count} hidden items that no longer belong to a collapsed group", reveal.Length);
        }
        if (_revision != revision) { RequestObservation(); return; }
        if (!Busy()) await ConcealParkedNowAsync();
    }
    private Guid? _parkedForRepeatToggle;
    public void KeepParkedForRepeatToggle(Guid id)
    {
        if (!_concealWhenIdle) _parkedForRepeatToggle = id;
    }
    public void UpdateConcealmentHeader(Guid? underPointer)
    {
        if (_parkedForRepeatToggle is null || _parkedForRepeatToggle == underPointer) return;
        _parkedForRepeatToggle = null;
        RequestObservation(DispatcherPriority.Input);
    }
    private async Task ConcealParkedNowAsync()
    {
        if (_concealment is null || _monitors.Count == 0 || InteractionActive || !Enabled || _disposed || _stopping) return;
        try
        {
            var concealed = ConcealedIds();
            var parked = _fences.Where(f => f.IsCollapsed && f.Id != _parkedForRepeatToggle).SelectMany(f => f.Icons).Select(i => i.StableId)
                .Where(id => !concealed.Contains(id) && _matched.TryGetValue(id, out var icon) && !OnMonitors(icon)).ToArray();
            if (parked.Length != 0) await _concealment.ConcealAsync(parked, _shutdown.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _logger.LogWarning(error, "Could not hide parked items"); }
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
