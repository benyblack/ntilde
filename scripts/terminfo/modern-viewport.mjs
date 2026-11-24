import { isDeepStrictEqual as same } from 'node:util';

export const scope = Object.freeze({ id: 'owned-modern-viewport-direct-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'mviewport-';
export const requirementId = 'scrollback.viewport-hold-output';
export const da = '\x1b[c';
export const clear = '\x1b[2J\x1b[3J\x1b[H';
export const syncSet = '\x1b[?2026h';
export const syncReset = '\x1b[?2026l';
export const phases = ['plain', 'synchronized'];
export const stages = ['bottom', 'anchor', 'output', 'restored'];
export const maxWheelNotches = 128;

export function requireViewport(condition, detail) {
    if (!condition) throw new Error('Modern viewport: ' + detail);
}

export function viewportFixture(runId, phase, rows) {
    const tag = `V-${runId}-${phase === 'plain' ? 'P' : 'S'}`;
    const lines = Array.from({ length: 3 * rows }, (_, index) => `${tag}-${String(index + 1).padStart(4, '0')}`);
    const marker = tag + '-MARKER';
    const seedBytes = clear + lines.join('\r\n') + '\r\n';
    const lineBytes = marker + '\r\n';
    return { lines, marker, seedBytes, markerBytes: phase === 'plain' ? lineBytes : syncSet + lineBytes + syncReset };
}

// History reads start at the oldest retained row. The App offset is distance above
// the live page. Project row identities using the same coordinate system as the view.
export function visibleRows({ history, live, offset }) {
    const first = history.length - offset;
    return history.concat(live).slice(first, first + live.length);
}

function isSeed(snapshot, expected) {
    return same(snapshot.history.concat(snapshot.live), [...expected, '']);
}

export function gradeViewportPhase({ phase, runId, bottom, anchor, output, restored }) {
    const rows = bottom.live.length;
    const { lines, marker } = viewportFixture(runId, phase, rows);
    const captures = { bottom, anchor, output, restored };
    const observed = Object.fromEntries(Object.entries(captures).map(([stage, capture]) => [stage, {
        offset: capture.offset, extent: capture.extent, visibleRows: visibleRows(capture), rowsEvicted: capture.rowsEvicted,
    }]));
    const grade = (conclusion, reason) => ({ phase, conclusion, reason, marker, ...observed });
    if (Object.values(captures).some(capture => capture.live.length !== rows || capture.extent !== capture.history.length
        || capture.generation !== bottom.generation || capture.rowsEvicted !== bottom.rowsEvicted)) {
        return grade('inconclusive', 'geometry or retained history changed unexpectedly');
    }
    if (bottom.offset !== 0 || !isSeed(bottom, lines) || !isSeed(anchor, lines)
        || anchor.offset < rows || same(observed.anchor.visibleRows, observed.bottom.visibleRows)) {
        return grade('inconclusive', 'numbered seed or inward viewport control failed');
    }
    const delivered = [...lines, marker];
    if (restored.offset !== 0 || !isSeed(output, delivered) || !isSeed(restored, delivered)
        || !observed.restored.visibleRows.includes(marker)) {
        return grade('inconclusive', 'marker delivery or bottom restoration failed');
    }
    if (same(observed.output.visibleRows, observed.anchor.visibleRows)) {
        return grade('pass', 'the same historical rows held at the same visible positions');
    }
    if (output.offset === 0 && same(observed.output.visibleRows, observed.restored.visibleRows)) {
        return grade('fail', 'the view snapped to the verified new live bottom');
    }
    return grade('inconclusive', 'visible historical rows drifted without reaching the new bottom');
}

export function gradeViewport(phases) {
    if (phases.some(phase => phase.conclusion === 'fail')) return 'fail';
    return phases.every(phase => phase.conclusion === 'pass') ? 'pass' : 'inconclusive';
}
