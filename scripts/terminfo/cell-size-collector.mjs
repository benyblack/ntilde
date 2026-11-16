import { cellSizeContracts, cellSizeNonce, cellSizePinnedRequests, cellSizePrefix } from './cell-size-contracts.mjs';
import { parseCellSizeFrame } from './cell-size-frame.mjs';
import { devqReset, devqSeed } from './devq-contracts.mjs';

export function cellSizeContext(index, report) {
    let used = false;
    const consume = request => {
        if (used || request !== cellSizePinnedRequests[index]) throw new Error('Pinned cell-size request differs.');
        used = true;
    };
    return { feedCapture: request => { consume(request); return report.replies.join(''); },
        queryWithSentinelOutcome: async (request, pattern) => {
            consume(request);
            const raw = report.replies.join('') + report.barrier;
            const match = pattern.exec(raw);
            return { raw, match, reason: match ? 'reply' : 'sentinel' };
        } };
}

export async function collectCellSize({ config, fence, owner, module }) {
    await owner(`${cellSizePrefix}-start`);
    const cases = [];
    let primary;
    try {
        await owner(`${cellSizePrefix}-bind`, await fence.write('\x1b[?1049h' + devqReset + devqSeed(cellSizeNonce + config.runId)));
        for (const [index, contract] of cellSizeContracts.entries()) {
            const exchanges = [];
            for (const item of contract.exchanges) {
                const report = await fence.exchange(item.request, parseCellSizeFrame); // NOSONAR - each request must complete before the next request
                await owner(`${cellSizePrefix}-read`, report.barrier, { report }); // NOSONAR - pair each exchange with its own parent snapshot before advancing
                exchanges.push({ id: item.id, request: item.request, report });
            }
            const pinned = exchanges.find(item => item.id === 'pinned').report;
            cases.push({ caseId: contract.caseId, exchanges, pinned: {
                termless: module.ntildeRunCellSizeTermless(contract.caseId, cellSizeContext(index, pinned)),
                terminal: await module.ntildeRunCellSizeTerminal(contract.caseId, cellSizeContext(index, pinned)), // NOSONAR - settle each pinned replay in case order
            } });
        }
    } catch (error) { primary = error; }
    try { await owner(`${cellSizePrefix}-finish`, await fence.write(devqReset + '\x1b[?1049l')); }
    catch (error) {
        if (primary) throw new AggregateError([primary, error], 'Cell-size collection and restoration failed.');
        throw error;
    }
    if (primary) throw primary;
    return { schemaVersion: 1, runId: config.runId, cases };
}
