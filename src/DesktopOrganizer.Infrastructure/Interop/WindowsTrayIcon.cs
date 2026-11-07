using DesktopOrganizer.Core.Localization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>UI-thread-owned notification icon and native menu. No desktop parent or overlay.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayIcon(ILogger<WindowsTrayIcon> logger, string? iconPath = null) : IDisposable
{
    private readonly ILogger<WindowsTrayIcon> _logger = logger;
    internal const uint Callback = 0x8000 + 73;
    private readonly NativeMethods.WindowProc _procedure = WindowProcedure;
    private static readonly Dictionary<nint, WindowsTrayIcon> Owners = [];
    private nint _window;
    private ushort _class;
    private uint _taskbarCreated;
    private nint _icon;
    private bool _ownsIcon;
    private readonly string _className = "DesktopOrganizer.Tray." + Guid.NewGuid().ToString("N");
    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public void Start()
    {
        if (_window != 0) return;
        if (iconPath is not null)
        {
            _icon = TrayNative.LoadImage(0, Path.GetFullPath(iconPath), 1, NativeMethods.GetSystemMetrics(49), NativeMethods.GetSystemMetrics(50), 0x10);
            if (_icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), Texts.T("Не удалось загрузить иконку трея."));
            _ownsIcon = true;
        }
        else _icon = TrayNative.LoadIcon(0, (nint)32512);
        var wc = new NativeMethods.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(), Procedure = _procedure,
            Instance = NativeMethods.GetModuleHandle(null), ClassName = _className
        };
        _class = NativeMethods.RegisterClassEx(ref wc);
        if (_class == 0) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        // Invisible top-level owner receives TaskbarCreated; message-only windows do not.
        _window = NativeMethods.CreateWindowEx(NativeMethods.ToolWindow | NativeMethods.NoActivate,
            _className, "DesktopOrganizer tray", 0x80000000, 0, 0, 0, 0, 0, 0, wc.Instance, 0);
        if (_window == 0) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        Owners.Add(_window, this);
        _taskbarCreated = TrayNative.RegisterWindowMessage("TaskbarCreated");
        try { AddIcon(); }
        catch { Dispose(); throw; }
    }

    private TrayNative.IconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<TrayNative.IconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4 | 0x80, CallbackMessage = Callback,
        Icon = _icon, Tip = "DesktopOrganizer",
        Info = "", InfoTitle = "", Version = 4
    };
    private void AddIcon()
    {
        var data = Data();
        if (!TrayNative.ShellNotifyIcon(0, ref data)) throw new Win32Exception(Texts.T("Не удалось добавить значок DesktopOrganizer в трей."));
        if (!TrayNative.ShellNotifyIcon(4, ref data))
        {
            TrayNative.ShellNotifyIcon(2, ref data);
            throw new Win32Exception(Texts.T("Не удалось настроить события значка DesktopOrganizer."));
        }
        _logger.LogInformation("DesktopOrganizer tray icon registered");
    }
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (!Owners.TryGetValue(window, out var owner)) return NativeMethods.DefWindowProc(window, message, wParam, lParam);
        try
        {
            if (message == owner._taskbarCreated) { owner.AddIcon(); return 0; }
            if (message == 0x111)
            {
                if (((long)wParam & 0xffff) == 1) owner.OpenRequested?.Invoke();
                else if (((long)wParam & 0xffff) == 2) owner.ExitRequested?.Invoke();
                return 0;
            }
            if (message == Callback)
            {
                var notification = (uint)((long)lParam & 0xffff);
                if (notification is 0x400 or 0x401) owner.OpenRequested?.Invoke(); // NIN_SELECT / NIN_KEYSELECT: one activation.
                else if (notification == 0x7b) owner.ShowMenu();
                return 0;
            }
        }
        catch (Exception error) { owner._logger.LogError(error, "Tray interaction failed"); }
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }
    private void ShowMenu()
    {
        var menu = TrayNative.CreatePopupMenu();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        uint selected;
        try
        {
            if (!TrayNative.AppendMenu(menu, 0, 1, Texts.T("Открыть DesktopOrganizer")) ||
                !TrayNative.AppendMenu(menu, 0, 2, Texts.T("Выход"))) throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_window);
            selected = TrayNative.TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, _window, 0);
            NativeMethods.PostMessage(_window, 0, 0, 0);
        }
        finally { TrayNative.DestroyMenu(menu); }
        if (selected is 1 or 2) NativeMethods.PostMessage(_window, 0x111, (nint)selected, 0);
    }
    public void Dispose()
    {
        if (_window != 0)
        {
            var data = Data(); TrayNative.ShellNotifyIcon(2, ref data);
            Owners.Remove(_window); NativeMethods.DestroyWindow(_window); _window = 0;
        }
        if (_class != 0) { NativeMethods.UnregisterClass(_className, NativeMethods.GetModuleHandle(null)); _class = 0; }
        if (_ownsIcon && _icon != 0) TrayNative.DestroyIcon(_icon);
        _icon = 0; _ownsIcon = false;
        GC.KeepAlive(_procedure);
    }
}

internal static class TrayNative
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyIcon(nint icon);
    [StructLayout(LayoutKind.Sequential)] internal struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    internal static extern int GetIconRect(ref IconIdentifier icon, out NativeMethods.Rect rect);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct IconData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShellNotifyIcon(uint message, ref IconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint LoadIcon(nint instance, nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] internal static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyMenu(nint menu);
}
