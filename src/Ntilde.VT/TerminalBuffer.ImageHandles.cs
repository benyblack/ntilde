using System;
using System.Runtime.CompilerServices;

namespace Ntilde.VT;

public partial class TerminalBuffer
{
    // Track every opaque protocol handle, so cross-protocol reuse cannot bypass
    // cancellation or revive a handle already handed to the disposer. Weak keys
    // do not retain disposed decoder wrappers. No Dispose or I/O runs under the gate.
    private sealed class ImageHandleLifetime
    {
        internal long RetirementToken;
        internal bool Pending;
        internal bool HandedOff;
        internal bool Admitted;
        internal bool MayBeShared;
    }
    private readonly ConditionalWeakTable<object, ImageHandleLifetime> _imageHandleLifetimes = new();
    private readonly object _imageHandleGate = new();

    /// <summary>
    /// Admits live aliases under one lifetime gate; native final retirement and every disposal handoff are irreversible.
    /// Native placement credit must belong to this buffer before admission can cancel legacy pending retirement.
    /// </summary>
    private bool TryAdmitImageHandle(object handle)
    {
        NativeSixelAllocation? allocation = (handle as INativeSixelImage)?.Allocation;
        if (allocation is not null && !ReferenceEquals(allocation.Owner, NativeSixelBudget)) return false;
        lock (_imageHandleGate)
        {
            ImageHandleLifetime lifetime = _imageHandleLifetimes.GetValue(handle, static _ => new ImageHandleLifetime());
            // Final native retirement is irreversible, even before handoff: its source
            // has entered the retired tier. Live aliases remain shareable; legacy handles
            // retain their existing pending-retirement cancellation contract.
            if (lifetime.HandedOff || (allocation is not null && lifetime.Pending) ||
                (allocation is not null && !allocation.Owner.TryAdmitPlacement(allocation))) return false;
            lifetime.MayBeShared |= lifetime.Admitted;
            lifetime.Admitted = true;
            lifetime.Pending = false;
            return true;
        }
    }

    /// <summary>
    /// Rejects ownership or retirement conflicts before a caller publishes an imported or newly placed handle.
    /// </summary>
    private void RequireImageHandleAdmission(object handle)
    {
        if (!TryAdmitImageHandle(handle)) throw new InvalidOperationException("The image handle cannot be admitted to this buffer.");
    }

    private bool HasOtherImageReference(TerminalImage retiring)
    {
        // Normal decoders return fresh wrappers: do not scan a long image history
        // for a handle that has only ever been admitted once.
        if (!_imageHandleLifetimes.TryGetValue(retiring.ImageHandle, out ImageHandleLifetime? lifetime) || !lifetime.MayBeShared) return false;
        bool excludedRetiringPlacement = false;
        foreach (TerminalImage image in _images)
        {
            if (!ReferenceEquals(image.ImageHandle, retiring.ImageHandle)) continue;
            if (!excludedRetiringPlacement && ReferenceEquals(image, retiring))
            {
                excludedRetiringPlacement = true;
                continue;
            }
            return true;
        }
        return false;
    }

    private void ClearImagePlacements()
    {
        while (_images.Count > 0)
        {
            int index = _images.Count - 1;
            TerminalImage image = _images[index];
            _images.RemoveAt(index);
            RetireImage(image);
        }
    }

    /// <summary>
    /// Queues a handle at most once under the lifetime gate; neither queueing nor the immediate flag performs disposal.
    /// </summary>
    private void QueueImageHandleRetirement(object handle, bool disposeImmediately)
    {
        // An undecoded image (#838) owns nothing to dispose and is never shared, so it is simply
        // dropped. Queuing it would grow without bound where no renderer drains the queue: the
        // persistent-session daemon, headless replay.
        if (handle is UndecodedImage) return;
        lock (_imageHandleGate)
        {
            ImageHandleLifetime lifetime = _imageHandleLifetimes.GetValue(handle, static _ => new ImageHandleLifetime());
            if (lifetime.HandedOff || lifetime.Pending) return;
            EnqueueImageRetirement(handle, lifetime, disposeImmediately);
        }
    }

    /// <summary>
    /// Releases pixels VT refused before admitting them (#838: a decoded size that differs from the
    /// header). A handle this buffer has never admitted cannot be in a placement, a render snapshot or
    /// the retirement queue, so it is disposed now instead of waiting for a renderer drain that a hidden
    /// pane may never run; a handle it has seen (a decoder sharing handles) is retired as usual.
    /// </summary>
    internal void ReleaseRejectedImageHandle(object handle)
    {
        bool seen;
        lock (_imageHandleGate) seen = _imageHandleLifetimes.TryGetValue(handle, out _);
        if (seen) RetireUnstoredKittyHandle(handle);
        else (handle as IDisposable)?.Dispose();
    }

    /// <summary>Requeues a failed native disposal without reopening image admission or returning its charge.</summary>
    public bool RetryRetiredImageDisposal(object handle)
    {
        if (handle is not INativeSixelImage image) return false;
        NativeSixelAllocation allocation = image.Allocation;
        if (!ReferenceEquals(allocation.Owner, NativeSixelBudget)) return false;
        lock (_imageHandleGate)
        {
            if (!_imageHandleLifetimes.TryGetValue(handle, out var lifetime) || !lifetime.HandedOff || lifetime.Pending ||
                !allocation.Owner.IsActive(allocation)) return false;
            // HandedOff stays true: a retry can never cancel retirement or revive a placement.
            EnqueueImageRetirement(handle, lifetime, disposeImmediately: true);
            return true;
        }
    }

    /// <summary>
    /// Advances the retirement generation under the lifetime gate so stale drain entries cannot complete a later handoff.
    /// </summary>
    private void EnqueueImageRetirement(object handle, ImageHandleLifetime lifetime, bool disposeImmediately)
    {
        lifetime.Pending = true;
        lifetime.RetirementToken = unchecked(lifetime.RetirementToken + 1);
        if (lifetime.RetirementToken == 0) lifetime.RetirementToken++;
        _retiredImageHandles.Enqueue((handle, Environment.TickCount64, disposeImmediately, lifetime.RetirementToken));
    }

    /// <summary>
    /// Checks the pending retirement generation while the lifetime gate is held; a stale token cannot authorize disposal.
    /// </summary>
    private bool IsCurrentImageRetirement(object handle, long token) =>
        _imageHandleLifetimes.TryGetValue(handle, out ImageHandleLifetime? lifetime)
        && lifetime.Pending && lifetime.RetirementToken == token;

    private bool TryCompleteImageRetirement(object handle, long token)
    {
        if (!IsCurrentImageRetirement(handle, token)) return false;
        ImageHandleLifetime lifetime = _imageHandleLifetimes.GetValue(handle, static _ => new ImageHandleLifetime());
        lifetime.Pending = false;
        lifetime.HandedOff = true;
        return true;
    }
}
