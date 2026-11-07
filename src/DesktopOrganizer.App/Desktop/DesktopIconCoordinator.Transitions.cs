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
        await WaitForPresentationAsync();
        if (startIsCollapsed && _concealment is not null)
        {
            var concealed = ConcealedIds();
            var hidden = target.Icons.Where(i => concealed.Contains(i.StableId)).ToArray();
            var approaching = !_prewarm.IsCompleted && _prewarmFence == target.Id;
            if (approaching || hidden.Length != 0 && hidden.Length == target.Icons.Length)
            {
                // Explorer needs ~0.6 s to re-add a hidden group. The Fence expands at once; the items
                // appear directly in their slots (inside one redraw lease) as soon as Explorer has them.
                _ = RevealInPlaceAsync(target, approaching ? target.Icons.ToArray() : hidden);
                return null;
            }
            if (hidden.Length != 0) await RevealGroupAsync(target); // mixed group: hidden items rejoin parking first
        }
        // One snapshot before the shared clock starts. Observation/polling is gated throughout the transition.
        var snapshot = await _icons.ReadAsync(_shutdown.Token);
        if (!snapshot.Connected || !snapshot.CanPosition || snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error ?? "Explorer is unavailable.");
        ApplySnapshot(snapshot);
        if (target.Icons.Any(i => _matched.ContainsKey(i.StableId) && !_recovery.Any(r => FenceIconLayout.IdentityComparer.Equals(r.StableId, i.StableId))))
            throw new InvalidOperationException("Recovery must be armed before animating desktop icons.");
        var proposed = _fences.Select(f => f.Id == target.Id ? target : f).ToArray();
        var final = IconPlacement.Targets(proposed, _monitors, Metrics).Where(t => _matched.ContainsKey(t.StableId)).ToArray();
        var parked = proposed.Select(f => f.Id == target.Id && !f.IsCollapsed ? f.ToggleCollapsed() : f).ToArray();
        var plan = new FenceIconAnimationPlan(target, snapshot.Icons, final, _monitors, startIsCollapsed,
            IconPlacement.Targets(parked, _monitors, Metrics), FenceInteractionGeometry.HeaderHeight * _host.GetScale(target));
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
            // Commit all parking slots and finish Shell redraw as soon as the last
            // visible glyph leaves. The remaining collapse frames only resize Fence.
            var targets = completes ? _final : _group;
            _changed.Clear();
            foreach (var t in targets)
                if (!_owner._matched.TryGetValue(t.StableId, out var icon) || icon.Position != t.Position) _changed.Add(t);
            var changed = _changed.Count != 0;
            if (!changed && !completes) { present(); return; }
            if (changed) _owner._host.SetIconTargets(_changed);
            var started = Stopwatch.GetTimestamp();
            try
            {
                var shellStarted = Stopwatch.GetTimestamp();
                if (changed) { await _shell.PositionAsync(targets, _owner._shutdown.Token); BulkUpdates++; }
                if (_owner._disposed) throw new OperationCanceledException("Desktop icons are shutting down.");
                foreach (var target in targets)
                {
                    if (!_owner._matched.TryGetValue(target.StableId, out var old)) continue;
                    _owner._matched[target.StableId] = old with { Position = target.Position, Bounds = old.Bounds with
                    { X = old.Bounds.X + target.Position.X - old.Position.X, Y = old.Bounds.Y + target.Position.Y - old.Position.Y } };
                }
                _owner._host.SetIcons(_owner._matched.Values.ToArray());
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
