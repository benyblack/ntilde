// Independently specified basic OSC 22 setter effects. No native pointer-image, full Kitty,
// alternate-screen runtime, mux, replay, physical input or stock-score claim.
import { osc133Fixture } from './osc133-contracts.mjs';

export const osc22ContractsVersion = 1;
export const osc22SuiteId = 'ntilde-owned-osc22-graded-v1';
export const osc22EffectContractId = 'ntilde-osc22-app-cursor-effect-v1';
export const osc22Prefix = 'ptr';
export const osc22CaseId = 'extensions.osc22-pointer';
export const osc22CaseIds = Object.freeze([osc22CaseId]);
export const osc22Minimum = Object.freeze({ rows: 12, cols: 48 });
export const osc22PinnedWrites = Object.freeze(['\x1b[1;1H\x1b[2K', '\x1b]22;pointer\x07']);

const osc = value => `\x1b]22;${value}\x07`;
const expected = (requested, controlCursor) => Object.freeze({ requested, controlCursor, linkOverride: false });
export const osc22Baseline = expected(null, 'Arrow');

/** Literal requested/control pairs come from the protocol names and Avalonia's StandardCursorType,
 * not from the production request mapper or the observations. Each experiment resets the fixture
 * and prepares text first, so an ignored pointer/crosshair/default/reset cannot look successful. */
export function makeOsc22Experiments(runId) {
    const fixture = osc133Fixture(runId);
    const experiment = (name, role, writes, request, cursor) => ({ caseId: `${osc22CaseId}/${name}`, name, role,
        expected: expected(request, cursor), plan: [{ feed: fixture.stage + osc('text') },
            ...writes.map(feed => ({ feed })), { read: 'effect' }] });
    return [
        experiment('delivery-text', 'control', [], 'text', 'Ibeam'),
        experiment('stock', 'stock', [...osc22PinnedWrites], 'pointer', 'Hand'),
        experiment('crosshair', 'required', [osc('crosshair')], 'crosshair', 'Cross'),
        experiment('text-st', 'required', [osc('pointer'), '\x1b]22;text\x1b\\'], 'text', 'Ibeam'),
        experiment('equals-pointer', 'required', [osc('=pointer')], 'pointer', 'Hand'),
        experiment('default', 'required', [osc('default')], 'default', 'Arrow'),
        experiment('reset', 'required', [osc('')], null, 'Arrow'),
        experiment('equals-reset', 'required', [osc('=')], null, 'Arrow'),
        experiment('unknown-control', 'control', [osc('not-a-shape')], 'text', 'Ibeam'),
        experiment('case-control', 'control', [osc('Pointer')], 'text', 'Ibeam'),
        experiment('stack-control', 'control', [osc('>pointer'), osc('<')], 'text', 'Ibeam'),
        experiment('query-control', 'control', [osc('?__current__')], 'text', 'Ibeam'),
        experiment('list-control', 'control', [osc('pointer,crosshair')], 'text', 'Ibeam'),
        experiment('length-control', 'control', [osc('x'.repeat(33))], 'text', 'Ibeam'),
        experiment('valid-after-invalid', 'required', [osc('bad'), osc('pointer')], 'pointer', 'Hand'),
    ];
}

/** Only for the existing stored-state oracle: pointer requests are not text/cursor mutations. */
export const withoutPointerSequences = text => text.replace(/\x1b\]22;[^\x07\x1b]*(?:\x07|\x1b\\)/g, '');

export function replayOsc22PinnedStatic(cursor) {
    const position = [cursor[0] + 1, cursor[1] + 1];
    return { caseId: osc22CaseId, termlessFunction: false, termless: null,
        terminal: { pass: false, response: `${position[0]};${position[1]}`,
            note: 'Cursor response does not measure OSC 22 pointer shape',
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query' } },
        writes: [...osc22PinnedWrites], cursor: position };
}
