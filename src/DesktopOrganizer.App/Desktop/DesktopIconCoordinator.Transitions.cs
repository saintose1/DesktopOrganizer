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

/// <summary>Collapse/expand presentation of real Explorer items on the shared animation clock.</summary>
public sealed partial class DesktopIconCoordinator
{
    public async Task<AnimatedTransition?> BeginAnimatedTransitionAsync(Fence target, bool startIsCollapsed)
    {
        if (target.Icons.IsEmpty) return null;
        if (!_initialized || !_positioningAvailable || _icons is not IDesktopIconAnimationPositioner positioner)
            throw new InvalidOperationException("Explorer icon animation is not ready.");
        // An idle inventory read in flight would delay this transition's own snapshot on the Shell worker.
        _observationRead?.Cancel();
        await WaitForPresentationAsync();
        if (startIsCollapsed && !target.IsCollapsed)
            await RevealGroupAsync(target); // Hidden items must rejoin parking before the shared clock starts.
        // Refresh actual positions and glyph bounds for this Fence before the shared clock starts.
        var ids = target.Icons.Select(i => i.StableId).ToHashSet(FenceIconLayout.IdentityComparer);
        var snapshot = await positioner.ReadAnimationItemsAsync(ids, _shutdown.Token);
        if (!snapshot.Connected || !snapshot.CanPosition || snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error ?? "Explorer is unavailable.");
        foreach (var id in ids) _matched.Remove(id);
        foreach (var icon in snapshot.Icons) _matched[icon.StableId] = icon;
        if (target.Icons.Any(i => _matched.ContainsKey(i.StableId) && !_recovery.Any(r => FenceIconLayout.IdentityComparer.Equals(r.StableId, i.StableId))))
            throw new InvalidOperationException("Recovery must be armed before animating desktop icons.");
        var proposed = _fences.Select(f => f.Id == target.Id ? target : f).ToArray();
        var final = IconPlacement.Targets(proposed, _monitors, Metrics, target.Id).Where(t => _matched.ContainsKey(t.StableId)).ToArray();
        var parked = proposed.Select(f => f.Id == target.Id && !f.IsCollapsed ? f.ToggleCollapsed() : f).ToArray();
        var plan = new FenceIconAnimationPlan(target, snapshot.Icons, final, _monitors, startIsCollapsed,
            IconPlacement.Targets(parked, _monitors, Metrics, target.Id), FenceInteractionGeometry.HeaderHeight * _host.GetScale(target));
        if (plan.Count == 0) return null;
        var shell = await positioner.BeginAnimationAsync(plan.InitialTargets, plan.FinalTargets, _shutdown.Token);
        return new AnimatedTransition(this, plan, shell);
    }
    public sealed class AnimatedTransition : IAsyncDisposable
    {
        private readonly DesktopIconCoordinator _owner;
        private readonly FenceIconAnimationPlan _plan;
        private readonly IDesktopIconAnimationSession _shell;
        private readonly DesktopIconTarget[] _group, _final;
        private readonly List<DesktopIconTarget> _changed;
        private bool _disposed, _parkingFinished;
        public int BulkUpdates { get; private set; }
        public int Redraws { get; private set; }
        public double PositionMilliseconds { get; private set; }
        public double ShellMilliseconds { get; private set; }
        public double RedrawMilliseconds { get; private set; }
        public double ShellFinishMilliseconds { get; private set; }
        public double IconCommitProgress { get; private set; }
        internal AnimatedTransition(DesktopIconCoordinator owner, FenceIconAnimationPlan plan, IDesktopIconAnimationSession shell)
        {
            _owner = owner; _plan = plan; _shell = shell;
            _group = new DesktopIconTarget[plan.Count]; _final = new DesktopIconTarget[plan.FinalTargets.Count];
            plan.Fill(1, true, _final);
            _changed = new(plan.FinalTargets.Count);
        }
        public async Task PresentAsync(FenceAnimationFrame frame, DesktopBounds content, Action present)
        {
            ObjectDisposedException.ThrowIf(_disposed || _owner._disposed, this);
            if (_parkingFinished) { present(); return; }
            _plan.Fill(frame.EasedProgress, false, _group, content);
            var finishParking = _plan.CanFinishParking(_group);
            var completes = frame.Progress >= 1 || finishParking;
            // Commit this group's parking slots and finish Shell redraw as soon as the last
            // visible glyph leaves. The remaining collapse frames only resize Fence.
            var targets = completes ? _final : _group;
            // Only items that moved this frame go to Explorer; the final frame re-sends the whole set.
            var changed = AnimationFrameBatch.Select(targets,
                id => _owner._matched.TryGetValue(id, out var icon) ? icon.Position : null, completes, _changed);
            if (!changed && !completes) { present(); return; }
            if (changed) _owner._host.SetIconTargets(_changed);
            var started = Stopwatch.GetTimestamp();
            try
            {
                var shellStarted = Stopwatch.GetTimestamp();
                if (changed) { await _shell.PositionAsync(_changed, _owner._shutdown.Token); BulkUpdates++; }
                if (_owner._disposed) throw new OperationCanceledException("Desktop icons are shutting down.");
                foreach (var target in _changed)
                {
                    if (!_owner._matched.TryGetValue(target.StableId, out var old)) continue;
                    _owner._matched[target.StableId] = old with { Position = target.Position, Bounds = old.Bounds with
                    { X = old.Bounds.X + target.Position.X - old.Position.X, Y = old.Bounds.Y + target.Position.Y - old.Position.Y } };
                }
                present();
                // Redraw was never disabled. Re-enabling it on every frame makes Explorer
                // repaint twice (Shell completion + our damage). Finish the Shell session
                // once on the final visible icon frame, before the collapsed header
                // finishes shrinking; intermediate frames have only the targeted paint.
                if (completes)
                {
                    var finishStarted = Stopwatch.GetTimestamp();
                    await _shell.CompleteFrameAsync(_owner._shutdown.Token);
                    ShellFinishMilliseconds = Stopwatch.GetElapsedTime(finishStarted).TotalMilliseconds;
                    IconCommitProgress = frame.Progress;
                    _parkingFinished = finishParking;
                }
                ShellMilliseconds += Stopwatch.GetElapsedTime(shellStarted).TotalMilliseconds;
                var redrawStarted = Stopwatch.GetTimestamp();
                // Only actual old/new glyph damage. All items are already parked before
                // collapse reaches its final frame; no region restoration exposes old pixels.
                var repaints = _owner._host.RepaintCount;
                _owner._host.FlushDamage();
                Redraws += (int)(_owner._host.RepaintCount - repaints);
                RedrawMilliseconds += Stopwatch.GetElapsedTime(redrawStarted).TotalMilliseconds;
                _owner.LastPresentationAt = Stopwatch.GetTimestamp();
                _owner.FramePresented?.Invoke(started, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            finally { PositionMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return; _disposed = true;
            await _shell.DisposeAsync();
            if (!_owner._disposed) _owner.RequestObservation(); // settles the Hidden attribute layer after collapse
        }
    }
}
