using System.Diagnostics;
using DesktopOrganizer.Core.Localization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DesktopOrganizer.Core.Desktop;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

[SupportedOSPlatform("windows")]
public sealed partial class DesktopIconService : IDesktopIconService, IDesktopSelectionReader, IDesktopDropPositioner, IDesktopDropReconciler, IDesktopPositionDiagnostics, IDesktopIconAnimationPositioner, IDesktopItemConcealment, IDisposable
{
    private readonly NativeStaWorker _worker;
    private readonly DesktopDiscovery _discovery;
    private readonly ILogger<DesktopIconService> _logger;
    private Session? _session;
    private string? _lastError;
    private bool _disposed;
    private long _positionBatches, _redrawCompletions;
    private readonly ExplorerInteractionTrace? _trace;
    private readonly HashSet<AnimationSession> _animations = [];
    public DesktopPositionCounters PositionCounters => new(Interlocked.Read(ref _positionBatches), Interlocked.Read(ref _redrawCompletions));
    public static bool IsConfirmedMissingFile(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity) || !Path.IsPathFullyQualified(identity)) return false;
        try { _ = File.GetAttributes(identity); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }
    public DesktopIconService(ILogger<DesktopIconService> logger, ILogger<DesktopDiscovery> discoveryLogger, ExplorerInteractionTrace? interactionTrace = null, string? concealmentJournal = null)
    {
        _logger = logger; _discovery = new(discoveryLogger); _trace = interactionTrace;
        _concealmentPath = concealmentJournal is null ? null : Path.GetFullPath(concealmentJournal); LoadConcealment();
        _worker = new NativeStaWorker(() =>
        {
            foreach (var animation in _animations.ToArray()) animation.ReleaseOnWorker();
            _session?.Dispose(); _session = null;
        });
    }
    public Task<DesktopIconSnapshot> ReadAsync(CancellationToken cancellationToken = default) => _worker.Run(() =>
    {
        try
        {
            var session = GetSession();
            var result = session.Read(cancellationToken);
            if (result.Error != _lastError)
            {
                if (result.Error is not null) _logger.LogWarning("Desktop icon snapshot: {Error}", result.Error);
                else _logger.LogInformation("Desktop icon reader connected; Icons={Count}", result.Icons.Count);
                _lastError = result.Error;
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (_lastError != error.Message) _logger.LogWarning(error, "Desktop icon reader unavailable; retrying");
            _lastError = error.Message;
            // A transient read failure must not restore snap-to-grid and reflow parked items during a drag.
            if (_session is not null && _discovery.Validate(_session.Handles))
                return new DesktopIconSnapshot(true, false, [], IconGridMetrics.Default, error.Message);
            _session?.Dispose(); _session = null;
            return new DesktopIconSnapshot(false, false, [], IconGridMetrics.Default, error.Message);
        }
    }, cancellationToken);

    public Task PositionAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default)
    {
        // Coalescing happens in the coordinator; there is never a queue of hundreds of move jobs.
        return _worker.Run(() => { GetSession().Position(targets, cancellationToken); return true; }, cancellationToken);
    }
    public Task<IDesktopIconAnimationSession> BeginAnimationAsync(IReadOnlyList<DesktopIconTarget> initialTargets,
        IReadOnlyList<DesktopIconTarget> finalTargets, CancellationToken cancellationToken = default) =>
        _worker.Run<IDesktopIconAnimationSession>(() => new AnimationSession(this, GetSession(), initialTargets, finalTargets), cancellationToken);
    public Task<DesktopIconSnapshot> ReadAnimationItemsAsync(IReadOnlySet<string> itemIds, CancellationToken cancellationToken = default) =>
        _worker.Run(() => GetSession().ReadAnimationItems(itemIds, cancellationToken), cancellationToken);
    private sealed class AnimationSession : IDesktopIconAnimationSession
    {
        private readonly DesktopIconService _owner;
        private readonly Session _session;
        private readonly Dictionary<string, nint> _clones = new(FenceIconLayout.IdentityComparer);
        private readonly nint[] _framePidls;
        private readonly NativeMethods.Point[] _framePositions;
        private readonly DesktopCoordinates _coordinates;
        private readonly nint _dpiContext;
        private bool _disposed, _pendingRedraw;
        public AnimationSession(DesktopIconService owner, Session session, IReadOnlyList<DesktopIconTarget> initial, IReadOnlyList<DesktopIconTarget> final)
        {
            _owner = owner; _session = session; _coordinates = session.Coordinates;
            _dpiContext = NativeMethods.GetWindowDpiAwarenessContext(session.Handles.Host);
            try
            {
                RetainIdentities(initial);
                RetainIdentities(final);
                _framePidls = new nint[_clones.Count];
                _framePositions = new NativeMethods.Point[_clones.Count];
                session.PrepareAnimationPositioning();
                owner._animations.Add(this);
            }
            catch { FreeClones(); throw; }
        }
        private void RetainIdentities(IReadOnlyList<DesktopIconTarget> targets)
        {
            foreach (var target in targets)
            {
                var id = target.StableId;
                if (_clones.ContainsKey(id)) continue;
                var clone = NativeMethods.ILClone(_session.IdentityPidl(id));
                if (clone == 0) throw new OutOfMemoryException("Cannot retain Shell animation identities.");
                _clones.Add(id, clone);
            }
        }
        private void Validate()
        {
            ObjectDisposedException.ThrowIf(_disposed || _owner._disposed, this);
            if (_owner._session != _session || !_owner._discovery.Validate(_session.Handles))
                throw new InvalidOperationException("Animation lost its validated Shell view.");
        }
        public Task PositionAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default) =>
            _owner._worker.Run(() =>
            {
                Validate(); cancellationToken.ThrowIfCancellationRequested();
                var count = targets.Count;
                if (count == 0) return true;
                if (count > _framePidls.Length) throw new InvalidOperationException("Too many items in the animation frame.");
                for (var i = 0; i < count; i++)
                {
                    var target = targets[i];
                    if (!_clones.TryGetValue(target.StableId, out _framePidls[i]))
                        throw new InvalidOperationException("Animation identities changed during a frame.");
                    var point = _coordinates.ToView(target.Position);
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < int.MinValue || point.X > int.MaxValue || point.Y < int.MinValue || point.Y > int.MaxValue)
                        throw new InvalidOperationException("Animation position is outside Shell coordinates.");
                    _framePositions[i] = new((int)Math.Round(point.X), (int)Math.Round(point.Y));
                }
                using var dpi = new DesktopDpiScope(_dpiContext);
                _pendingRedraw = true;
                _owner._trace?.Mark("Animation.SelectAndPositionItems.begin", count);
                var result = _session.PositionAnimation(count, _framePidls, _framePositions);
                _owner._trace?.Mark("Animation.SelectAndPositionItems.end");
                Marshal.ThrowExceptionForHR(result);
                if (result != 0) throw new InvalidOperationException("Shell did not accept animation positions.");
                return true;
            }, cancellationToken);
        public Task CompleteFrameAsync(CancellationToken cancellationToken = default) => _owner._worker.Run(() =>
        {
            if (!_pendingRedraw) return true;
            Validate();
            _session.CompleteAnimationRedraw(); _pendingRedraw = false;
            return true;
        }, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            if (_owner._disposed) return; // Worker cleanup owns outstanding COM work and clones until it completes.
            try { await CompleteFrameAsync(); }
            finally
            {
                await _owner._worker.Run(() => { ReleaseOnWorker(); return true; });
            }
        }
        public void ReleaseOnWorker()
        {
            if (_disposed) return;
            try
            {
                if (_pendingRedraw && _owner._session == _session && _owner._discovery.Validate(_session.Handles))
                    _session.CompleteAnimationRedraw();
            }
            catch (Exception error) { _owner._logger.LogWarning(error, "Animation Shell redraw could not finish during cleanup"); }
            finally { _pendingRedraw = false; _disposed = true; FreeClones(); _owner._animations.Remove(this); }
        }
        private void FreeClones() { foreach (var clone in _clones.Values) Marshal.FreeCoTaskMem(clone); _clones.Clear(); }
    }
    public Task<DesktopSelection> ReadSelectionAsync(DesktopPoint pressPoint, CancellationToken cancellationToken = default) =>
        _worker.Run(() => GetSession().Selection(pressPoint, cancellationToken), cancellationToken);
    public Task PositionDropAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default) =>
        _worker.Run(() => { GetSession().Position(targets, cancellationToken, deferRedraw: true); return true; }, cancellationToken);
    public Task ReconcileDropAsync(Func<DesktopIconSnapshot, IReadOnlyList<DesktopIconTarget>> layout,
        CancellationToken cancellationToken = default, IReadOnlySet<string>? positionScope = null) =>
        _worker.Run(() => { GetSession().ReconcileDrop(layout, cancellationToken, positionScope); return true; }, cancellationToken);
    /// <summary>Diagnostic of the documented redraw lease only; never moves an item or synthesizes input.</summary>
    public Task<IReadOnlyList<string>> ProbeRedrawVisibilityAsync() => _worker.Run<IReadOnlyList<string>>(() => GetSession().ProbeRedrawVisibility());
    /// <summary>Checks real Shell selection enumeration; restores the original selected indices in finally.</summary>
    public Task<string> ProbeSelectionEnumerationAsync() => _worker.Run(() => GetSession().ProbeSelectionEnumeration());
    private Session GetSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null && _discovery.Validate(_session.Handles)) return _session;
        _session?.Dispose(); _session = null;
        var handles = _discovery.Find() ?? throw new InvalidOperationException("Validated desktop icon ListView unavailable.");
        _session = new Session(handles, _logger, () => Interlocked.Increment(ref _positionBatches), () => Interlocked.Increment(ref _redrawCompletions), _trace);
        return _session;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _worker.Dispose(); }

    private sealed class Session : IDisposable
    {
        public DesktopHandles Handles { get; }
        private readonly ShellFolderView _shell;
        private readonly ILogger _logger;
        private readonly Dictionary<string, nint> _pidls = new(FenceIconLayout.IdentityComparer);
        private DesktopCoordinates _coordinates;
        private DesktopIconSnapshot? _completeSnapshot;
        private uint _completeDpi;
        private bool _restoreGrid;
        private readonly Action _positioned, _redrawn;
        private readonly ExplorerInteractionTrace? _trace;
        public DesktopCoordinates Coordinates => _coordinates;
        public nint IdentityPidl(string id) => _pidls.TryGetValue(id, out var pidl) ? pidl : throw new InvalidOperationException("Shell animation identity is missing.");
        public int PositionAnimation(int count, nint[] pidls, NativeMethods.Point[] positions)
        { var result = _shell.View.SelectAndPositionItems((uint)count, pidls, positions, 0x80000080); _positioned(); return result; }
        public void CompleteAnimationRedraw() => SetRedraw(true);
        public void PrepareAnimationPositioning()
        {
            if (_shell.View.GetAutoArrange() != 1) throw new InvalidOperationException("Explorer auto-arrange prevents icon animation.");
            Marshal.ThrowExceptionForHR(_shell.View.GetCurrentFolderFlags(out var flags));
            if ((flags & 4) != 0) { Marshal.ThrowExceptionForHR(_shell.View.SetCurrentFolderFlags(4, 0)); _restoreGrid = true; }
        }
        public Session(DesktopHandles handles, ILogger logger, Action positioned, Action redrawn, ExplorerInteractionTrace? trace)
        { Handles = handles; _logger = logger; _shell = new(handles); _positioned = positioned; _redrawn = redrawn; _trace = trace; }
        public DesktopSelection Selection(DesktopPoint pressPoint, CancellationToken cancellationToken)
        {
            _trace?.Mark("SelectedSet.read.begin");
            using var dpi = new DesktopDpiScope(NativeMethods.GetWindowDpiAwarenessContext(Handles.Host));
            using var memory = new RemoteListViewBuffer(Handles.ProcessId, _logger);
            var origin = new NativeMethods.Point();
            if (!NativeMethods.ClientToScreen(Handles.IconList, ref origin)) throw new InvalidOperationException("Cannot read desktop coordinate origin.");
            var viewOrigin = SendMessage(Handles.IconList, 0x1029, 0, memory.Address(0)) != 0 ? memory.Read<NativeMethods.Point>(0) : new();
            _coordinates = new(new(origin.X, origin.Y), new(viewOrigin.X, viewOrigin.Y));
            var point = new NativeMethods.Point((int)Math.Round(pressPoint.X), (int)Math.Round(pressPoint.Y));
            if (!NativeMethods.ScreenToClient(Handles.IconList, ref point)) throw new InvalidOperationException("Cannot map desktop press.");
            memory.Write(0, new HitTestInfo { Point = point });
            var hit = (int)SendMessage(Handles.IconList, 0x1012, 0, memory.Address(0)); // LVM_HITTEST, read only.
            // Explorer's GetSelectedItem cursor skips adjacent selected indices with the former
            // index+1 walk. GetSelection returns the complete Shell-owned selection as one array.
            var selected = SelectedIdentities(cancellationToken);
            var initial = new List<DesktopIconTarget>();
            var selectedSet = selected.ToHashSet(FenceIconLayout.IdentityComparer);
            var count = (int)SendMessage(Handles.IconList, 0x1004, 0, 0);
            if (count is < 0 or > 100000) throw new InvalidOperationException("Invalid desktop item count.");
            for (var index = 0; index < count && initial.Count < selectedSet.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Identity(index);
                if (selectedSet.Contains(id)) initial.Add(new(id, InitialPosition(index)));
            }
            var hitId = hit >= 0 ? Identity(hit) : null;
            if (hitId is not null && !selected.Contains(hitId, FenceIconLayout.IdentityComparer)) initial.Add(new(hitId, InitialPosition(hit)));
            _trace?.Mark("SelectedSet.read.end", selected.Count);
            return new(selected, hitId) { InitialPositions = initial };
        }
        private IReadOnlyList<string> SelectedIdentities(CancellationToken cancellationToken)
        {
            var result = _shell.View.GetSelection(false, out var selection);
            // With noneImpliesFolder=false Explorer returns HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
            // for an empty selection. This is an ordinary background click, not a read failure.
            if (result == unchecked((int)0x80070490))
            {
                if (selection is not null) Marshal.ReleaseComObject(selection);
                return [];
            }
            Marshal.ThrowExceptionForHR(result);
            if (selection is null) return [];
            try
            {
                Marshal.ThrowExceptionForHR(selection.GetCount(out var count));
                if (count > 100000) throw new InvalidOperationException("Invalid Shell selection count.");
                var ids = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Marshal.ThrowExceptionForHR(selection.GetItemAt(i, out var item));
                    try
                    {
                        Marshal.ThrowExceptionForHR(item.GetDisplayName(0x80028000, out var name));
                        try { ids.Add(Marshal.PtrToStringUni(name) ?? throw new InvalidOperationException("Empty selected item identity.")); }
                        finally { Marshal.FreeCoTaskMem(name); }
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
                return ids.Distinct(FenceIconLayout.IdentityComparer).ToArray();
            }
            finally { Marshal.ReleaseComObject(selection); }
        }
        public string ProbeSelectionEnumeration()
        {
            var count = (int)SendMessage(Handles.IconList, 0x1004, 0, 0);
            if (count < 6) throw new InvalidOperationException("Six desktop items required for the selection probe.");
            var original = new List<int>();
            Marshal.ThrowExceptionForHR(_shell.View.GetFocusedItem(out var focus));
            for (var i = 0; i < count; i++)
                if ((SendMessage(Handles.IconList, 0x102C, i, 2) & 2) != 0) original.Add(i); // LVM_GETITEMSTATE
            try
            {
                for (var i = 0; i < 4; i++) Marshal.ThrowExceptionForHR(_shell.View.SelectItem(i, i == 0 ? 5u : 1u));
                var actual = Enumerable.Range(0, count).Where(i => (SendMessage(Handles.IconList, 0x102C, i, 2) & 2) != 0).ToArray();
                var walked = new List<int>();
                var start = 0;
                while (_shell.View.GetSelectedItem(start, out var index) == 0)
                {
                    walked.Add(index); start = index + 1;
                    if (walked.Count > count) throw new InvalidOperationException("Invalid selected index walk.");
                }
                var full = SelectedIdentities(CancellationToken.None);
                var expected = actual.Select(Identity).ToHashSet(FenceIconLayout.IdentityComparer);
                if (!expected.SetEquals(full)) throw new InvalidOperationException("Shell selection snapshot omitted selected items.");
                var first = Read(CancellationToken.None).Icons.Single(i => i.Index == 0);
                var captured = Selection(new(first.Bounds.X + first.Bounds.Width / 2, first.Bounds.Y + 10), CancellationToken.None);
                if (!expected.SetEquals(captured.SelectedIds) || captured.InitialPositions.Count != 4 ||
                    captured.DraggedItemId is null || !expected.Contains(captured.DraggedItemId))
                    throw new InvalidOperationException("Press snapshot lost selected items, anchor or initial positions.");
                Marshal.ThrowExceptionForHR(_shell.View.SelectItem(-1, 4));
                if (SelectedIdentities(CancellationToken.None).Count != 0) throw new InvalidOperationException("Empty selection snapshot was not empty.");
                return $"Selected indices=[{string.Join(',', actual)}]; old GetSelectedItem walk=[{string.Join(',', walked)}]; GetSelection snapshot={full.Count}; press snapshot={captured.SelectedIds.Count}; initial positions={captured.InitialPositions.Count}; empty selection=0; PASS";
            }
            finally
            {
                Marshal.ThrowExceptionForHR(_shell.View.SelectItem(-1, 4));
                foreach (var index in original) Marshal.ThrowExceptionForHR(_shell.View.SelectItem(index, 1));
                if (focus >= 0) Marshal.ThrowExceptionForHR(_shell.View.SelectItem(focus, 0x10));
            }
        }
        private DesktopPoint InitialPosition(int index)
        {
            Marshal.ThrowExceptionForHR(_shell.View.Item(index, out var pidl));
            try
            {
                Marshal.ThrowExceptionForHR(_shell.View.GetItemPosition(pidl, out var position));
                return _coordinates.ToScreen(new(position.X, position.Y));
            }
            finally { if (pidl != 0) Marshal.FreeCoTaskMem(pidl); }
        }
        private string Identity(int index)
        {
            Marshal.ThrowExceptionForHR(_shell.View.Item(index, out var pidl));
            try { return ShellFolderView.Name(pidl, 0x80028000); }
            finally { if (pidl != 0) Marshal.FreeCoTaskMem(pidl); }
        }
        public DesktopIconSnapshot ReadAnimationItems(IReadOnlySet<string> itemIds, CancellationToken cancellationToken)
        {
            _trace?.Mark("Animation.snapshot.begin", itemIds.Count);
            try
            {
                if (_completeSnapshot is { } cached)
                {
                    var items = cached.Icons.Where(i => itemIds.Contains(i.StableId)).ToArray();
                    if (items.Length == itemIds.Count && Read(cancellationToken, items) is { Error: null } scoped)
                    {
                        _trace?.Mark("Animation.snapshot.scoped", items.Length);
                        return scoped;
                    }
                }
                _trace?.Mark("Animation.snapshot.fullFallback");
                var full = Read(cancellationToken);
                return full with { Icons = full.Icons.Where(i => itemIds.Contains(i.StableId)).ToArray() };
            }
            finally { _trace?.Mark("Animation.snapshot.end"); }
        }
        public DesktopIconSnapshot Read(CancellationToken cancellationToken, IReadOnlyList<DesktopIcon>? scope = null)
        {
            using var dpi = new DesktopDpiScope(NativeMethods.GetWindowDpiAwarenessContext(Handles.Host));
            using var memory = new RemoteListViewBuffer(Handles.ProcessId, _logger);
            var count = (int)SendMessage(Handles.IconList, 0x1004, 0, 0);
            if (count is < 0 or > 100000) throw new InvalidOperationException("Invalid ListView icon count.");
            Marshal.ThrowExceptionForHR(_shell.View.ItemCount(2, out var shellCount)); // SVGIO_ALLVIEW
            var origin = new NativeMethods.Point();
            if (!NativeMethods.ClientToScreen(Handles.IconList, ref origin)) throw new InvalidOperationException("Cannot read desktop coordinate origin.");
            var viewOrigin = SendMessage(Handles.IconList, 0x1029, 0, memory.Address(0)) != 0 ? memory.Read<NativeMethods.Point>(0) : new();
            var coordinates = new DesktopCoordinates(new(origin.X, origin.Y), new(viewOrigin.X, viewOrigin.Y));
            var spacing = (long)SendMessage(Handles.IconList, 0x1033, 0, 0);
            var stepX = (int)(spacing & 0xFFFF); var stepY = (int)((spacing >> 16) & 0xFFFF);
            var dpiAtStart = NativeMethods.GetDpiForWindow(Handles.IconList);
            var cached = scope is null ? null : _completeSnapshot;
            if (scope is not null && (cached is null || shellCount != count || count != cached.Icons.Count ||
                dpiAtStart != _completeDpi || stepX != cached.Metrics.StepX || stepY != cached.Metrics.StepY))
                return new(true, false, [], IconGridMetrics.Default, "Explorer inventory or grid changed before animation.");
            var readCount = scope?.Count ?? count;
            var icons = new List<DesktopIcon>(readCount);
            var newPidls = new Dictionary<string, nint>(FenceIconLayout.IdentityComparer);
            var failed = 0;
            string? firstFailure = null;
            double itemWidth = 1, itemHeight = 1, offsetX = 0, offsetY = 0;
            try
            {
                for (var i = 0; i < readCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = scope is null ? i : scope[i].Index;
                    nint pidl = 0;
                    try
                    {
                        Marshal.ThrowExceptionForHR(_shell.View.Item(index, out pidl));
                        var identity = ShellFolderView.Name(pidl, 0x80028000); // SIGDN_DESKTOPABSOLUTEPARSING
                        if (scope is not null && !FenceIconLayout.IdentityComparer.Equals(identity, scope[i].StableId))
                            throw new InvalidOperationException("Explorer index changed before animation.");
                        var shellName = ShellFolderView.Name(pidl, 0);
                        if (SendMessage(Handles.IconList, 0x1010, index, memory.Address(0)) == 0) throw new InvalidOperationException("LVM_GETITEMPOSITION failed.");
                        var point = memory.Read<NativeMethods.Point>(0);
                        Marshal.ThrowExceptionForHR(_shell.View.GetItemPosition(pidl, out var shellPoint));
                        if (Math.Abs((long)point.X - shellPoint.X) > 2 || Math.Abs((long)point.Y - shellPoint.Y) > 2)
                            throw new InvalidOperationException("Shell/ListView changed during enumeration.");
                        var item = new ListViewItem { Index = index, Text = memory.Address(256), TextLength = 2048 };
                        memory.Write(64, item);
                        var length = (int)SendMessage(Handles.IconList, 0x1073, index, memory.Address(64));
                        var name = length > 0 ? memory.ReadText(256, length) : shellName; // Owner-data views use Shell display text.
                        if (!StringComparer.CurrentCulture.Equals(name, shellName)) throw new InvalidOperationException("Icon index identity changed during enumeration.");
                        memory.Write(16, new NativeMethods.Rect()); // LVIR_BOUNDS=0
                        var screen = coordinates.ToScreen(new(point.X, point.Y));
                        DesktopBounds bounds;
                        if (SendMessage(Handles.IconList, 0x100E, index, memory.Address(16)) != 0)
                        {
                            var rect = memory.Read<NativeMethods.Rect>(16);
                            bounds = new(origin.X + rect.Left, origin.Y + rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                        }
                        else bounds = new(screen.X, screen.Y, Math.Max(1, stepX), Math.Max(1, stepY));
                        if (!newPidls.TryAdd(identity, pidl)) throw new InvalidOperationException("Ambiguous desktop identity.");
                        pidl = 0;
                        icons.Add(new(index, identity, name, new(point.X, point.Y), screen, bounds));
                        if (bounds.Width >= itemWidth) { itemWidth = bounds.Width; offsetX = bounds.X - screen.X; }
                        if (bounds.Height >= itemHeight) { itemHeight = bounds.Height; offsetY = bounds.Y - screen.Y; }
                    }
                    catch (Exception itemError) when (itemError is not OperationCanceledException) { failed++; firstFailure ??= itemError.Message; }
                    finally { if (pidl != 0) Marshal.FreeCoTaskMem(pidl); }
                }
                Marshal.ThrowExceptionForHR(_shell.View.ItemCount(2, out var finalShellCount));
                var finalCount = (int)SendMessage(Handles.IconList, 0x1004, 0, 0);
                var finalOrigin = new NativeMethods.Point();
                var finalOriginRead = NativeMethods.ClientToScreen(Handles.IconList, ref finalOrigin);
                var finalViewOrigin = SendMessage(Handles.IconList, 0x1029, 0, memory.Address(0)) != 0 ? memory.Read<NativeMethods.Point>(0) : new();
                var finalDpi = NativeMethods.GetDpiForWindow(Handles.IconList);
                if (shellCount != count || finalShellCount != count || finalCount != count || finalDpi != dpiAtStart ||
                    !finalOriginRead || finalOrigin.X != origin.X || finalOrigin.Y != origin.Y ||
                    finalViewOrigin.X != viewOrigin.X || finalViewOrigin.Y != viewOrigin.Y)
                { failed++; firstFailure ??= "Explorer membership or coordinate origin changed during enumeration."; }
                // An incomplete enumeration cannot replace the last usable identity cache or prune assignments.
                if (failed == 0)
                {
                    _coordinates = coordinates;
                    if (scope is null)
                    {
                        foreach (var old in _pidls.Values) Marshal.FreeCoTaskMem(old);
                        _pidls.Clear();
                    }
                    foreach (var pair in newPidls)
                    {
                        if (_pidls.TryGetValue(pair.Key, out var replaced)) Marshal.FreeCoTaskMem(replaced);
                        _pidls[pair.Key] = pair.Value;
                    }
                    newPidls.Clear();
                }
                var autoArrange = _shell.View.GetAutoArrange();
                Marshal.ThrowExceptionForHR(autoArrange);
                var canPosition = autoArrange == 1; // S_FALSE: auto-arrange is off.
                var error = !canPosition ? Texts.T("Отключите «Упорядочить значки автоматически» на рабочем столе для группировки иконок.") :
                    failed > 0 ? Texts.F("Не удалось прочитать {0} из {1} иконок: {2}", failed, readCount, firstFailure) : null;
                var snapshot = new DesktopIconSnapshot(true, canPosition, icons, cached?.Metrics ?? new IconGridMetrics(stepX, stepY, itemWidth, itemHeight, offsetX, offsetY).Validated(), error,
                    _restoreGrid ? Texts.T("Сетка Explorer временно отключена; восстановится при закрытии приложения.") : null);
                if (error is null && scope is null) { _completeSnapshot = snapshot; _completeDpi = finalDpi; }
                return snapshot;
            }
            finally { foreach (var pidl in newPidls.Values) Marshal.FreeCoTaskMem(pidl); }
        }
        public void ReconcileDrop(Func<DesktopIconSnapshot, IReadOnlyList<DesktopIconTarget>> layout, CancellationToken cancellationToken,
            IReadOnlySet<string>? positionScope)
        {
            RequireCompletedGesture();
            // Enumerate and compute the full proposal before freezing. Most drop-out
            // operations only change assignment and already have final Explorer
            // positions. They must not toggle redraw at all.
            var snapshot = ReadDropPositions(cancellationToken, positionScope);
            if (!snapshot.Connected || !snapshot.CanPosition || snapshot.Error is not null)
                throw new InvalidOperationException("Explorer drop snapshot is unavailable.");
            var targets = layout(snapshot);
            if (targets.Count == 0) { _trace?.Mark("Drop.noPosition/noRedraw"); return; }
            cancellationToken.ThrowIfCancellationRequested();
            RequireCompletedGesture();
            // Position prepares PIDLs/grid before the short false -> bulk -> true lease.
            Position(targets, cancellationToken, deferRedraw: true);
        }
        private DesktopIconSnapshot ReadDropPositions(CancellationToken cancellationToken, IReadOnlySet<string>? positionScope)
        {
            _trace?.Mark("Drop.positions.begin");
            try
            {
                var cached = _completeSnapshot;
                if (cached is null || cached.Icons.Count != _pidls.Count || NativeMethods.GetDpiForWindow(Handles.IconList) != _completeDpi)
                    return Read(cancellationToken);
                using var dpi = new DesktopDpiScope(NativeMethods.GetWindowDpiAwarenessContext(Handles.Host));
                using var memory = new RemoteListViewBuffer(Handles.ProcessId, _logger);
                DesktopCoordinates Coordinates()
                {
                    var origin = new NativeMethods.Point();
                    if (!NativeMethods.ClientToScreen(Handles.IconList, ref origin)) throw new InvalidOperationException("Desktop origin unavailable.");
                    var view = SendMessage(Handles.IconList, 0x1029, 0, memory.Address(0)) != 0 ? memory.Read<NativeMethods.Point>(0) : new();
                    return new(new(origin.X, origin.Y), new(view.X, view.Y));
                }
                bool SameInventory()
                {
                    Marshal.ThrowExceptionForHR(_shell.View.ItemCount(2, out var count));
                    return count == cached.Icons.Count && (int)SendMessage(Handles.IconList, 0x1004, 0, 0) == count;
                }
                var coordinates = Coordinates();
                var spacing = (long)SendMessage(Handles.IconList, 0x1033, 0, 0);
                if (!SameInventory() || (spacing & 0xFFFF) != cached.Metrics.StepX || ((spacing >> 16) & 0xFFFF) != cached.Metrics.StepY)
                    return Read(cancellationToken);
                var candidates = positionScope is null ? cached.Icons : cached.Icons.Where(icon => positionScope.Contains(icon.StableId)).ToArray();
                var icons = new DesktopIcon[candidates.Count];
                for (var i = 0; i < icons.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var old = candidates[i];
                    Marshal.ThrowExceptionForHR(_shell.View.GetItemPosition(IdentityPidl(old.StableId), out var point));
                    var screen = coordinates.ToScreen(new(point.X, point.Y));
                    icons[i] = old with { ViewPosition = new(point.X, point.Y), Position = screen,
                        Bounds = old.Bounds with { X = old.Bounds.X + screen.X - old.Position.X, Y = old.Bounds.Y + screen.Y - old.Position.Y } };
                }
                if (!SameInventory() || coordinates != Coordinates() || NativeMethods.GetDpiForWindow(Handles.IconList) != _completeDpi)
                    return Read(cancellationToken);
                var arrange = _shell.View.GetAutoArrange(); Marshal.ThrowExceptionForHR(arrange);
                if (arrange != 1) return Read(cancellationToken);
                _coordinates = coordinates;
                _trace?.Mark(positionScope is null ? "Drop.positions.cached" : "Drop.positions.scopedParking", icons.Length);
                return cached with { Icons = icons };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            { _trace?.Mark("Drop.positions.fallback"); return Read(cancellationToken); }
            finally { _trace?.Mark("Drop.positions.end"); }
        }
        private void RequireCompletedGesture()
        {
            var info = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
            var thread = NativeMethods.GetWindowThreadProcessId(Handles.IconList, out _);
            if ((NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0 || !NativeMethods.GetGUIThreadInfo(thread, ref info) || info.Capture != 0)
                throw new InvalidOperationException("Explorer gesture must finish before drop reconciliation.");
        }
        private void SetRedraw(bool enabled)
        {
            _trace?.Mark(enabled ? "SetRedraw(true).begin" : "SetRedraw(false).begin");
            Marshal.ThrowExceptionForHR(_shell.View.SetRedraw(enabled));
            if (enabled) _redrawn();
            _trace?.Mark(enabled ? "SetRedraw(true).end/visible" : "SetRedraw(false).end/visible", NativeMethods.IsWindowVisible(Handles.IconList) ? 1 : -1);
        }
        public void Position(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken, bool deferRedraw = false, bool completeRedraw = true)
        {
            if (_shell.View.GetAutoArrange() != 1) throw new InvalidOperationException("Explorer auto-arrange prevents positioning desktop icons.");
            using var dpi = new DesktopDpiScope(NativeMethods.GetWindowDpiAwarenessContext(Handles.Host));
            var pidls = new List<nint>(targets.Count);
            var positions = new List<NativeMethods.Point>(targets.Count);
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_pidls.TryGetValue(target.StableId, out var pidl)) continue;
                var point = _coordinates.ToView(target.Position);
                if (point.X < int.MinValue || point.X > int.MaxValue || point.Y < int.MinValue || point.Y > int.MaxValue) continue;
                pidls.Add(pidl); positions.Add(new((int)Math.Round(point.X), (int)Math.Round(point.Y)));
            }
            if (pidls.Count == 0) return;
            Marshal.ThrowExceptionForHR(_shell.View.GetCurrentFolderFlags(out var folderFlags));
            const uint snapToGrid = 4;
            if ((folderFlags & snapToGrid) != 0)
            {
                Marshal.ThrowExceptionForHR(_shell.View.SetCurrentFolderFlags(snapToGrid, 0));
                _restoreGrid = true;
                _logger.LogInformation("Explorer snap-to-grid temporarily disabled for exact Fence positioning; restored when the icon session closes");
            }
            // Preserve focus/selection, and let Shell persist the genuine icon positions.
            // Only the short post-drop bulk operation is suspended, never mouse/OLE input, enumeration or disk I/O.
            try
            {
                if (deferRedraw) SetRedraw(false);
                _trace?.Mark("SelectAndPositionItems.begin", pidls.Count);
                var result = _shell.View.SelectAndPositionItems((uint)pidls.Count, pidls.ToArray(), positions.ToArray(), 0x80000080);
                _positioned();
                _trace?.Mark("SelectAndPositionItems.end");
                Marshal.ThrowExceptionForHR(result);
                if (result != 0) throw new InvalidOperationException("Shell did not accept desktop icon positions.");
            }
            finally
            {
                if (completeRedraw) SetRedraw(true);
            }
            // This Explorer can retain the previous rendered positions after a successful
            // SelectAndPositionItems, even while GetItemPosition reports the new coordinates.
            // Complete the documented Shell redraw path before acknowledging the visual move.
        }
        /// <summary>
        /// Brings items whose Hidden attribute was cleared back into the view and places them, all inside one
        /// documented redraw lease: Explorer adds a re-created item at its first free grid slot, which is never
        /// presented. Returns the identities that appeared.
        /// </summary>
        public IReadOnlyList<string> Reveal(IReadOnlyList<(string Id, DesktopPoint? Position)> items, CancellationToken cancellationToken)
        {
            SetRedraw(false);
            try
            {
                _trace?.Mark("Reveal.notify", items.Count);
                // One SHCNE_UPDATEDIR per folder re-adds every un-hidden item in a single re-enumeration
                // (about twice as fast as per-item DELETE+CREATE pairs for a group).
                foreach (var folder in items.Select(i => Path.GetDirectoryName(i.Id)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                    NotifyItem(0x1000, folder);
                var expected = items.Select(i => i.Id).ToHashSet(FenceIconLayout.IdentityComparer);
                var present = new HashSet<string>(FenceIconLayout.IdentityComparer);
                var started = Stopwatch.GetTimestamp();
                var lastRead = started;
                int? lastCount = null;
                var reads = 0;
                while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)SendMessage(Handles.IconList, 0x1004, 0, 0); // LVM_GETITEMCOUNT
                    // Re-scan when icons are added. A periodic read covers same-count replacements and unstable snapshots.
                    if (count != lastCount || Stopwatch.GetElapsedTime(lastRead) >= TimeSpan.FromMilliseconds(100))
                    {
                        lastCount = count;
                        lastRead = Stopwatch.GetTimestamp();
                        var read = Read(cancellationToken);
                        reads++;
                        present.Clear();
                        if (read.Error is null)
                        {
                            foreach (var icon in read.Icons)
                                if (expected.Contains(icon.StableId)) present.Add(icon.StableId);
                            if (present.Count == expected.Count) break;
                        }
                    }
                    Thread.Sleep(5);
                }
                _trace?.Mark("Reveal.inventoryReads", reads);
                _trace?.Mark("Reveal.present", present.Count);
                var targets = items.Where(i => i.Position is not null && present.Contains(i.Id))
                    .Select(i => new DesktopIconTarget(i.Id, i.Position!.Value)).ToArray();
                if (targets.Length != 0) Position(targets, cancellationToken, completeRedraw: false);
                return items.Where(i => present.Contains(i.Id)).Select(i => i.Id).ToArray();
            }
            finally { SetRedraw(true); }
        }
        public IReadOnlyList<string> ProbeRedrawVisibility()
        {
            var info = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
            var thread = NativeMethods.GetWindowThreadProcessId(Handles.IconList, out _);
            if ((NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0 || !NativeMethods.GetGUIThreadInfo(thread, ref info) || info.Capture != 0)
                throw new InvalidOperationException("Wait for the actual Explorer gesture to finish before the diagnostic.");
            var states = new List<string>();
            string State(string label) => label + $": visible={NativeMethods.IsWindowVisible(Handles.IconList)}; style=0x{NativeMethods.GetWindowStyle(Handles.IconList, -16):X}; ex=0x{NativeMethods.GetWindowStyle(Handles.IconList, -20):X}";
            states.Add(State("Before"));
            try { Marshal.ThrowExceptionForHR(_shell.View.SetRedraw(false)); states.Add(State("SetRedraw(false)")); }
            finally { Marshal.ThrowExceptionForHR(_shell.View.SetRedraw(true)); states.Add(State("SetRedraw(true)")); }
            return states;
        }
        public void Dispose()
        {
            try
            {
                if (_restoreGrid) Marshal.ThrowExceptionForHR(_shell.View.SetCurrentFolderFlags(4, 4));
            }
            catch (Exception error) { _logger.LogWarning(error, "Could not restore Explorer snap-to-grid after icon session"); }
            finally { foreach (var pidl in _pidls.Values) Marshal.FreeCoTaskMem(pidl); _pidls.Clear(); _shell.Dispose(); }
        }
    }
    // Synchronous messages run only on the dedicated worker. The remote buffers stay alive until
    // the receiver has returned; timeout-based premature freeing would risk a remote use-after-free.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct ListViewItem
    {
        public uint Mask; public int Index, SubItem; public uint State, StateMask; public nint Text;
        public int TextLength, Image; public nint Parameter; public int Indent, GroupId; public uint Columns;
        public nint ColumnArray, ColumnFormats; public int Group;
    }
    [StructLayout(LayoutKind.Sequential)] private struct HitTestInfo
    { public NativeMethods.Point Point; public uint Flags; public int Item, SubItem, Group; }
}
