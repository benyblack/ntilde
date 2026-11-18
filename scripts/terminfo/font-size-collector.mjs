import { fontSizeContracts, fontSizeNonce, fontSizePinnedRequests, fontSizePrefix } from './font-size-contracts.mjs';
import { parseCellSizeFrame } from './cell-size-frame.mjs';
import { devqReset, devqSeed } from './devq-contracts.mjs';

export function fontSizeContext(index, report) {
    let used = false;
    const consume = request => {
        if (used || request !== fontSizePinnedRequests[index]) throw new Error('Pinned font-size request differs.');
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

export async function collectFontSize({ config, fence, owner, module }) {
    await owner(`${fontSizePrefix}-start`);
    const cases = [];
    let primary;
    try {
        await owner(`${fontSizePrefix}-bind`, await fence.write('\x1b[?1049h' + devqReset + devqSeed(fontSizeNonce + config.runId)));
        for (const [index, contract] of fontSizeContracts.entries()) {
            const exchanges = [];
            for (const item of contract.exchanges) {
                const report = await fence.exchange(item.request, parseCellSizeFrame); // NOSONAR - each request must complete before the next request
                await owner(`${fontSizePrefix}-read`, report.barrier, { report }); // NOSONAR - pair each exchange with its own parent snapshot before advancing
                exchanges.push({ id: item.id, request: item.request, report });
            }
            const pinned = exchanges.find(item => item.id === 'pinned').report;
            cases.push({ caseId: contract.caseId, exchanges, pinned: {
                termless: module.ntildeRunFontSizeTermless(contract.caseId, fontSizeContext(index, pinned)),
                terminal: await module.ntildeRunFontSizeTerminal(contract.caseId, fontSizeContext(index, pinned)), // NOSONAR - settle each pinned replay in case order
            } });
        }
    } catch (error) { primary = error; }
    try {
        const restore = devqReset + '\x1b[?1049l';
        if (fence.failed) await fence.writeCleanup(restore);
        else await owner(`${fontSizePrefix}-finish`, await fence.write(restore));
    }
    catch (error) {
        if (primary) throw new AggregateError([primary, error], 'Font-size collection and restoration failed.');
        throw error;
    }
    if (primary) throw primary;
    return { schemaVersion: 1, runId: config.runId, cases };
}
