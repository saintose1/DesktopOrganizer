using System.Reflection;
using System.Windows.Threading;
using DesktopOrganizer.App.Desktop;
using DesktopOrganizer.Core.Desktop;
using DesktopOrganizer.Infrastructure.Interop;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopOrganizer.Tests;

public sealed class ExplorerObservationTests
{
    [Fact]
    public async Task PressCancelsIdleReadWithoutConsumingTheNewGesture()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    // No connection to Explorer: this checks only coordinator scheduling/cancellation.
                    using var host = new Win32DesktopHost(new(NullLogger<DesktopDiscovery>.Instance), NullLogger<Win32DesktopHost>.Instance);
                    var icons = new PendingRead();
                    using var coordinator = new DesktopIconCoordinator(icons, host, NullLogger<DesktopIconCoordinator>.Instance, _ => throw new InvalidOperationException("No commit expected."));
                    coordinator.Start();
                    await icons.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    var notify = typeof(DesktopIconCoordinator).GetMethod("OnExplorerPointer", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .CreateDelegate<Action<ExplorerPointerEvent>>(coordinator);
                    notify(new(ExplorerPointerPhase.Pressed, new(10, 20), false, true)
                    { SelectionSnapshot = Task.FromResult(new DesktopSelection(["ordinary"], "ordinary")) });
                    Assert.True(icons.Token.IsCancellationRequested);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    Assert.Equal(ExplorerGesturePhase.Pressed, coordinator.ExplorerPhase);
                    notify(new(ExplorerPointerPhase.DragStarted, new(10, 20), false, true));
                    Assert.Equal(ExplorerGesturePhase.SingleDrag, coordinator.ExplorerPhase);
                    Assert.Equal(1, icons.Reads);
                    completed.SetResult();
                }
                catch (Exception error) { completed.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        thread.Join(TimeSpan.FromSeconds(1));
    }

    private sealed class PendingRead : IDesktopIconService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public int Reads { get; private set; }
        public async Task<DesktopIconSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++; Token = cancellationToken; Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The idle read must be cancelled.");
        }
        public Task PositionAsync(IReadOnlyList<DesktopIconTarget> targets, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Observation must not position icons.");
    }
}
