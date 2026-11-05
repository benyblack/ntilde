using System.Globalization;

namespace Ntilde.VT
{
    public partial class AnsiParser
    {
        /// <summary>
        /// Sixel color registers an image may define: 0 to 4095, as the Sixel decoder accepts. The count
        /// is fixed; XTSMGRAPHICS reports it and refuses to change it.
        /// </summary>
        public const int SixelColorRegisterCount = 4096;

        /// <summary>
        /// XTSMGRAPHICS, <c>CSI ? Pi ; Pa ; Pv S</c> (#872), following xterm's handler
        /// (<c>CASE_GRAPHICS_ATTRIBUTES</c>) with Ntilde's real Sixel limits.
        /// </summary>
        /// <remarks>
        /// Exactly three parameters separated by <c>;</c>; any other shape is not this request and
        /// gets no reply, as in xterm. The reply is <c>CSI ? Pi ; 0 ; Pv S</c> on success (two
        /// values for geometry) and <c>CSI ? Pi ; Ps S</c> otherwise: 1 for an unknown item, 2 for
        /// an unknown action, 3 for an action that fails.
        /// <list type="bullet">
        /// <item><c>Pi = 1</c>, color registers: read, reset and read-max report
        /// <see cref="SixelColorRegisterCount"/>; set succeeds only for that same count.</item>
        /// <item><c>Pi = 2</c>, Sixel geometry: read reports the text area in Sixel pixels (see
        /// <see cref="SixelAreaPixels"/>) capped at <see cref="SixelGeometry.DefaultMaxPixelDimension"/>,
        /// read-max reports the cap, and reset and set fail because the window, not the program,
        /// sets the size.</item>
        /// <item><c>Pi = 3</c> (ReGIS) and anything else: unknown item.</item>
        /// </list>
        /// Nothing changes state, so replay and state transfer are unaffected.
        /// </remarks>
        private void HandleXtsmgraphics(ReadOnlySpan<int> args, ReadOnlySpan<char> separators)
        {
            if (args.Length != 3 || separators[0] != ';' || separators[1] != ';') return;
            int item = args[0];
            (int status, string values) = item switch
            {
                1 => ColorRegisterAttribute(action: args[1], value: args[2]),
                2 => SixelGeometryAttribute(action: args[1]),
                _ => (XtsmgraphicsUnknownItem, ""),
            };
            OnResponse?.Invoke(status == XtsmgraphicsSuccess
                ? string.Create(CultureInfo.InvariantCulture, $"\x1b[?{item};0;{values}S")
                : string.Create(CultureInfo.InvariantCulture, $"\x1b[?{item};{status}S"));
        }

        // XTSMGRAPHICS reply statuses, as xterm numbers them.
        private const int XtsmgraphicsSuccess = 0;
        private const int XtsmgraphicsUnknownItem = 1;
        private const int XtsmgraphicsUnknownAction = 2;
        private const int XtsmgraphicsFailed = 3;

        /// <summary>Pi 1: the fixed register count; only a set to that same count succeeds.</summary>
        private static (int Status, string Values) ColorRegisterAttribute(int action, int value) => (
            action switch
            {
                1 or 2 or 4 => XtsmgraphicsSuccess,
                3 => value == SixelColorRegisterCount ? XtsmgraphicsSuccess : XtsmgraphicsFailed,
                _ => XtsmgraphicsUnknownAction,
            },
            SixelColorRegisterCount.ToString(CultureInfo.InvariantCulture));

        /// <summary>Pi 2: read and read-max succeed; the window, not the program, sets the size.</summary>
        private (int Status, string Values) SixelGeometryAttribute(int action)
        {
            int cap = SixelGeometry.DefaultMaxPixelDimension;
            (int width, int height) = action == 1 ? SixelAreaPixels() : (cap, cap);
            int status = action switch
            {
                1 or 4 => XtsmgraphicsSuccess,
                2 or 3 => XtsmgraphicsFailed,
                _ => XtsmgraphicsUnknownAction,
            };
            return (status, string.Create(CultureInfo.InvariantCulture, $"{Math.Min(width, cap)};{Math.Min(height, cap)}"));
        }

        /// <summary>
        /// The text area measured in Sixel pixels: columns and rows times the cell size images are
        /// placed in (<see cref="ImageCellPixels"/>, device pixels on a scaled display). An image this
        /// wide spans exactly the pane's columns. It is the same area <c>CSI 14 t</c> reports;
        /// <see cref="SixelGeometryAttribute"/> then caps it at the decoder's clip.
        /// </summary>
        private (int Width, int Height) SixelAreaPixels()
        {
            var (cw, ch) = ImageCellPixels();
            return (Math.Max(1, (int)Math.Round(_buffer.Cols * cw)), Math.Max(1, (int)Math.Round(_buffer.Rows * ch)));
        }
    }
}
