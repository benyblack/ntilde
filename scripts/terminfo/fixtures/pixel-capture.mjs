// Synthetic wire/PNG fixture for portable refusal and ownership tests. Not renderer evidence.
import { crc32, deflateSync } from 'node:zlib';
export const pixelPaneId = '11111111-1111-1111-1111-111111111111';
export function pngChunk(type, data) {
    const result = Buffer.alloc(data.length + 12);
    result.writeUInt32BE(data.length); result.write(type, 4); data.copy(result, 8);
    result.writeUInt32BE(crc32(result.subarray(4, result.length - 4)), result.length - 4);
    return result;
}
export function syntheticPng(width = 600, height = 200) {
    const header = Buffer.alloc(13); header.writeUInt32BE(width); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 2;
    const raw = Buffer.alloc(height * (width * 3 + 1));
    return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), pngChunk('IHDR', header),
        pngChunk('IDAT', deflateSync(raw)), pngChunk('IEND', Buffer.alloc(0))]);
}
export function pixelScreen() {
    return { rows: 10, cols: 60, lines: Array(10).fill(''), cursorRow: 0, cursorCol: 0, cursorVisible: true,
        cursorMode: { style: 2, blink: true, isDefault: true }, cellDetails: [{ overline: false, count: 600, flags: 768,
            fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null }] };
}
export function pixelCapture(screen = pixelScreen(), timeMs = 0) {
    const bytes = syntheticPng();
    return { width: 600, height: 200, rows: screen.rows, cols: screen.cols, byteCount: bytes.length, downscaled: false,
        inlineOmitted: false, mode: 'render', scale: 1, pngBase64: bytes.toString('base64'), presentationTimeMs: timeMs,
        renderDetails: { schemaVersion: 1, platform: 'macos', architecture: 'arm64', enableLigatures: false, enableComplexShaping: false, minimumContrast: 1,
            font: { configuredFamily: 'fixture', primaryFamily: 'fixture', primarySha256: 'a'.repeat(64), collectionIndex: 0,
                weight: 400, width: 5, slant: 'Upright', fallbackFamilies: [], sizeDip: 14 },
            grid: { renderWidthPx: 600, renderHeightPx: 200, cellWidthDip: 10, cellHeightDip: 20, baselineDip: 15,
                originXPx: 0, originYPx: 0, cellWidthPx: 10, cellHeightPx: 20, baselineOffsetPx: 15, underlineOffsetPx: 17, strikeOffsetPx: 10 },
            theme: { foregroundArgb: 0xffffffff, backgroundArgb: 0xff000000, cursorArgb: 0xffffffff, cursorTextArgb: 0xff000000,
                ansiPaletteArgb: Array.from({ length: 256 }, (_, i) => 0xff000000 + i * 65536), foregroundOverride: false, backgroundOverride: false },
            presentation: { timeMs, halfPeriodMs: 530, textBlinkVisible: Math.floor(timeMs / 530) % 2 === 0,
                cursorVisible: screen.cursorVisible && Math.floor(timeMs / 530) % 2 === 0, cursorBlinkEnabled: true,
                cursorStyle: screen.cursorMode.style, cursorRow: screen.cursorRow, cursorCol: screen.cursorCol } } };
}
