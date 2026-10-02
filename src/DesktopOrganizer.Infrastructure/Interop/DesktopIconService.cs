using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DesktopOrganizer.Core.Desktop;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

[SupportedOSPlatform("windows")]
public sealed class DesktopIconService : IDesktopIconService, IDisposable
{
    private readonly NativeStaWorker _worker;
    private readonly DesktopDiscovery _discovery;
    private readonly ILogger<DesktopIconService> _logger;
    private Session? _session;
    private string? _lastError;
    private bool _disposed;
    public DesktopIconService(ILogger<DesktopIconService> logger, ILogger<DesktopDiscovery> discoveryLogger)
    {
        _logger = logger; _discovery = new(discoveryLogger);
        _worker = new NativeStaWorker(() => { _session?.Dispose(); _session = null; });
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
            _session?.Dispose(); _session = null;
            return new DesktopIconSnapshot(false, false, [], IconGridMetrics.Default, error.Message);
        }
    }, cancellationToken);

    public Task PositionAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default)
    {
        // Coalescing happens in the coordinator; there is never a queue of hundreds of move jobs.
        return _worker.Run(() => { GetSession().Position(targets, cancellationToken); return true; }, cancellationToken);
    }
    private Session GetSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null && _discovery.Validate(_session.Handles)) return _session;
        _session?.Dispose(); _session = null;
        var handles = _discovery.Find() ?? throw new InvalidOperationException("Validated desktop icon ListView unavailable.");
        _session = new Session(handles, _logger);
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
        private bool _restoreGrid;
        public Session(DesktopHandles handles, ILogger logger) { Handles = handles; _logger = logger; _shell = new(handles); }
        public DesktopIconSnapshot Read(CancellationToken cancellationToken)
        {
            using var dpi = new DesktopDpiScope(NativeMethods.GetWindowDpiAwarenessContext(Handles.Host));
            using var memory = new RemoteListViewBuffer(Handles.ProcessId, _logger);
            var count = (int)SendMessage(Handles.IconList, 0x1004, 0, 0);
            if (count is < 0 or > 100000) throw new InvalidOperationException("Invalid ListView icon count.");
            var origin = new NativeMethods.Point();
            if (!NativeMethods.ClientToScreen(Handles.IconList, ref origin)) throw new InvalidOperationException("Cannot read desktop coordinate origin.");
            var viewOrigin = SendMessage(Handles.IconList, 0x1029, 0, memory.Address(0)) != 0 ? memory.Read<NativeMethods.Point>(0) : new();
            _coordinates = new(new(origin.X, origin.Y), new(viewOrigin.X, viewOrigin.Y));
            var spacing = (long)SendMessage(Handles.IconList, 0x1033, 0, 0);
            var stepX = (int)(spacing & 0xFFFF); var stepY = (int)((spacing >> 16) & 0xFFFF);
            var icons = new List<DesktopIcon>(count);
            var newPidls = new Dictionary<string, nint>(FenceIconLayout.IdentityComparer);
            var failed = 0;
            string? firstFailure = null;
            double itemWidth = 1, itemHeight = 1, offsetX = 0, offsetY = 0;
            try
            {
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    nint pidl = 0;
                    try
                    {
                        Marshal.ThrowExceptionForHR(_shell.View.Item(index, out pidl));
                        var identity = ShellFolderView.Name(pidl, 0x80028000); // SIGDN_DESKTOPABSOLUTEPARSING
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
                        var screen = _coordinates.ToScreen(new(point.X, point.Y));
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
                foreach (var old in _pidls.Values) Marshal.FreeCoTaskMem(old);
                _pidls.Clear();
                foreach (var pair in newPidls) _pidls.Add(pair.Key, pair.Value);
                newPidls.Clear();
                var autoArrange = _shell.View.GetAutoArrange();
                Marshal.ThrowExceptionForHR(autoArrange);
                var canPosition = autoArrange == 1; // S_FALSE: auto-arrange is off.
                var error = !canPosition ? "Отключите «Упорядочить значки автоматически» на рабочем столе для группировки иконок." :
                    failed > 0 ? $"Не удалось прочитать {failed} из {count} иконок: {firstFailure}" : null;
                return new(true, canPosition, icons, new IconGridMetrics(stepX, stepY, itemWidth, itemHeight, offsetX, offsetY).Validated(), error,
                    _restoreGrid ? "Сетка Explorer временно отключена; восстановится при закрытии приложения." : null);
            }
            finally { foreach (var pidl in newPidls.Values) Marshal.FreeCoTaskMem(pidl); }
        }
        public void Position(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken)
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
            var result = _shell.View.SelectAndPositionItems((uint)pidls.Count, pidls.ToArray(), positions.ToArray(), 0x80000080);
            Marshal.ThrowExceptionForHR(result);
            if (result != 0) throw new InvalidOperationException("Shell did not accept desktop icon positions.");
            // This Explorer can retain the previous rendered positions after a successful
            // SelectAndPositionItems, even while GetItemPosition reports the new coordinates.
            // Complete the documented Shell redraw path before acknowledging the visual move.
            Marshal.ThrowExceptionForHR(_shell.View.SetRedraw(true));
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
}
