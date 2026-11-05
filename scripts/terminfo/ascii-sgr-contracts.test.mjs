import assert from 'node:assert/strict';
import { test } from 'node:test';
import { asciiSgrContracts, asciiSgrCaseIds, asciiSgrContractsVersion, asciiSgrSuiteId,
    contractFeeds, contractReads, validateRecordedFeeds, gradeRecordedCase, expandScreenFlags } from './ascii-sgr-contracts.mjs';
import { syntheticCaseScreens } from './fixtures/ascii-sgr-evidence.mjs';

test('the batch covers exactly the four contracted terminfo.dev cases in a fixed order', () => {
    assert.equal(asciiSgrContractsVersion, 1);
    assert.equal(asciiSgrSuiteId, 'ntilde-owned-ascii-sgr-graded-v1');
    assert.deepEqual([...asciiSgrCaseIds], ['text.basic', 'text.overwrite', 'sgr.bold', 'sgr.italic']);
});

test('contracts pin the exact pinned fixture bytes for every case', () => {
    const feeds = Object.fromEntries(asciiSgrContracts.map(contract => [contract.caseId, contractFeeds(contract)]));
    assert.deepEqual(feeds['text.basic'], ['\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H', '\x1b[2J\x1b[H', 'Hello']);
    assert.deepEqual(feeds['text.overwrite'], ['\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H', 'AB', '\x1b[1GC']);
    assert.deepEqual(feeds['sgr.bold'], ['\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H', '\x1b[1mX']);
    assert.deepEqual(feeds['sgr.italic'], ['\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H', '\x1b[3mX']);
    assert.deepEqual(asciiSgrContracts.map(contractReads), [2, 2, 1, 1]);
});

test('contracts retain the stock terminal fixture and its unmet evidence demand', () => {
    const basic = asciiSgrContracts.find(contract => contract.caseId === 'text.basic');
    assert.deepEqual(basic.terminalFixture.writes, ['\x1b[1;1H\x1b[2K', 'Hello']);
    assert.equal(basic.terminalFixture.demand, 'rendered Hello text');
    assert.deepEqual(basic.terminalFixture.minimum, { rows: 1, cols: 6 });
    const overwrite = asciiSgrContracts.find(contract => contract.caseId === 'text.overwrite');
    assert.deepEqual(overwrite.terminalFixture.writes, ['\x1b[1;1H\x1b[2K', 'AB', '\x1b[1;2H', 'X']);
    for (const caseId of ['sgr.bold', 'sgr.italic']) {
        const contract = asciiSgrContracts.find(item => item.caseId === caseId);
        assert.match(contract.terminalFixture.demand, /^consumed \(cursor advance does not verify SGR styling\)$/);
        assert.match(contract.appearanceScope, /^stored (bold|italic) flag only/);
    }
});

test('wrong input variants are rejected against the contract bytes', () => {
    const contract = asciiSgrContracts.find(item => item.caseId === 'sgr.bold');
    for (const variant of [
        ['\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H', '\x1b[2mX'],
        ['\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H', '\x1b[1mX', 'extra'],
        ['\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H'],
        [],
    ]) assert.throws(() => validateRecordedFeeds(contract, variant), /do not match the pinned contract/);
    assert.doesNotThrow(() => validateRecordedFeeds(contract, contractFeeds(contract)));
});

test('contract grader passes the observed positive screens', () => {
    for (const contract of asciiSgrContracts) {
        const graded = gradeRecordedCase(contract, syntheticCaseScreens(contract.caseId));
        assert.equal(graded.outcome, 'supported', contract.caseId);
        assert.equal(graded.note, undefined);
    }
});

test('measured mismatches are unsupported only with a valid control', () => {
    const cells = 6 * 60;
    const screen = (line, runs) => ({ rows: 6, cols: 60, lines: [line, '', '', '', '', ''],
        cursorRow: 0, cursorCol: line.length, cursorVisible: true,
        attributeRuns: runs ?? [{ count: cells, flags: 0, fg: -1, bg: -1 }] });
    const basic = asciiSgrContracts.find(item => item.caseId === 'text.basic');
    assert.equal(basic.grade([screen('', undefined), screen('Hellp', undefined)]).outcome, 'unsupported');
    assert.equal(basic.grade([screen('Hello', undefined), screen('', undefined)]).outcome, 'inconclusive');
    assert.equal(basic.grade([screen('Hello', undefined), screen('Hello', undefined)]).outcome, 'inconclusive');
    const overwrite = asciiSgrContracts.find(item => item.caseId === 'text.overwrite');
    assert.equal(overwrite.grade([screen('AB'), screen('AB')]).outcome, 'unsupported');
    assert.equal(overwrite.grade([screen('AX'), screen('CB')]).outcome, 'inconclusive');
    const bold = asciiSgrContracts.find(item => item.caseId === 'sgr.bold');
    assert.equal(bold.grade([screen('X')]).outcome, 'unsupported');
    assert.equal(bold.grade([screen('Y')]).outcome, 'inconclusive');
});

test('attribute flag expansion rejects incomplete or invalid runs', () => {
    const cells = 6 * 60;
    const base = syntheticCaseScreens('sgr.bold')[0];
    assert.equal(expandScreenFlags(base)[0], 0x0001);
    const short = structuredClone(base); short.attributeRuns = [{ count: 10, flags: 0, fg: -1, bg: -1 }];
    assert.throws(() => expandScreenFlags(short), /Incomplete graded attribute runs/);
    const invalid = structuredClone(base); invalid.attributeRuns = [{ count: cells, flags: 0x1ffff, fg: -1, bg: -1 }];
    assert.throws(() => expandScreenFlags(invalid), /Invalid graded attribute runs/);
});
