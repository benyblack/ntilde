import { collectFontSize } from './font-size-collector.mjs';
import { FontSizeObserver } from './font-size-capture.mjs';
import { fontSizeContracts } from './font-size-contracts.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { syntheticFontSizeEvidence } from './fixtures/font-size-evidence.mjs';
import { buildFontSizeReport } from './font-size-grading.mjs';
import { fontSizeGeometry, fontSizeMatches, fontSizeIntegerPoints } from './font-size-contracts.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { legacyPrefixSource, routedPrefixSource } from './fixtures/private-clipboard-receipt.mjs';
const build = e => buildFontSizeReport(e.frames, e.run, e.runId, e.adapter);

test('synthetic frozen scopes exclude new font imports on LF and CRLF without broadening historical source maps', () => {
    const source = "import { retained } from './kept.mjs';\nimport { collectFontSize } from './font-size-collector.mjs';\n"
        + "import { checkFontSizeEvidence } from './terminfo/font-size-receipt.mjs';\n";
    for (const project of [legacyPrefixSource, routedPrefixSource]) {
        for (const text of [source, source.replaceAll('\n', '\r\n')]) {
            assert.equal(project(Buffer.from(text)).toString(), "import { retained } from './kept.mjs';\n");
        }
    }
});

test('two exact integer-point queries qualify from raw replies, independent font size and unchanged window geometry', () => {
    assert.deepEqual(build(syntheticFontSizeEvidence()).cases.map(c => [c.conclusion.outcome, c.effectContract.outcome]),
        [['supported', 'supported'], ['supported', 'supported']]);
});
for (const [name, replies] of [['missing', []], ['wrong size', ['\x1b]7770;14\x07']],
    ['wrong units', ['\x1b]7770;16\x07']], ['wrong code', ['\x1b]7777;12\x07']],
    ['wrong terminator', ['\x1b]7770;12\x1b\\']], ['duplicate', ['\x1b]7770;12\x07', '\x1b]7770;12\x07']]]) {
    test('valid controls grade ' + name + ' as unsupported', () => {
        const e = syntheticFontSizeEvidence((_id, item) => item.id === 'documented-bel' ? replies : null);
        assert.equal(build(e).cases[0].effectContract.outcome, 'unsupported');
    });
}
test('bad delivery control is inconclusive; responding to a mutation form is unsupported', () => {
    assert.equal(build(syntheticFontSizeEvidence((_id, item) => item.id === 'positive-before' ? [] : null)).cases[0].effectContract.outcome, 'inconclusive');
    assert.equal(build(syntheticFontSizeEvidence((_id, item) => item.id === 'unavailable-form-0' ? ['\x1b]7770;12\x07'] : null)).cases[0].effectContract.outcome, 'unsupported');
});
for (const [name, change] of [
    ['truncated journal', e => e.frames.pop()], ['foreign run', e => { e.runId = 'c'.repeat(32); }],
    ['foreign pane', e => { e.frames[2].paneId = 'foreign'; }], ['stale barrier', e => { e.frames[2].barrier = '\x1b[?1c'; }],
    ['changed window', e => { e.frames[3].geometry.windowWidthDip++; }], ['changed font', e => { e.frames[3].geometry.fontSizeDip += 4; }],
    ['missing font', e => { delete e.frames[0].geometry.fontSizeDip; }], ['fractional points', e => { e.frames[0].geometry.fontSizeDip = 14; }],
    ['changed scale', e => { e.frames[3].geometry.backingScale = 1; }], ['unrestored state', e => { e.frames.at(-1).screen.cursorCol++; e.frames.at(-1).sha256 = snapshotHash(e.frames.at(-1).screen); }],
    ['changed request', e => { e.run.cases[0].exchanges[0].request = '\x1b[14t'; }],
    ['changed pinned', e => { e.run.cases[0].pinned.terminal.pass = false; }],
]) test('refuses ' + name, () => { const e = syntheticFontSizeEvidence(); change(e); assert.throws(() => build(e)); });

test('oracle separates typographic points, logical DIPs, interface zoom and physical backing scale', () => {
    const geometry = { ...syntheticFontSizeEvidence().frames[0].geometry, renderScaling: 3, backingScale: 2 };
    assert.equal(fontSizeMatches({ protocol: '7770', terminator: '\x07' }, ['\x1b]7770;18\x07'], fontSizeGeometry(geometry, geometry.viewRows, geometry.viewCols)), true);
    assert.equal(fontSizeMatches({ protocol: '7770', terminator: '\x07' }, ['\x1b]7770;48\x07'], geometry), false);
    assert.equal(fontSizeIntegerPoints({ fontSizeDip: 20, renderScaling: 1.4 * 1.5, backingScale: 1.5 }), 21);
    assert.throws(() => fontSizeIntegerPoints({ fontSizeDip: 28.000001, renderScaling: 1.5, backingScale: 1.5 }));
});

const da = '\x1b[?62;4;22c';
test('the worker driver executes every exact request and restores after both success and a primary failure', async () => {
    const evidence = syntheticFontSizeEvidence(), calls = [], requests = [];
    let caseIndex = 0, exchangeIndex = 0;
    const owner = async kind => { calls.push(kind); return {}; };
    const fence = { write: async () => da, exchange: async request => {
        const contract = evidence.run.cases[caseIndex], exchange = contract.exchanges[exchangeIndex++];
        assert.equal(request, exchange.request); requests.push(request);
        if (exchangeIndex === contract.exchanges.length) { caseIndex++; exchangeIndex = 0; }
        return structuredClone(exchange.report);
    } };
    const module = {
        ntildeRunFontSizeTermless: id => evidence.run.cases.find(item => item.caseId === id).pinned.termless,
        ntildeRunFontSizeTerminal: async id => evidence.run.cases.find(item => item.caseId === id).pinned.terminal,
    };
    const run = await collectFontSize({ config: { runId: evidence.runId }, fence, owner, module });
    assert.deepEqual(run, evidence.run);
    assert.deepEqual(requests, fontSizeContracts.flatMap(contract => contract.exchanges.map(item => item.request)));
    assert.equal(calls.at(-1), 'fnt-finish');
    const broken = { ...fence, exchange: async () => { throw new Error('primary'); } };
    await assert.rejects(collectFontSize({ config: { runId: evidence.runId }, fence: broken, owner, module }), /primary/);
    assert.equal(calls.at(-1), 'fnt-finish');
    await assert.rejects(collectFontSize({ config: { runId: evidence.runId }, fence: broken,
        owner: async kind => { if (kind === 'fnt-finish') throw new Error('cleanup'); }, module }), AggregateError);
});

test('the parent observer journals independent view geometry and refuses stale pane metrics or a mismatched exchange barrier', async () => {
    const e = syntheticFontSizeEvidence();
    const observer = new FontSizeObserver({ appData: '/synthetic', ownerPid: 42, endpoint: () => 'synthetic',
        rpc: async (_endpoint, method, params) => {
            assert.equal(method, 'readPaneMetadata'); assert.equal(params.paneId, e.frames[0].paneId);
            return { paneId: e.frames[0].paneId, cellGeometry: e.frames[0].geometry, fontGeometry: e.frames[0].geometry };
        } });
    let next = 0;
    observer.cells = { restored: false, frames: [], handle: async () => {
        const frame = structuredClone(e.frames[next++]); observer.cells.frames.push(frame);
        return { paneId: frame.paneId, screen: frame.screen };
    } };
    await observer.handle({ kind: 'fnt-start' }, () => 1000);
    assert.deepEqual(observer.frames[0].geometry, e.frames[0].geometry);
    await assert.rejects(observer.handle({ kind: 'fnt-read', barrier: '\x1b[?1c', report: e.frames[2].report }, () => 1000), /Unbound/);
    observer.options.rpc = async () => ({ paneId: 'stale' });
    await assert.rejects(observer.handle({ kind: 'fnt-bind', barrier: da }, () => 1000), /identity/);
});
