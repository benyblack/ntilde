// Parent-side observer of the selection-color qualification: a thin parameterization of the runtime-color reset
// observer. Every read journals one complete, coherent state (stored cells and attributes, the App's resolved colors
// and override metadata, cursor and its mode) bracketing one controlled offscreen render of the established
// render-only synthetic selection (2,4)..(4,10) (the whole render theme, the presentation phase and the original PNG),
// together with the exact query replies the worker received, each bound to the DA barrier that ordered it. The render
// must describe the very state observed and may not change it; the real UI selection is never touched. Selection
// colors are session-wide, so the suite starts from a pristine private theme and may only finish when the original
// state, theme and PNG return.
import { readFileSync, lstatSync } from 'node:fs';
import { join } from 'node:path';
import { validateColorCapture } from './color-capture.mjs';
import { pngHash } from './pixels-capture.mjs';
import { selectionColorsReadPlan, selectionColorsSelection } from './selection-colors-contracts.mjs';

const directory = 'selc-pixels';

/** A retained render: the original PNG bytes revalidated against the frame that carries them, refusing symlinks and changed bytes. */
export function retainedSelectionColorsCapture(output, frame) {
    const pixel = frame?.pixel;
    if (!pixel || !new RegExp(String.raw`^${directory}/frame-\d{3}\.png$`).test(pixel.file ?? '') || !/^[a-f\d]{64}$/.test(pixel.sha256 ?? '')) throw new Error('Invalid retained selection-color capture.');
    const path = join(output, pixel.file), entry = lstatSync(path);
    if (!entry.isFile() || entry.isSymbolicLink() || entry.size > 5 * 1024 * 1024) throw new Error('Invalid retained selection-color capture file.');
    const bytes = readFileSync(path);
    if (bytes.length !== pixel.byteCount || pngHash(bytes) !== pixel.sha256) throw new Error('Retained selection-color PNG bytes changed.');
    return validateColorCapture({ ...pixel, pngBase64: bytes.toString('base64'), renderDetails: pixel.renderDetails }, frame.screen, selectionColorsSelection);
}

