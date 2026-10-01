using System.Globalization;
using Ntilde.Mux.Contracts;

namespace Ntilde.Shell.Mux;

/// <summary>One line of the "Attach to session…" picker (spec §7.2).</summary>
internal sealed record MuxSessionPickerRow(
    Guid SessionId, string Title, string Command, string? Cwd, int Cols, int Rows,
    int AttachedClients, bool Running, int? ExitCode, bool OpenHere)
{
    public string State => Running ? "running" : $"exited {ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}";

    public string Display
    {
        get
        {
            string title = string.IsNullOrWhiteSpace(Title) ? Command : Title;
            string where = string.IsNullOrEmpty(Cwd) ? string.Empty : "  " + Cwd;
            string attached = AttachedClients switch
            {
                0 => "detached",
                _ => string.Create(CultureInfo.InvariantCulture, $"{AttachedClients} attached"),
            };
            string here = OpenHere ? "  ·  open here" : string.Empty;
            return string.Create(CultureInfo.InvariantCulture, $"{title}  —  {Command}{where}  ·  {Cols}x{Rows}  ·  {attached}  ·  {State}{here}");
        }
    }
}

internal static class MuxSessionPicker
{
    /// <summary>Running first, then by title; faulted sessions cannot be attached and are left out.</summary>
    public static IReadOnlyList<MuxSessionPickerRow> BuildRows(IEnumerable<SessionSummary> sessions, IReadOnlySet<Guid> openHere) =>
        sessions
            .Where(s => !s.Faulted)
            .OrderByDescending(s => s.Running)
            .ThenBy(s => string.IsNullOrWhiteSpace(s.Title) ? s.Command : s.Title, StringComparer.OrdinalIgnoreCase)
            .Select(s => new MuxSessionPickerRow(s.SessionId, s.Title, s.Command, s.Cwd, s.Cols, s.Rows, s.AttachedClients, s.Running, s.ExitCode, openHere.Contains(s.SessionId)))
            .ToList();
}
