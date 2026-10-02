using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

public sealed record DesktopHandles(nint Progman, nint WorkerW, nint DefView, nint IconList, nint Host, uint ProcessId)
{
    public override string ToString() => $"Progman=0x{Progman:X}; WorkerW=0x{WorkerW:X}; SHELLDLL_DefView=0x{DefView:X}; SysListView32=0x{IconList:X}; Host=0x{Host:X}; ExplorerPID={ProcessId}";
}

[SupportedOSPlatform("windows")]
public sealed class DesktopDiscovery(ILogger<DesktopDiscovery> logger)
{
    private DesktopHandles? _lastLogged;
    private string? _lastFailure;
    public string LastDiagnostics { get; private set; } = "Desktop discovery pending";

    public DesktopHandles? Find()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        var shell = NativeMethods.GetShellWindow();
        var pid = NativeMethods.ProcessOf(shell);
        LastDiagnostics = new DesktopHandles(progman, 0, 0, 0, 0, pid).ToString();
        if (!Matches(progman, "Progman", pid) || pid == 0 || !IsExplorer(pid)) return Disconnected();

        var roots = new List<nint> { progman };
        NativeMethods.EnumWindows((window, _) =>
        {
            if (Matches(window, "WorkerW", pid)) roots.Add(window);
            return true;
        }, 0);

        foreach (var root in roots)
        {
            nint view = 0;
            NativeMethods.EnumChildWindows(root, (window, _) =>
            {
                if (Matches(window, "SHELLDLL_DefView", pid)) { view = window; return false; }
                return true;
            }, 0);
            if (view == 0) continue;
            var list = NativeMethods.FindWindowEx(view, 0, "SysListView32", "FolderView");
            var parent = NativeMethods.GetParent(view);
            var worker = Matches(parent, "WorkerW", pid) ? parent : 0;
            var handles = new DesktopHandles(progman, worker, view, list, view, pid);
            LastDiagnostics = handles.ToString();
            if (!Validate(handles)) continue;
            if (_lastLogged != handles)
            {
                logger.LogInformation("Desktop discovery: {Handles}", handles);
                _lastLogged = handles;
            }
            _lastFailure = null;
            return handles;
        }
        return Disconnected();
    }

    public bool Validate(DesktopHandles handles)
    {
        if (!Matches(handles.Progman, "Progman", handles.ProcessId) ||
            NativeMethods.ProcessOf(NativeMethods.GetShellWindow()) != handles.ProcessId ||
            !Matches(handles.DefView, "SHELLDLL_DefView", handles.ProcessId) ||
            !Matches(handles.IconList, "SysListView32", handles.ProcessId) ||
            handles.Host != handles.DefView || NativeMethods.GetParent(handles.IconList) != handles.DefView)
            return false;
        var parent = NativeMethods.GetParent(handles.DefView);
        return (parent == handles.Progman || (parent == handles.WorkerW && Matches(parent, "WorkerW", handles.ProcessId))) &&
               NativeMethods.IsWindowVisible(handles.Host);
    }

    private DesktopHandles? Disconnected()
    {
        if (_lastFailure != LastDiagnostics)
            logger.LogWarning("Desktop host unavailable; rediscovery will retry. Found: {Diagnostics}", LastDiagnostics);
        _lastFailure = LastDiagnostics;
        _lastLogged = null;
        return null;
    }
    private static bool Matches(nint window, string name, uint pid) => window != 0 && NativeMethods.IsWindow(window) &&
        NativeMethods.ClassOf(window) == name && NativeMethods.ProcessOf(window) == pid;
    private static bool IsExplorer(uint pid)
    {
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
