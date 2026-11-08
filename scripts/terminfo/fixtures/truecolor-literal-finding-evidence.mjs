// Synthetic, fully valid truecolor-literal finding evidence for portable tests. Every
// frame and run-log entry goes through the same production validators as a real
// measurement. `readsOf(contract, phase)` may override cells to build negative or
// before-fix (aliased) evidence; `bindingOf(contract, phase)` may override the recorded palette.
import { snapshotHash } from '../agent-observer.mjs';
import { truecolorFindingContracts, truecolorFindingFeeds, truecolorFindingNoncePrefix, truecolorFindingReset,
    truecolorFindingSuiteId, truecolorFindingContractsVersion, truecolorFindingCaseIds, gradeTruecolorFindingCase,
    truecolorFindingObservation } from '../truecolor-literal-finding-contracts.mjs';
import { canonicalPalette, paletteBinding, colorCell, plainCell, paletteChannel, literalChannel, syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';

const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
export const phases = Object.freeze(['before', 'mutated', 'restored']);
const magenta = 0xffff00ff;

/** The recorded palette of one phase: the OSC 4 mutation shows as one overridden entry. */
export function findingBinding(contract, phase, base = {}) {
    if (phase !== 'mutated') return paletteBinding(base);
    const palette = canonicalPalette();
    palette[contract.index] = magenta;
    return { ...paletteBinding({ ...base, palette }), paletteOverrideIndices: [contract.index] };
}

/** The five cells of one phase: faithful (literal) unless `aliased` stores the palette index instead. */
export function findingCells(contract, { aliased = false } = {}) {
    const { channel, requested, index } = contract;
    const own = spec => ({ [channel]: spec });
    const target = char => colorCell(char, own(aliased ? paletteChannel(index) : literalChannel(requested.r, requested.g, requested.b)));
    return [plainCell('B'), colorCell('I', own(paletteChannel(index))), target('S'), target('C'), target('D')];
}

function frame(index, kind, screen, barrier) {
    return { index, kind, paneId, barrier, screen, sha256: snapshotHash(screen) };
}

/** A complete synthetic run: frames and worker run log. */
export function syntheticTruecolorFindingEvidence(runId = 'a'.repeat(32), { cellsOf = null, bindingOf = null } = {}) {
    const nonce = truecolorFindingNoncePrefix + runId;
    const blank = syntheticSgrPaletteScreen([], paletteBinding());
    const bindScreen = { ...syntheticSgrPaletteScreen([], paletteBinding()), lines: [nonce, '', '', '', '', ''], cursorCol: nonce.length };
    const frames = [frame(0, 'sgtf-start', blank, null), frame(1, 'sgtf-bind', bindScreen, deviceAttribute)];
    const cases = truecolorFindingContracts.map(contract => {
        const feeds = truecolorFindingFeeds(contract);
        const screens = phases.map(phase => syntheticSgrPaletteScreen(cellsOf?.(contract, phase) ?? findingCells(contract),
            bindingOf?.(contract, phase) ?? findingBinding(contract, phase)));
        for (const screen of screens) frames.push(frame(frames.length, 'sgtf-read', screen, deviceAttribute));
        return { caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute),
            reads: screens.map(screen => ({ kind: 'cells', cursor: [screen.cursorRow, screen.cursorCol] })) };
    });
    frames.push(frame(frames.length, 'sgtf-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, suiteId: truecolorFindingSuiteId, contractsVersion: truecolorFindingContractsVersion,
        caseIds: [...truecolorFindingCaseIds], cases };
    return { frames, run, runId, reset: truecolorFindingReset };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticTruecolorFindingGrade(caseId, { cellsOf = null, bindingOf = null } = {}) {
    const contract = truecolorFindingContracts.find(item => item.caseId === caseId);
    const screens = phases.map(phase => syntheticSgrPaletteScreen(cellsOf?.(contract, phase) ?? findingCells(contract),
        bindingOf?.(contract, phase) ?? findingBinding(contract, phase)));
    return gradeTruecolorFindingCase(contract, truecolorFindingObservation(contract, screens));
}
