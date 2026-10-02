using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Desktop;

public sealed record DesktopConnection(bool Connected, int SurfaceCount, string Diagnostics, string? Error = null, string IconStatus = "");
public sealed class FenceStateChangeRequest(IReadOnlyList<Fence> fences) : EventArgs
{
    public IReadOnlyList<Fence> Fences { get; } = fences;
    public bool Accepted { get; set; }
}

public interface IDesktopIntegration
{
    DesktopConnection Connection { get; }
    bool IsPresentingTransition => false;
    event EventHandler? ConnectionChanged;
    event EventHandler<FenceInteractionEventArgs>? Interaction;
    event EventHandler<FenceStateChangeRequest>? StateChangeRequested;
    Fence NormalizeFence(Fence original, Fence proposed);
    void SetInteractionEnabled(bool enabled);
    void SetFences(IReadOnlyList<Fence> fences);
    void ShowDesktop();
}

public readonly record struct DesktopArea(int X, int Y, int Width, int Height);

/// <summary>Visible pixels only. Coordinates in the model are virtual-screen pixels.</summary>
public readonly record struct FenceSurfaceLayout(int X, int Y, int Width, int Height)
{
    public static FenceSurfaceLayout? Clip(Fence fence, DesktopArea area, double scale = 1)
    {
        if (area.Width <= 0 || area.Height <= 0) return null;
        var left = Math.Max(fence.X, area.X);
        var top = Math.Max(fence.Y, area.Y);
        var right = Math.Min(fence.X + fence.Width, (double)area.X + area.Width);
        var bottom = Math.Min(fence.Y + FenceInteractionGeometry.VisibleHeight(fence, scale), (double)area.Y + area.Height);
        if (right <= left || bottom <= top) return null;
        var x = (int)Math.Floor(left);
        var y = (int)Math.Floor(top);
        return new(x, y, (int)Math.Ceiling(right) - x, (int)Math.Ceiling(bottom) - y);
    }
}
