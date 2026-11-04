using System;

namespace Ntilde.VT
{
    public class TerminalImage
    {
        public Guid ImageId { get; }
        public int CellX { get; set; }
        public int CellY { get; set; }
        public int CellWidth { get; }
        public int CellHeight { get; }
        public int ZIndex { get; set; }

        public bool IsSixelImage { get; internal set; }
        /// <summary>Rows irreversibly clipped from the bitmap at scrolling/page boundaries.</summary>
        public int ClipTopRows { get; internal set; }
        public int ClipBottomRows { get; internal set; }
        public int ClipRightColumns { get; internal set; }

        public SixelSource? SixelSource { get; internal set; }
        public object ImageHandle { get; internal set; }

        public bool IsSticky { get; set; } = true;

        /// <summary>
        /// True when the image was placed while the alternate screen was active.
        /// Alt-screen images use viewport-relative <see cref="CellY"/>; main-screen
        /// images use absolute rows (scrollback + viewport). Set by
        /// <c>TerminalBuffer.AddImage</c>.
        /// </summary>
        public bool IsAltScreenImage { get; set; }

        /// <summary>
        /// The Kitty graphics image id (<c>i=</c>) for this placement. Zero denotes an
        /// anonymous Kitty image; null identifies other image protocols. Retained Kitty
        /// data owns its opaque handle while placements borrow it. Image numbers
        /// (<c>I=</c>) and newest-number lookup belong to the retained asset registry.
        /// </summary>
        public uint? KittyImageId { get; set; }

        /// <summary>Optional Kitty placement identity, distinct from the retained image id.</summary>
        public uint? KittyPlacementId { get; internal set; }

        public TerminalImage(object imageHandle, int cellX, int cellY, int cellWidth, int cellHeight)
        {
            ImageId = Guid.NewGuid();
            ImageHandle = imageHandle ?? throw new ArgumentNullException(nameof(imageHandle));
            CellX = cellX;
            CellY = cellY;
            CellWidth = cellWidth;
            CellHeight = cellHeight;
        }
    }
}
