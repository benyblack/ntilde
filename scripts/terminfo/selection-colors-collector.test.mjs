import assert from 'node:assert/strict';
import { test } from 'node:test';
import { selectionColorsRecordingContext, pinnedCellAt, collectSelectionColorsGrading } from './selection-colors-collector.mjs';
import { selectionColorsContracts, selectionColorsCaseIds, selectionColorsFeeds, selectionColorsReads, selectionColorsNoncePrefix, selectionColorsBind, selectionColorsFinish, selectionColorsReadPlan } from './selection-colors-contracts.mjs';

const deviceAttribute = '\x1b[?62;4;22c';
const reply = (code, rgb) => ({ raw: `\x1b]${code};rgb:${rgb}\x1b\\`, channels16: [0xaaaa, 0xbbbb, 0xcccc] });

test('the pinned cell projection is shared and minimal', () => {
    assert.deepEqual(pinnedCellAt({ lines: ['Xabc'] }, 0, 0), { char: 'X' });
    assert.deepEqual(pinnedCellAt({ lines: [''] }, 0, 0), { char: ' ' });
});

test('the set-case recording context serves exactly the pinned capture, and nothing else', () => {
    const contract = selectionColorsContracts[0];
    const context = selectionColorsRecordingContext(contract, [reply(17, 'aaaa/bbbb/cccc')], null);
    const captured = context.feedCapture('\x1b]17;?\x07');
    assert.equal(captured, reply(17, 'aaaa/bbbb/cccc').raw);
    assert.equal(context.fedCount, 1);
    assert.throws(() => context.feed('\x1b]17;?\x07'), /feed 1 differs/, 'a second feed is not part of the pinned fixture');
    assert.throws(() => selectionColorsRecordingContext(contract, [reply(17, 'aaaa/bbbb/cccc')], null).feedCapture('\x1b]19;?\x07'), /differs/);
    assert.throws(() => context.getCell(0, 0), /must read cell/, 'the set cases read no cells');
    assert.throws(() => context.getCursor(), /must not read/);
    assert.throws(() => context.getText(), /must not read/);
    // An absent reply is served as the empty raw the pinned oscReplyResult grades as no-response.
    assert.equal(selectionColorsRecordingContext(contract, [null], null).feedCapture('\x1b]17;?\x07'), '');
});

test('the reset-case recording context serves exactly the pinned feed and one cell read at (0,0)', () => {
    const contract = selectionColorsContracts[2];
    const screen = { lines: ['X'] };
    const context = selectionColorsRecordingContext(contract, [], screen);
    context.feed('\x1b]117\x07X');
    assert.equal(context.fedCount, 1);
    assert.deepEqual(context.getCell(0, 0), { char: 'X' });
    assert.equal(context.cellsRead, 1);
    assert.throws(() => selectionColorsRecordingContext(contract, [], screen).feed('\x1b]117\x07Y'), /feed 0 differs/, 'a changed byte differs');
    assert.throws(() => selectionColorsRecordingContext(contract, [], null).getCell(0, 0), /must read cell/, 'no screen, no cell');
    assert.throws(() => selectionColorsRecordingContext(contract, [], screen).getCell(1, 0), /must read cell/, 'only (0,0)');
    assert.throws(() => selectionColorsRecordingContext(contract, [], screen).feedCapture('\x1b]117\x07X'), /differs/, 'the pinned reset code does not capture');
});

/** A scripted fence and owner driven by one synthetic evidence run: the exchanges serve the evidence's replies, the reads return the evidence's screens. */
async function drivenFenceAndOwner() {
    const { syntheticSelectionColorsEvidence } = await import('./fixtures/selection-colors-evidence.mjs');
    const evidence = syntheticSelectionColorsEvidence('a'.repeat(32));
    const requests = [];
    let cleaned = 0;
    let caseIndex = -1, readIndex = -1;
    const readOf = () => {
        const frames = [evidence.frames[0], evidence.frames[1]];
        for (const contract of selectionColorsContracts) {
            const count = selectionColorsReads(contract);
            if (caseIndex < frames.length) { /* fall through */ }
            frames.push(...evidence.frames.slice(2 + frames.length - 2, 2 + frames.length - 2 + count));
        }
        return null;
    };
    void readOf;
    // The evidence's frames in order: start(0), bind(1), then every case's reads, then finish.
    const reads = [];
    let at = 2;
    for (const contract of selectionColorsContracts) {
        reads.push(...evidence.frames.slice(at, at + selectionColorsReads(contract)));
        at += selectionColorsReads(contract);
    }
    const fence = {
        requests,
        get failed() { return false; },
        get cleanups() { return cleaned; },
        write(sequence) { requests.push({ kind: 'write', sequence }); return Promise.resolve(deviceAttribute); },
        writeCleanup(sequence) { cleaned++; requests.push({ kind: 'cleanup', sequence }); return Promise.resolve(); },
        colorExchange(sequence, spec) {
            requests.push({ kind: 'exchange', sequence, spec });
            // The replies are whatever the evidence's next read journals for this spec.
            const read = reads[readIndex + 1];
            const pending = read ? read.queries.filter(query => query.spec.code === spec.code
                && (spec.code !== 4 || query.spec.index === spec.index)) : [];
            const reply = pending[0]?.reply ?? null;
            return Promise.resolve({ barrier: deviceAttribute, reply });
        },
    };
    const owner = (kind, barrier, extra = {}) => {
        if (kind === 'selc-read') readIndex++;
        const screen = kind === 'selc-read' ? reads[readIndex].screen : evidence.frames[kind === 'selc-start' ? 0 : 1].screen;
        return Promise.resolve({ screen, facts: undefined });
    };
    return { fence, owner, evidence, reads };
}

test('the worker driver replays every contracted plan serially, grades the pinned code over its own evidence and restores', async t => {
    const { adaptSelectionColorsCollector, selectionColorsAdapterId } = await import('./selection-colors-grading-collector.mjs');
    const { replaySelectionColorQuery, replaySelectionColorResetTermless } = await import('./selection-colors-grading.mjs');
    const { selectionColorsRecordingContext, pinnedCellAt } = await import('./selection-colors-collector.mjs');
    // The live pinned module is approximated by the replay ports over the served evidence; the real-bundle test covers the live code.
    const live = {
        ntildeSelectionColorsAdapter: { id: selectionColorsAdapterId, probeIds: [...selectionColorsCaseIds] },
        ntildeRunSelectionColorsTermless: (probeId, context) => {
            const contract = selectionColorsContracts.find(item => item.caseId === probeId);
            if (contract.isResetCase) {
                // The driver hands the pinned read's screen; replay the pinned fixture bytes through the context to cross-check them.
                context.feed(contract.pinnedFeeds[0]);
                const cell = context.getCell(0, 0);
                return { probeId, ...replaySelectionColorResetTermless(contract, { lines: [cell.char] }) };
            }
            const raw = context.feedCapture(contract.pinnedFeeds[0]);
            return { probeId, ...replaySelectionColorQuery(contract, [raw ? { raw } : null]) };
        },
    };
    const { fence, owner, evidence } = await drivenFenceAndOwner();
    const log = [];
    const loggingOwner = async (kind, barrier, extra) => { log.push(kind); return owner(kind, barrier, extra); };
    const run = await collectSelectionColorsGrading({ config: { runId: 'a'.repeat(32) }, fence, owner: loggingOwner, gradingModule: live });
    assert.deepEqual(run.probeIds, [...selectionColorsCaseIds]);
    assert.equal(run.cases.length, 4);
    for (const [index, contract] of selectionColorsContracts.entries()) {
        const item = run.cases[index];
        assert.equal(item.caseId, contract.caseId);
        assert.deepEqual(item.feeds, selectionColorsFeeds(contract));
        assert.equal(item.reads.length, selectionColorsReads(contract));
        assert.ok(item.barriers.every(barrier => barrier === deviceAttribute));
        assert.equal(item.pinned.notTested?.reason ?? item.pinned.observation?.evidence,
            contract.isResetCase ? 'no-semantic-observable' : 'query');
    }
    assert.equal(log[0], 'selc-start');
    assert.equal(log[1], 'selc-bind');
    assert.equal(log.at(-1), 'selc-finish');
    assert.equal(log.length, 3 + selectionColorsReadPlan.length);
    assert.equal(fence.requests[0].sequence, selectionColorsBind + selectionColorsNoncePrefix + 'a'.repeat(32));
    assert.equal(fence.requests.at(-1).sequence, selectionColorsFinish);
    assert.equal(fence.cleanups, 0, 'a healthy run takes no cleanup path');
});

test('a fatal collection aggregates the failure with restoration and never issues another barrier after the failure', async () => {
    const { fence, owner } = await drivenFenceAndOwner();
    let exchanges = 0;
    let failed = false;
    const failing = {
        get failed() { return failed; },
        write(sequence) { fence.requests.push({ kind: 'write', sequence }); return Promise.resolve(deviceAttribute); },
        writeCleanup(sequence) { return fence.writeCleanup(sequence); },
        colorExchange(sequence, spec) {
            if (++exchanges > 2) { failed = true; return Promise.reject(new Error('Cell barrier timed out.')); }
            return fence.colorExchange(sequence, spec);
        },
    };
    const live = { ntildeSelectionColorsAdapter: { id: 'x', probeIds: [] } };
    let error;
    try { await collectSelectionColorsGrading({ config: { runId: 'b'.repeat(32) }, fence: failing, owner, gradingModule: live }); }
    catch (caught) { error = caught; }
    assert.ok(error, 'the failure propagates');
    assert.equal(fence.cleanups, 1, 'the bounded best-effort cleanup ran once');
    const last = fence.requests.at(-1);
    assert.equal(last.kind, 'cleanup');
    assert.equal(last.sequence, selectionColorsFinish);
    assert.ok(!fence.requests.some(request => request.kind === 'exchange' && request.sequence.startsWith('\x1b]4;5;')), 'no later exchange on a dead fence');
});
