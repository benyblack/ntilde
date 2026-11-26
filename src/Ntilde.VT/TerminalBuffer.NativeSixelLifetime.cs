namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>Headless sources have no native callbacks; dispose only those owned handles after leaving all VT locks.</summary>
    internal void DisposeRetiredHeadlessSixelSources()
    {
        if (NativeSixelBudget is null || _retiredImageHandles.IsEmpty || Lock.IsReadLockHeld || Lock.IsWriteLockHeld) return;
        var retired = new List<object>();
        DrainRetiredImageHandles(retired, long.MaxValue, headlessSourcesOnly: true);
        foreach (var handle in retired)
        {
            // The filtered drain never hands a renderer's native/opaque owner to this consumer.
            try { ((NativeSixelSourceOwner)handle).Dispose(); }
            catch { RetryRetiredImageDisposal(handle); }
        }
    }
}
