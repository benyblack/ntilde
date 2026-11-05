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

    private bool TryAdmitImageHandle(object handle)
    {
        NativeSixelAllocation? allocation = (handle as INativeSixelImage)?.Allocation;
        if (allocation is not null && !ReferenceEquals(allocation.Owner, NativeSixelBudget)) return false;
        lock (_imageHandleGate)
        {
            ImageHandleLifetime lifetime = _imageHandleLifetimes.GetValue(handle, static _ => new ImageHandleLifetime());
            if (lifetime.HandedOff || (allocation is not null && !allocation.Owner.TryAdmitPlacement(allocation))) return false;
            lifetime.MayBeShared |= lifetime.Admitted;
            lifetime.Admitted = true;
            lifetime.Pending = false;
            return true;
        }
    }

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

    private void QueueImageHandleRetirement(object handle, bool disposeImmediately)
    {
        lock (_imageHandleGate)
        {
            ImageHandleLifetime lifetime = _imageHandleLifetimes.GetValue(handle, static _ => new ImageHandleLifetime());
            if (lifetime.HandedOff || lifetime.Pending) return;
            lifetime.Pending = true;
            lifetime.RetirementToken = unchecked(lifetime.RetirementToken + 1);
            if (lifetime.RetirementToken == 0) lifetime.RetirementToken++;
            _retiredImageHandles.Enqueue((handle, Environment.TickCount64, disposeImmediately, lifetime.RetirementToken));
        }
    }

    private bool IsCurrentImageRetirement(object handle, long token) =>
        _imageHandleLifetimes.TryGetValue(handle, out ImageHandleLifetime? lifetime)
        && lifetime.Pending && !lifetime.HandedOff && lifetime.RetirementToken == token;

    private bool TryCompleteImageRetirement(object handle, long token)
    {
        if (!IsCurrentImageRetirement(handle, token)) return false;
        ImageHandleLifetime lifetime = _imageHandleLifetimes.GetValue(handle, static _ => new ImageHandleLifetime());
        lifetime.Pending = false;
        lifetime.HandedOff = true;
        return true;
    }
}
