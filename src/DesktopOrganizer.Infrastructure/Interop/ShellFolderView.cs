using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DesktopOrganizer.Infrastructure.Interop;

[SupportedOSPlatform("windows")]
internal sealed class ShellFolderView : IDisposable
{
    private object? _windows, _desktop;
    private IShellBrowser? _browser;
    private IShellView? _shellView;
    public IFolderView2 View { get; private set; } = null!;
    public ShellFolderView(DesktopHandles handles)
    {
        try
        {
            _windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!)!;
            dynamic windows = _windows;
            int desktopWindow = 0;
            _desktop = windows.FindWindowSW(0, null, 8, out desktopWindow, 1); // CSIDL_DESKTOP, SWC_DESKTOP, NEEDDISPATCH
            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
            var browserId = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(((IServiceProvider)_desktop!).QueryService(ref service, ref browserId, out var browser));
            _browser = (IShellBrowser)browser;
            Marshal.ThrowExceptionForHR(_browser.QueryActiveShellView(out _shellView));
            Marshal.ThrowExceptionForHR(_shellView.GetWindow(out var viewWindow));
            if (viewWindow != handles.DefView) throw new InvalidOperationException("Shell COM view does not match validated desktop DefView.");
            View = (IFolderView2)_shellView;
        }
        catch { Dispose(); throw; }
    }
    public static string Name(nint pidl, uint form)
    {
        Marshal.ThrowExceptionForHR(SHGetNameFromIDList(pidl, form, out var name));
        try { return Marshal.PtrToStringUni(name) ?? throw new InvalidOperationException("Shell returned an empty name."); }
        finally { Marshal.FreeCoTaskMem(name); }
    }
    public void Dispose()
    {
        // View and ShellView are the same RCW identity. Release it once.
        Release(_shellView); Release(_browser); Release(_desktop); Release(_windows);
        _shellView = null; _browser = null; _desktop = null; _windows = null;
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(nint pidl, uint form, out nint name);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
}
[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    [PreserveSig] int GetWindow(out nint window);
    [PreserveSig] int ContextSensitiveHelp(bool enter);
    [PreserveSig] int InsertMenus(nint menu, nint widths);
    [PreserveSig] int SetMenu(nint menu, nint oleMenu, nint activeWindow);
    [PreserveSig] int RemoveMenus(nint menu);
    [PreserveSig] int SetStatusText([MarshalAs(UnmanagedType.LPWStr)] string text);
    [PreserveSig] int EnableModeless(bool enable);
    [PreserveSig] int TranslateAccelerator(nint message, ushort id);
    [PreserveSig] int BrowseObject(nint pidl, uint flags);
    [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
    [PreserveSig] int GetControlWindow(uint id, out nint window);
    [PreserveSig] int SendControlMessage(uint id, uint message, nint wParam, nint lParam, out nint result);
    [PreserveSig] int QueryActiveShellView(out IShellView view);
}
[ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    [PreserveSig] int GetWindow(out nint window);
    [PreserveSig] int ContextSensitiveHelp(bool enter);
}
[ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    [PreserveSig] int GetCurrentViewMode(out uint mode);
    [PreserveSig] int SetCurrentViewMode(uint mode);
    [PreserveSig] int GetFolder(ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object folder);
    [PreserveSig] int Item(int index, out nint pidl);
    [PreserveSig] int ItemCount(uint flags, out int count);
    [PreserveSig] int Items(uint flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object items);
    [PreserveSig] int GetSelectionMarkedItem(out int index);
    [PreserveSig] int GetFocusedItem(out int index);
    [PreserveSig] int GetItemPosition(nint pidl, out NativeMethods.Point point);
    [PreserveSig] int GetSpacing(out NativeMethods.Point point);
    [PreserveSig] int GetDefaultSpacing(out NativeMethods.Point point);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int index, uint flags);
    [PreserveSig] int SelectAndPositionItems(uint count,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] nint[] pidls,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] NativeMethods.Point[] positions, uint flags);
    // Inherited IFolderView slots above must be repeated in the COM declaration.
    [PreserveSig] int SetGroupBy(nint key, bool ascending);
    [PreserveSig] int GetGroupBy(nint key, nint ascending);
    [PreserveSig] int SetViewProperty(nint pidl, nint key, nint value);
    [PreserveSig] int GetViewProperty(nint pidl, nint key, nint value);
    [PreserveSig] int SetTileViewProperties(nint pidl, [MarshalAs(UnmanagedType.LPWStr)] string properties);
    [PreserveSig] int SetExtendedTileViewProperties(nint pidl, [MarshalAs(UnmanagedType.LPWStr)] string properties);
    [PreserveSig] int SetText(uint type, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
    [PreserveSig] int GetCurrentFolderFlags(out uint flags);
    [PreserveSig] int GetSortColumnCount(out int count);
    [PreserveSig] int SetSortColumns(nint columns, int count);
    [PreserveSig] int GetSortColumns(nint columns, int count);
    [PreserveSig] int GetItem(int index, ref Guid iid, out nint item);
    [PreserveSig] int GetVisibleItem(int start, bool previous, out int index);
    [PreserveSig] int GetSelectedItem(int start, out int index);
    [PreserveSig] int GetSelection([MarshalAs(UnmanagedType.Bool)] bool noneImpliesFolder, out IShellItemArray selection);
    [PreserveSig] int GetSelectionState(nint pidl, out uint flags);
    [PreserveSig] int InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPStr)] string verb);
    [PreserveSig] int SetViewModeAndIconSize(uint mode, int size);
    [PreserveSig] int GetViewModeAndIconSize(out uint mode, out int size);
    [PreserveSig] int SetGroupSubsetCount(uint count);
    [PreserveSig] int GetGroupSubsetCount(out uint count);
    [PreserveSig] int SetRedraw([MarshalAs(UnmanagedType.Bool)] bool enabled);
}

[ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    [PreserveSig] int BindToHandler(nint context, ref Guid handler, ref Guid iid, out nint value);
    [PreserveSig] int GetPropertyStore(uint flags, ref Guid iid, out nint value);
    [PreserveSig] int GetPropertyDescriptionList(nint key, ref Guid iid, out nint value);
    [PreserveSig] int GetAttributes(uint flags, uint mask, out uint attributes);
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemAt(uint index, out IShellItem item);
    [PreserveSig] int EnumItems(out nint enumerator);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(nint context, ref Guid handler, ref Guid iid, out nint value);
    [PreserveSig] int GetParent(out IShellItem parent);
    [PreserveSig] int GetDisplayName(uint form, out nint name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
}
