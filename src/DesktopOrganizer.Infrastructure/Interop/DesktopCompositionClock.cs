using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DesktopOrganizer.Infrastructure.Interop;

public interface ICompositionFrameClock
{
    IDisposable Subscribe(Action<long> frame, Action<Exception> failed);
}

/// <summary>Waits for compositor presentation off the HWND thread, also when the WPF window is hidden.</summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopCompositionClock : ICompositionFrameClock
{
    public IDisposable Subscribe(Action<long> frame, Action<Exception> failed)
    {
        var subscription = new Subscription();
        var thread = new Thread(() =>
        {
            try
            {
                while (!subscription.Stopped)
                {
                    Marshal.ThrowExceptionForHR(DwmFlush());
                    if (!subscription.Stopped) frame(Stopwatch.GetTimestamp());
                }
            }
            catch (Exception error) { if (!subscription.Stopped) failed(error); }
        }) { IsBackground = true, Name = "DesktopOrganizer composition cadence" };
        thread.Start();
        return subscription;
    }
    private sealed class Subscription : IDisposable
    {
        private int _stopped;
        public bool Stopped => Volatile.Read(ref _stopped) != 0;
        public void Dispose() => Interlocked.Exchange(ref _stopped, 1);
    }
    [DllImport("dwmapi.dll", PreserveSig = true)] private static extern int DwmFlush();
}
