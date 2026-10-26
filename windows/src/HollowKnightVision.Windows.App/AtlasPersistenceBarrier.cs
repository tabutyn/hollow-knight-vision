namespace HollowKnightVision.Windows.App;

internal static class AtlasPersistenceBarrier
{
    internal static async Task WaitForWorldWritesAsync(GameRuntimeMode mode, Task? pending)
    {
        // Normal tracking mutates only the in-memory mosaic. Persistence owns a
        // copied snapshot, so disk latency must not stop camera analysis. Modded
        // tracking also writes live-world files inside the directory being swapped
        // by publication and must finish that transaction before writing again.
        // Reset, load and shutdown still explicitly drain both modes.
        if (mode == GameRuntimeMode.Normal || pending is null) return;
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
            // The UI observes and reports the same task on the next frame or
            // during the mandatory shutdown/atlas-operation drain.
        }
    }
}
