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
            int item = args[0], action = args[1], value = args[2];
            const int Success = 0, UnknownItem = 1, UnknownAction = 2, Failed = 3;
            int status;
            string values = "";
            switch (item)
            {
                case 1:
                    status = action switch
                    {
                        1 or 2 or 4 => Success,
                        3 => value == SixelColorRegisterCount ? Success : Failed,
                        _ => UnknownAction,
                    };
                    values = SixelColorRegisterCount.ToString(CultureInfo.InvariantCulture);
                    break;
                case 2:
                    int cap = SixelGeometry.DefaultMaxPixelDimension;
                    (int width, int height) = action == 1 ? SixelAreaPixels() : (cap, cap);
                    status = action switch
                    {
                        1 or 4 => Success,
                        2 or 3 => Failed,
                        _ => UnknownAction,
                    };
                    values = string.Create(CultureInfo.InvariantCulture, $"{Math.Min(width, cap)};{Math.Min(height, cap)}");
                    break;
                default:
                    status = UnknownItem;
                    break;
            }
            OnResponse?.Invoke(status == Success
                ? string.Create(CultureInfo.InvariantCulture, $"\x1b[?{item};0;{values}S")
                : string.Create(CultureInfo.InvariantCulture, $"\x1b[?{item};{status}S"));
        }

        /// <summary>
        /// The text area measured in Sixel pixels: columns and rows times the cell size Sixel
        /// placement uses (<see cref="SixelCellSizeOverride"/>, else <see cref="CellWidth"/> and
        /// <see cref="CellHeight"/>, with the same fallbacks as <c>HandleSixel</c>). An image this
        /// wide spans exactly the pane's columns. On a scaled display the App sets the override in
        /// device pixels, so this can differ from <c>CSI 14 t</c>, which reports logical pixels.
        /// </summary>
        private (int Width, int Height) SixelAreaPixels()
        {
            SixelCellSize? size = SixelCellSizeOverride;
            float cellWidth = size?.WidthPx ?? CellWidth;
            float cellHeight = size?.HeightPx ?? CellHeight;
            float cw = float.IsFinite(cellWidth) && cellWidth > 0 ? Math.Max(1, cellWidth) : 10;
            float ch = float.IsFinite(cellHeight) && cellHeight > 0 ? Math.Max(1, cellHeight) : 20;
            return (Math.Max(1, (int)Math.Round(_buffer.Cols * cw)), Math.Max(1, (int)Math.Round(_buffer.Rows * ch)));
        }
    }
}
