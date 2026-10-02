using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.App.Desktop;

/// <summary>Visual hide/show completes before durable state advances. Failed commits restore the original presentation.</summary>
public static class FenceVisualTransaction
{
    public static async Task ExecuteAsync(Fence before, Fence after, Func<Fence, Task> present, Func<Task<bool>> commit)
    {
        try
        {
            await present(after);
            if (!await commit()) throw new InvalidOperationException("Fence state could not be saved; presentation restored.");
        }
        catch (Exception failure)
        {
            try { await present(before); }
            catch (Exception rollback) { throw new AggregateException("Fence presentation and rollback failed.", failure, rollback); }
            throw;
        }
    }
}
