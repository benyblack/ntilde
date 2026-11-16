// Preliminary observation plan, not a measured result. OSC 633 semantics:
// https://code.visualstudio.com/docs/terminal/shell-integration#_supported-escape-sequences
// A marks prompt start, B prompt end, C pre-execution and D execution finished;
// D's exit code is optional. Command-line extraction and E/P are outside this plan.
import { consumptionResult, osc133Fixture } from './osc133-contracts.mjs';
import { osc633CaseIds } from './osc633-grading-collector.mjs';

const osc = body => `\x1b]${body}\x07`;
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const mark = (code, marker) => osc(`${code};${marker}`);
const prompt = 'ntilde$ ';
const read = label => ({ read: label });
const target = ['A', 'B', 'C', 'D;0'];
export const osc633Prefix = 'vs633';
export const osc633SuiteId = 'ntilde-owned-osc633-graded-v1';
export const osc633ContractsVersion = 1;
export const osc633EffectContractId = 'ntilde-osc633-shell-lifecycle-effect-v1';

export const osc633Pinned = Object.freeze(Object.fromEntries(osc633CaseIds.map((id, index) => {
    const fixture = `${mark(633, target[index])}X`;
    return [id, Object.freeze({ termlessFeeds: Object.freeze([fixture]), terminalWrites: Object.freeze(['\x1b[1;1H\x1b[2K', fixture]) })];
})));

// OSC 133 prepares a directly observed, distinguishable state and supplies a
// separate positive control. It is never substituted for the target sequence.
function prior(index) {
    if (index === 1) return [mark(133, 'A'), at(3, 0) + prompt];
    const input = [mark(133, 'A'), at(2, 0) + prompt, mark(133, 'B') + 'echo control'];
    if (index === 2) return input;
    return [...input, mark(133, 'C'), '\r\noutput'];
}

export function makeOsc633Experiments(runId) {
    const fixture = osc133Fixture(runId), experiments = [];
    for (const [index, id] of osc633CaseIds.entries()) {
        const name = (suffix, steps) => ({ caseId: `${id}/${suffix}`, owner: id, name: suffix,
            plan: [{ feed: fixture.stage }, ...steps.map(step => typeof step === 'string' ? { feed: step } : step)] });
        const pinned = osc633Pinned[id].terminalWrites;
        const setup = prior(index);
        experiments.push(name('stock', [...pinned, read('pinned')]));
        experiments.push(name('effect', [...setup, read('prior'), '\x1b[1;1H\x1b[2K', 'X', read('ordinary'), ...pinned, read('pinned')]));
        experiments.push(name('positive-control', [...setup, read('prior'), '\x1b[1;1H\x1b[2K', `${mark(133, target[index])}X`, read('positive')]));
        experiments.push(name('st-terminator', [...setup, read('prior'), '\x1b[1;1H\x1b[2K', `\x1b]633;${target[index]}\x1b\\X`, read('post')]));
        // A and D may both return a running session to a prompt, and A may
        // clear the anchors too. Neither is a valid gate for the other.
        for (const [gateIndex, gate] of target.entries()) {
            if (gateIndex === index || (index === 0 && gateIndex === 3) || (index === 3 && gateIndex === 0)) continue;
            experiments.push(name(`gate-${'abcd'[gateIndex]}`, [...setup, read('prior'), '\x1b[1;1H\x1b[2K', `${mark(633, gate)}X`, read('post')]));
        }
        if (index === 0) experiments.push(name('shared-d', [...setup, read('prior'), '\x1b[1;1H\x1b[2K', `${mark(633, 'D;0')}X`, read('post')]));
        if (index === 3) {
            experiments.push(name('shared-a', [...setup, read('prior'), '\x1b[1;1H\x1b[2K', `${mark(633, 'A')}X`, read('post')]));
            experiments.push(name('no-exit-code', [...setup, read('prior'), mark(633, 'D'), read('post')]));
            experiments.push(name('nonzero-exit-code', [...setup, read('prior'), mark(633, 'D;7'), read('post')]));
        }
    }
    return experiments;
}

export function osc633TermlessContext(caseId, screen) {
    const pinned = osc633Pinned[caseId];
    if (!pinned) throw new Error('Unknown OSC 633 case.');
    let fed = 0;
    return { get fedCount() { return fed; },
        feed: text => { if (fed !== 0 || text !== pinned.termlessFeeds[0]) throw new Error(`Pinned ${caseId} feed differs.`); fed++; },
        getCell: (row, col) => { if (fed !== 1 || row !== 0 || col !== 0) throw new Error(`Pinned ${caseId} read differs.`); return { char: screen.lines[0]?.[0] ?? '' }; } };
}
export function osc633TerminalContext(caseId, screen) {
    const pinned = osc633Pinned[caseId];
    if (!pinned) throw new Error('Unknown OSC 633 case.');
    let written = 0;
    return { get writtenCount() { return written; },
        write: text => { if (text !== pinned.terminalWrites[written]) throw new Error(`Pinned ${caseId} write differs.`); written++; },
        queryCursorPosition: () => { if (written !== 2) throw new Error(`Pinned ${caseId} queried early.`); return Promise.resolve({ row: screen.cursorRow + 1, col: screen.cursorCol + 1 }); } };
}
export function replayOsc633Pinned(caseId, screen) {
    if (!osc633Pinned[caseId]) throw new Error('Unknown OSC 633 case.');
    return { termless: consumptionResult((screen.lines[0]?.[0] ?? '') === 'X'), terminal: consumptionResult(screen.cursorCol + 1 === 2) };
}
