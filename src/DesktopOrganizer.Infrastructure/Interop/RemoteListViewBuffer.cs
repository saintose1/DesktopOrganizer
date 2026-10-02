using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;

namespace DesktopOrganizer.Infrastructure.Interop;

/// <summary>Data-only memory for documented ListView messages. No code injection/executable pages.</summary>
internal sealed class RemoteListViewBuffer : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly nint _memory;
    private readonly ILogger _logger;
    private readonly byte[] _scratch = new byte[4096];
    public nint Address(int offset) => offset is >= 0 and < 8192 ? _memory + offset : throw new ArgumentOutOfRangeException(nameof(offset));
    public RemoteListViewBuffer(uint pid, ILogger logger)
    {
        _logger = logger;
        _process = OpenProcess(0x0438, false, pid); // QUERY_INFORMATION | VM_READ | VM_WRITE | VM_OPERATION
        if (_process.IsInvalid) { _process.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!IsWow64Process(_process, out var wow64) || (Environment.Is64BitOperatingSystem && wow64 == Environment.Is64BitProcess))
        { _process.Dispose(); throw new NotSupportedException("Desktop icon reader requires matching Explorer pointer width."); }
        _memory = VirtualAllocEx(_process, 0, 8192, 0x3000, 4);
        if (_memory == 0) { _process.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    public void Write<T>(int offset, T value) where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        MemoryMarshal.Write(_scratch, in value);
        if (!WriteProcessMemory(_process, Address(offset), _scratch, (nuint)size, out var written) || written != (nuint)size)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public T Read<T>(int offset) where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        Read(offset, size);
        return MemoryMarshal.Read<T>(_scratch);
    }
    public string ReadText(int offset, int characters)
    {
        var bytes = Math.Clamp(characters, 0, 2047) * 2;
        Read(offset, bytes);
        return System.Text.Encoding.Unicode.GetString(_scratch, 0, bytes);
    }
    private void Read(int offset, int size)
    {
        if (size > _scratch.Length || offset + size > 8192) throw new ArgumentOutOfRangeException(nameof(size));
        if (!ReadProcessMemory(_process, Address(offset), _scratch, (nuint)size, out var read) || read != (nuint)size)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        if (!_process.IsClosed)
        {
            if (!VirtualFreeEx(_process, _memory, 0, 0x8000))
                _logger.LogWarning("Could not release desktop read buffer; Win32 error={Error}", Marshal.GetLastWin32Error());
            _process.Dispose();
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(SafeProcessHandle process, out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint read);
}
