// Deliberately blank synthetic images: useful to prove missing ink cannot become a pass.
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pixelCapture, pixelScreen, pixelPaneId } from './pixel-capture.mjs';
import { pixelPlan } from '../pixels-cases.mjs';
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
import { pngHash } from '../pixels-capture.mjs';
export function syntheticPixelJournal(output, runId) {
    mkdirSync(join(output, 'pixels'));
    const steps = [{ kind: 'pixel-start', timeMs: 0 }, { kind: 'pixel-bind', timeMs: 0 },
        ...pixelPlan.map(plan => ({ kind: 'pixel-read', ...plan })), { kind: 'pixel-finish', timeMs: 0 }];
    return steps.map((step, index) => {
        const screen = screenSnapshot(pixelScreen(), false, true, true, true);
        if (step.kind === 'pixel-bind') { screen.lines[0] = `NTILDE_PX_${runId}`; screen.cursorCol = 42; }
        const result = pixelCapture(screen, step.timeMs), { pngBase64, ...pixel } = result;
        const bytes = Buffer.from(pngBase64, 'base64');
        pixel.file = `pixels/frame-${String(index).padStart(3, '0')}.png`; pixel.sha256 = pngHash(bytes);
        writeFileSync(join(output, pixel.file), bytes);
        return { index, kind: step.kind, paneId: pixelPaneId, barrier: index ? '\x1b[?62;4;22c' : null,
            screen, sha256: snapshotHash(screen), afterScreen: structuredClone(screen), afterSha256: snapshotHash(screen), sceneId: step.sceneId ?? null, pixel };
    });
}
