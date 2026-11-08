// Synthetic, fully valid SGR-attribute grading evidence for portable tests.
// Every frame, run-log entry and pinned result is produced through the same
// production validators used for real measurements; nothing here replaces the
// frozen real-App publication.
import { snapshotHash } from '../agent-observer.mjs';
import { sgrAttrsContracts, sgrAttrsReset, sgrAttrsFeeds, sgrAttrsFlagBits,
    sgrAttrsObservation, gradeSgrAttrsCase } from '../sgr-attrs-contracts.mjs';
import { replaySgrAttrsPinned } from '../sgr-attrs-grading.mjs';
import { sgrAttrsAdapterId } from '../sgr-attrs-grading-collector.mjs';
import { syntheticColorBinding } from './sgr-rgb-evidence.mjs';

/** Synthetic row count, comfortably above the observer's floor. */
export const sgrAttrsRows = 6;
/** Synthetic column count, comfortably above the observer's floor. */
export const sgrAttrsCols = 60;
/** Total synthetic cell count. */
const cells = sgrAttrsRows * sgrAttrsCols;
/** A fixed synthetic pane identity. */
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
/** The DA barrier every fenced operation records. */
const deviceAttribute = '\x1b[?62;4;22c';
/** Default-color storage flags (default fg + default bg). */
const defaults = 0x0300;

export { syntheticColorBinding };

/** Coalesce adjacent identical runs (every field but count) so fixtures stay canonical. */
function coalesce(runs) {
    const key = run => JSON.stringify(Object.entries(run).filter(([field]) => field !== 'count').sort(([a], [b]) => a.localeCompare(b)));
    const out = [];
    for (const run of runs) {
        const previous = out.at(-1);
        if (previous && key(previous) === key(run)) previous.count += run.count;
        else out.push({ ...run });
    }
    return out;
}

/** A plain neutral cell spec. */
export const plainCell = char => ({ char, attrs: [], underlineStyle: 0 });
/** A cell spec with named attributes (bold, faint, inverse, hidden, strikethrough, italic, blink, underline). */
export const styledCell = (char, attrs, underlineStyle = attrs.includes('underline') ? 1 : 0) => ({ char, attrs, underlineStyle });

/** Cell flags for one spec. */
function flagsOf(spec) {
    let flags = defaults;
    for (const name of spec.attrs) flags |= sgrAttrsFlagBits[name === 'dim' ? 'faint' : name];
    if (spec.underlineStyle) flags |= sgrAttrsFlagBits.underline;
    return flags;
}

/** A complete valid observation screen with the given leading cells; the rest is plain.
 * `binding` supplies the theme state, whose reverseVideo drives the DECSCNM control. */
export function syntheticSgrAttrsScreen(leading, binding = syntheticColorBinding()) {
    const flat = { stage: binding.stage };
    void flat;
    const channelDefaultFg = { source: 'default', index: -1, argb: binding.foregroundArgb };
    const channelDefaultBg = { source: 'default', index: -1, argb: binding.backgroundArgb };
    const specs = leading.map(spec => ({ ...spec, flags: flagsOf(spec) }));
    const attributes = coalesce([...specs.map(spec => ({ count: 1, flags: spec.flags, fg: -1, bg: -1 })),
        { count: cells - specs.length, flags: defaults, fg: -1, bg: -1 }]);
    const resolved = coalesce([...specs.map(() => ({ count: 1, fg: channelDefaultFg, bg: channelDefaultBg })),
        { count: cells - specs.length, fg: channelDefaultFg, bg: channelDefaultBg }]);
    const detail = (count, flags, underlineStyle) => ({ count, flags, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null,
        underlineStyle, underlineColorIndex: -1, underlineArgb: null });
    const details = coalesce([...specs.map(spec => detail(1, spec.flags, spec.underlineStyle)),
        detail(cells - specs.length, defaults, 0)]);
    return { rows: sgrAttrsRows, cols: sgrAttrsCols, lines: [specs.map(spec => spec.char).join(''), '', '', '', '', ''],
        cursorRow: 0, cursorCol: specs.length, cursorVisible: true,
        attributeRuns: attributes, resolvedColors: resolved, cellDetails: details, colorBinding: binding };
}

/** The cell specs of the three reads of one synthetic case (a conclusive, supported run). */
export function syntheticSgrAttrsReads(caseId) {
    const attr = { 'sgr.underline.single': ['underline'], 'sgr.inverse': ['inverse'], 'sgr.faint': ['dim'],
        'sgr.hidden': ['hidden'], 'sgr.strikethrough': ['strikethrough'] }[caseId];
    if (!attr) throw new Error(`Unknown synthetic case ${caseId}.`);
    const target = styledCell('X', attr);
    const trailing = caseId === 'sgr.faint'
        ? [target, styledCell('Y', ['bold', 'dim']), plainCell('Z')]
        : [target, plainCell('Y')];
    return { baseline: [plainCell('C')], target: [target], trailing };
}

/** Screens (baseline, target, trailing) of one synthetic case. */
export function syntheticSgrAttrsScreens(caseId, binding = syntheticColorBinding()) {
    const reads = syntheticSgrAttrsReads(caseId);
    return [reads.baseline, reads.target, reads.trailing].map(specs => syntheticSgrAttrsScreen(specs, binding));
}

/** One journaled frame with its digest. */
function frame(index, kind, screenValue, barrier) {
    return { index, kind, paneId, barrier, screen: screenValue, sha256: snapshotHash(screenValue) };
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts.
 * `readsOf(caseId)` may override the per-case cell specs to build negative evidence. */
export function syntheticSgrAttrsEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = syntheticColorBinding(), readsOf = null) {
    const nonce = 'NTILDE_SA_' + runId;
    const blank = syntheticSgrAttrsScreen([], binding);
    const bindScreen = { ...syntheticSgrAttrsScreen([], binding), lines: [nonce, '', '', '', '', ''], cursorCol: nonce.length };
    const frames = [frame(0, 'sgattr-start', blank, null), frame(1, 'sgattr-bind', bindScreen, deviceAttribute)];
    const cases = sgrAttrsContracts.map(contract => {
        const feeds = sgrAttrsFeeds(contract);
        const barriers = feeds.map(() => deviceAttribute);
        const reads = readsOf?.(contract.caseId) ?? syntheticSgrAttrsReads(contract.caseId);
        const screens = [reads.baseline, reads.target, reads.trailing].map(specs => syntheticSgrAttrsScreen(specs, binding));
        for (const screen of screens) frames.push(frame(frames.length, 'sgattr-read', screen, deviceAttribute));
        const pinned = { probeId: contract.caseId, ...replaySgrAttrsPinned(contract, screens) };
        return { caseId: contract.caseId, feeds, barriers,
            reads: screens.map(screen => ({ kind: 'cells', cursor: [screen.cursorRow, screen.cursorCol] })), pinned };
    });
    frames.push(frame(frames.length, 'sgattr-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: sgrAttrsAdapterId,
        probeIds: sgrAttrsContracts.map(contract => contract.caseId), cases };
    const adapter = { id: sgrAttrsAdapterId, derivedFromAdapterId: 'ntilde-owned-sgr-rgb-grading-v1',
        bundleSha256, upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, reset: sgrAttrsReset };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticSgrAttrsGrade(caseId, binding, readsOf = null) {
    const contract = sgrAttrsContracts.find(item => item.caseId === caseId);
    const reads = readsOf?.(caseId) ?? syntheticSgrAttrsReads(caseId);
    const screens = [reads.baseline, reads.target, reads.trailing].map(specs => syntheticSgrAttrsScreen(specs, binding));
    return gradeSgrAttrsCase(contract, sgrAttrsObservation(screens));
}
