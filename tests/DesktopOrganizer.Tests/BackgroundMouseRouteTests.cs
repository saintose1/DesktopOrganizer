using DesktopOrganizer.Infrastructure.Interop;
using System.Runtime.InteropServices;

namespace DesktopOrganizer.Tests;

public sealed class BackgroundMouseRouteTests
{
    [Fact]
    public void BackgroundPressIsRoutedButCapturedExplorerGetsGenuineMotionAndRelease()
    {
        var route = new BackgroundMouseRoute();
        Assert.Equal(BackgroundMouseDecision.Forward, route.Observe(0x201, true, BackgroundMouseCapture.None));
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x200, true, BackgroundMouseCapture.None));
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x200, false, BackgroundMouseCapture.Explorer));
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x202, false, BackgroundMouseCapture.Explorer));
        Assert.False(route.Active);
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x200, true, BackgroundMouseCapture.None));
    }

    [Fact]
    public void FastClickDeliversReleaseEvenBeforeNativeCaptureStarts()
    {
        var route = new BackgroundMouseRoute();
        Assert.Equal(BackgroundMouseDecision.Forward, route.Observe(0x201, true, BackgroundMouseCapture.None));
        Assert.Equal(BackgroundMouseDecision.ForwardAndPass, route.Observe(0x202, false, BackgroundMouseCapture.None));
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x202, true, BackgroundMouseCapture.None));
    }

    [Theory]
    [InlineData(false, 0)] // An icon, Fence header/border, or another app.
    [InlineData(true, 1)] // Existing Explorer capture.
    [InlineData(true, 2)] // Existing capture in another window.
    [InlineData(false, 2)]
    public void ExistingNativeInputIsNeverIntercepted(bool background, int capture)
    {
        var route = new BackgroundMouseRoute();
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x201, background, (BackgroundMouseCapture)capture));
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x200, true, BackgroundMouseCapture.None));
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x202, true, BackgroundMouseCapture.None));
    }

    [Fact]
    public void DisconnectDoesNotLeakIntoTheNextGesture()
    {
        var route = new BackgroundMouseRoute();
        route.Observe(0x201, true, BackgroundMouseCapture.None);
        route.Reset();
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x200, true, BackgroundMouseCapture.None));
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x202, true, BackgroundMouseCapture.None));
    }

    [Fact]
    public void ANewPressOnInteractiveContentCancelsRouting()
    {
        var route = new BackgroundMouseRoute();
        route.Observe(0x201, true, BackgroundMouseCapture.None);
        Assert.Equal(BackgroundMouseDecision.Cancel, route.Observe(0x201, false, BackgroundMouseCapture.None));
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x200, true, BackgroundMouseCapture.None));
    }

    [Theory]
    [InlineData(0x202u)]
    public void ForeignCaptureCancelsWithoutSwallowingItsNextMouseEvent(uint message)
    {
        var route = new BackgroundMouseRoute();
        route.Observe(0x201, true, BackgroundMouseCapture.None);
        Assert.Equal(BackgroundMouseDecision.Cancel, route.Observe(message, false, BackgroundMouseCapture.Other));
        Assert.False(route.Active);
    }

    [Theory]
    [InlineData(0x204u)] // Right button.
    [InlineData(0x207u)] // Middle button.
    [InlineData(0x20Bu)] // X button.
    public void AnotherButtonCancelsBeforeItsOrdinaryEventIsDelivered(uint message)
    {
        var route = new BackgroundMouseRoute();
        route.Observe(0x201, true, BackgroundMouseCapture.None);
        Assert.Equal(BackgroundMouseDecision.Cancel, route.Observe(message, false, BackgroundMouseCapture.Explorer));
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x202, true, BackgroundMouseCapture.None));
    }

    [Fact]
    public void MovingAcrossAFenceGripOrIconDoesNotStealAnExistingSelectionGesture()
    {
        var route = new BackgroundMouseRoute();
        route.Observe(0x201, true, BackgroundMouseCapture.None);
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x200, false, BackgroundMouseCapture.None));
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x200, false, BackgroundMouseCapture.Explorer));
        Assert.Equal(BackgroundMouseDecision.ForwardAndPass, route.Observe(0x202, false, BackgroundMouseCapture.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RegressionMouseMovementIsNeverSuppressedEvenWhileSelecting(int capture)
    {
        var route = new BackgroundMouseRoute();
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x200, true, (BackgroundMouseCapture)capture));
        route.Observe(0x201, true, BackgroundMouseCapture.None);
        for (var i = 0; i < 10; i++)
            Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x200, true, (BackgroundMouseCapture)capture));
        Assert.True(route.Active);
    }

    [Fact]
    public void WheelIsPassedThroughWithoutStartingOrTerminatingSelection()
    {
        var route = new BackgroundMouseRoute();
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x20A, true, BackgroundMouseCapture.None));
        Assert.False(route.Active);
        route.Observe(0x201, true, BackgroundMouseCapture.None);
        Assert.Equal(BackgroundMouseDecision.Pass, route.Observe(0x20A, false, BackgroundMouseCapture.Explorer));
        Assert.True(route.Active);
    }

    [Theory]
    [InlineData(0x201u, false, false, 1)]
    [InlineData(0x200u, true, false, 5)]
    [InlineData(0x200u, false, true, 9)]
    [InlineData(0x202u, true, true, 12)]
    [InlineData(0x202u, false, false, 0)]
    public void ButtonAndSelectionModifiersUseEventStateRatherThanAsyncButtonPolling(uint message, bool shift, bool control, int expected)
        => Assert.Equal(expected, MouseMessagePoint.Keys(message, shift, control));

    [Fact]
    public void NativeHookPacketPreservesPerMonitorCoordinatesAndPointerWidth()
    {
        var packet = new NativeMethods.MouseHookData { Position = new(-1920, 1250), Flags = 1, Time = uint.MaxValue, Extra = nuint.MaxValue };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MouseHookData>());
        try
        {
            Marshal.StructureToPtr(packet, memory, false);
            var restored = Marshal.PtrToStructure<NativeMethods.MouseHookData>(memory);
            Assert.Equal(-1920, restored.Position.X); Assert.Equal(1250, restored.Position.Y);
            Assert.Equal(packet.Extra, restored.Extra);
            Assert.Equal(packet.Time, restored.Time);
            Assert.Equal(IntPtr.Size == 8 ? 32 : 24, Marshal.SizeOf<NativeMethods.MouseHookData>());
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Theory]
    [InlineData(-2000, -300)]
    [InlineData(600, 1600)]
    [InlineData(-32768, 32767)]
    public void MouseMessageCoordinatesPreserveSignedMonitorPositions(int x, int y)
    {
        Assert.True(MouseMessagePoint.TryPack(x, y, out var parameter));
        var result = MouseMessagePoint.Unpack(unchecked((uint)(long)parameter));
        Assert.Equal(x, result.X); Assert.Equal(y, result.Y);
    }

    [Theory]
    [InlineData(32768, 0)]
    [InlineData(0, -32769)]
    public void UnrepresentableCoordinatesAreRejectedRatherThanWrapped(int x, int y)
        => Assert.False(MouseMessagePoint.TryPack(x, y, out _));
}
