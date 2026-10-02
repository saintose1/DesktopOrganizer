using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace DesktopOrganizer.Infrastructure.Interop;

[SupportedOSPlatform("windows")]
internal sealed class NativeStaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _work = [];
    private readonly Thread _thread;
    public NativeStaWorker(Action cleanup)
    {
        _thread = new Thread(() => { try { foreach (var action in _work.GetConsumingEnumerable()) action(); } finally { cleanup(); } })
            { Name = "DesktopOrganizer Shell STA", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    public Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); return; }
                try { completion.TrySetResult(action()); } catch (Exception error) { completion.TrySetException(error); }
            }, cancellationToken);
        }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }
    public void Dispose()
    {
        if (_work.IsAddingCompleted) return;
        _work.CompleteAdding();
        // Buffers are released on the worker after synchronous messages return, never prematurely.
        if (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(2))) _work.Dispose();
    }
}
