using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopOrganizer.Infrastructure.Interop;

namespace DesktopOrganizer.Tests;

/// <summary>Native property ordering on our own hidden HWND; this does not verify Explorer wallpaper pixels.</summary>
public sealed class ExplorerCompositionPropertiesTests
{
    [Fact]
    public void DarkBackgroundIsNeverExposedBeforeItsColorKeyIsInstalled()
    {
        WithOwnedWindow((window, writes) =>
        {
            ExplorerIconLayer.ApplyFenceProperties(window);

            var background = Assert.Single(writes, w => w.Message == 0x1001);
            Assert.Equal(0x030201u, background.Color);
            Assert.True(background.Layered);
            Assert.True(background.KeyConfigured);
            Assert.Equal(background.Color, background.Key);
        });
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu)] // Explorer wallpaper-backed transparent colors.
    [InlineData(0xA06020u, 0x907030u)] // Preserve an original configured background, too.
    public void RestorePutsOriginalBackgroundBackBeforeRemovingLayeredBacking(uint originalBackground, uint originalTextBackground)
    {
        WithOwnedWindow((window, writes) =>
        {
            ExplorerIconLayer.ApplyFenceProperties(window);
            writes.Clear();
            ExplorerIconLayer.RestoreProperties(window, NativeMethods.ToolWindow, 0x00400000, originalBackground, originalTextBackground);

            var background = Assert.Single(writes, w => w.Message == 0x1001);
            var text = Assert.Single(writes, w => w.Message == 0x1026);
            Assert.Equal(originalBackground, background.Color);
            Assert.Equal(originalTextBackground, text.Color);
            Assert.True(background.Layered);
            Assert.True(text.Layered);
            Assert.Equal(0u, (uint)NativeMethods.GetWindowStyle(window, -20) & NativeMethods.Layered);
            Assert.NotEqual(0u, (uint)NativeMethods.GetWindowStyle(window, -20) & NativeMethods.ToolWindow);
        });
    }

    [Fact]
    public void RepeatedRestoreDoesNotReintroduceTheDarkKeyOrRemoveUnrelatedStyles()
    {
        WithOwnedWindow((window, writes) =>
        {
            var originalStyles = (uint)NativeMethods.GetWindowStyle(window, -20);
            ExplorerIconLayer.ApplyFenceProperties(window);
            ExplorerIconLayer.RestoreProperties(window, NativeMethods.ToolWindow, 0, uint.MaxValue, uint.MaxValue);
            writes.Clear();
            ExplorerIconLayer.RestoreProperties(window, NativeMethods.ToolWindow, 0, uint.MaxValue, uint.MaxValue);

            Assert.All(writes.Where(w => w.Message is 0x1001 or 0x1026), w => Assert.Equal(uint.MaxValue, w.Color));
            Assert.Equal(originalStyles, (uint)NativeMethods.GetWindowStyle(window, -20));
        });
    }

    [Fact]
    public void InvalidWindowPropertyFailureIsReported()
    {
        Assert.Throws<Win32Exception>(() => ExplorerIconLayer.ApplyFenceProperties(0));
        Assert.Throws<Win32Exception>(() => ExplorerIconLayer.RestoreProperties(0, 0, 0, uint.MaxValue, uint.MaxValue));
    }

    [Fact]
    public void HealthyLeaseIsNotReappliedDuringObservation()
    {
        WithOwnedWindow((window, writes) =>
        {
            ExplorerIconLayer.ApplyFenceProperties(window);
            writes.Clear();
            Assert.False(ExplorerIconLayer.EnsureFenceProperties(window));
            Assert.Empty(writes);
        });
    }

    [Theory]
    [InlineData(0u)] // Opaque black backing after a desktop refresh.
    [InlineData(0xFFFFFFFFu)] // Original Explorer transparent/wallpaper-backed state.
    public void DesktopRefreshBackgroundResetRepairsOwnedKeyBeforePainting(uint refreshedColor)
    {
        WithOwnedWindow((window, writes) =>
        {
            ExplorerIconLayer.ApplyFenceProperties(window);
            NativeMethods.SendMessage(window, 0x1001, 0, (nint)refreshedColor);
            writes.Clear();
            Assert.True(ExplorerIconLayer.EnsureFenceProperties(window));
            var repaired = Assert.Single(writes, w => w.Message == 0x1001);
            Assert.Equal(0x030201u, repaired.Color);
            Assert.True(repaired.KeyConfigured);
            writes.Clear();
            Assert.False(ExplorerIconLayer.EnsureFenceProperties(window));
            Assert.Empty(writes);
        });
    }

    [Fact]
    public void ExplorerRemovingOurLayeredStyleCanBeRepaired()
    {
        WithOwnedWindow((window, writes) =>
        {
            ExplorerIconLayer.ApplyFenceProperties(window);
            NativeMethods.SetWindowStyle(window, -20, (nint)NativeMethods.ToolWindow);
            writes.Clear();
            Assert.True(ExplorerIconLayer.EnsureFenceProperties(window));
            Assert.True(Assert.Single(writes, w => w.Message == 0x1001).KeyConfigured);
        });
    }

    [Fact]
    public void NewItemRefreshRestoringParentPaintAndNativeBackgroundRepairsTheWholeOwnedComposition()
    {
        WithOwnedWindow((window, writes) =>
        {
            ExplorerIconLayer.ApplyFenceProperties(window);
            NativeMethods.SendMessage(window, 0x1036, 0x00410000, 0x00410000);
            NativeMethods.SendMessage(window, 0x1001, 0, -1);
            writes.Clear();

            Assert.True(ExplorerIconLayer.EnsureFenceProperties(window));
            Assert.Equal(0x00010000u, (uint)NativeMethods.SendMessage(window, 0x1037, 0, 0) & 0x00410000);
            Assert.Equal(0x030201u, (uint)NativeMethods.SendMessage(window, 0x1000, 0, 0));
            Assert.True(Assert.Single(writes, w => w.Message == 0x1001).KeyConfigured);
            writes.Clear();
            Assert.False(ExplorerIconLayer.EnsureFenceProperties(window));
            Assert.Empty(writes);
        });
    }

    [Fact]
    public void ParentPaintRepairPreservesUnrelatedExplorerListViewStyles()
    {
        WithOwnedWindow((window, _) =>
        {
            ExplorerIconLayer.ApplyFenceProperties(window);
            const uint unrelated = 0x00000020;
            NativeMethods.SendMessage(window, 0x1036, (nint)(0x00410000 | unrelated), (nint)(0x00410000 | unrelated));
            Assert.True(ExplorerIconLayer.EnsureFenceProperties(window));
            Assert.Equal(unrelated | 0x00010000u, (uint)NativeMethods.SendMessage(window, 0x1037, 0, 0));
        });
    }

    [Fact]
    public void ForeignLayeredAttributesAreNotOverwritten()
    {
        WithOwnedWindow((window, writes) =>
        {
            ExplorerIconLayer.ApplyFenceProperties(window);
            Assert.True(NativeMethods.SetLayeredWindowAttributes(window, 0, 150, 2));
            writes.Clear();
            Assert.Throws<InvalidOperationException>(() => ExplorerIconLayer.EnsureFenceProperties(window));
            Assert.Throws<InvalidOperationException>(() => ExplorerIconLayer.RestoreProperties(window, 0, 0, uint.MaxValue, uint.MaxValue));
            Assert.Empty(writes);
            Assert.True(NativeMethods.GetLayeredWindowAttributes(window, out _, out var alpha, out var flags));
            Assert.Equal(150, alpha);
            Assert.Equal(2u, flags);
        });
    }

    private readonly record struct ColorWrite(uint Message, uint Color, bool Layered, bool KeyConfigured, uint Key);

    private static void WithOwnedWindow(Action<nint, List<ColorWrite>> test)
    {
        var writes = new List<ColorWrite>();
        uint background = uint.MaxValue, textBackground = uint.MaxValue, listStyles = 0;
        NativeMethods.WindowProc procedure = (window, message, wParam, lParam) =>
        {
            if (message == 0x1000) return (nint)background;
            if (message == 0x1025) return (nint)textBackground;
            if (message == 0x1037) return (nint)listStyles;
            if (message == 0x1036) listStyles = (listStyles & ~(uint)wParam) | ((uint)lParam & (uint)wParam);
            if (message is 0x1001 or 0x1026)
            {
                var configured = NativeMethods.GetLayeredWindowAttributes(window, out var key, out _, out var flags);
                writes.Add(new(message, unchecked((uint)(long)lParam),
                    ((uint)NativeMethods.GetWindowStyle(window, -20) & NativeMethods.Layered) != 0, configured && (flags & 1) != 0, key));
                if (message == 0x1001) background = unchecked((uint)(long)lParam);
                else textBackground = unchecked((uint)(long)lParam);
            }
            return NativeMethods.DefWindowProc(window, message, wParam, lParam);
        };
        var name = "DesktopOrganizer.CompositionOrderingTest." + Guid.NewGuid().ToString("N");
        var definition = new NativeMethods.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(), Procedure = procedure,
            Instance = NativeMethods.GetModuleHandle(null), ClassName = name
        };
        if (NativeMethods.RegisterClassEx(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var window = NativeMethods.CreateWindowEx(NativeMethods.ToolWindow, name, "", 0, 0, 0, 10, 10, 0, 0, definition.Instance, 0);
        try
        {
            if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            test(window, writes);
        }
        finally
        {
            if (window != 0) NativeMethods.DestroyWindow(window);
            NativeMethods.UnregisterClass(name, definition.Instance);
            GC.KeepAlive(procedure);
        }
    }
}
