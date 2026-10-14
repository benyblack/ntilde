using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Core;

/// <summary>
/// The "Attach to session…" picker's seam (<c>MainWindow.PickMuxSession</c>) for the window tests: a choice made the way a
/// user makes it, from the rows offered.
/// </summary>
internal static class MuxPickerChoice
{
    /// <summary>Chooses the row of session <paramref name="id"/> on <paramref name="endpoint"/> (the local daemon by default); cancels when none is offered.</summary>
    public static Func<IReadOnlyList<MuxPickerItem>, Task<MuxPickerItem?>> Session(Guid id, MuxEndpointId endpoint = default) =>
        items => Task.FromResult<MuxPickerItem?>(Find(items, id, endpoint));

    /// <summary>The row of session <paramref name="id"/> on <paramref name="endpoint"/> among <paramref name="items"/>, or null.</summary>
    public static MuxSessionPickerRow? Find(IReadOnlyList<MuxPickerItem> items, Guid id, MuxEndpointId endpoint = default) =>
        items.OfType<MuxSessionPickerRow>().FirstOrDefault(r => r.Endpoint == endpoint && r.SessionId == id);
}
