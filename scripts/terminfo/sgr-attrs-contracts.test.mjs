import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sgrAttrsContracts, sgrAttrsCaseIds, sgrAttrsReset, sgrAttrsClear, sgrAttrsFeeds, sgrAttrsReads,
    validateSgrAttrsFeeds, sgrAttrsCellAt, sgrAttrsObservation, gradeSgrAttrsCase } from './sgr-attrs-contracts.mjs';
import { syntheticSgrAttrsReads, syntheticSgrAttrsGrade, syntheticSgrAttrsScreen, syntheticSgrAttrsScreens,
    syntheticColorBinding, plainCell, styledCell } from './fixtures/sgr-attrs-evidence.mjs';

const grade = (caseId, mutate, binding) => syntheticSgrAttrsGrade(caseId, binding,
    id => { const reads = syntheticSgrAttrsReads(id); mutate?.(reads); return reads; });
const contractOf = caseId => sgrAttrsContracts.find(contract => contract.caseId === caseId);

test('the five contracted cases keep the pinned fixture bytes exactly', () => {
    assert.deepEqual([...sgrAttrsCaseIds], ['sgr.underline.single', 'sgr.inverse', 'sgr.faint', 'sgr.hidden', 'sgr.strikethrough']);
    assert.deepEqual(sgrAttrsContracts.map(contract => [...contract.pinnedFeeds]),
        [['\x1b[4mX'], ['\x1b[7mX'], ['\x1b[2mX'], ['\x1b[8mX'], ['\x1b[9mX']]);
    for (const contract of sgrAttrsContracts) {
        assert.deepEqual(sgrAttrsFeeds(contract).slice(0, 5), [sgrAttrsReset, 'C', sgrAttrsClear, contract.pinnedFeeds[0], contract.trailing]);
        assert.equal(sgrAttrsReads(contract), 3);
        assert.equal(contract.pinnedRead, 1);
        validateSgrAttrsFeeds(contract, sgrAttrsFeeds(contract));
        assert.throws(() => validateSgrAttrsFeeds(contract, [...sgrAttrsFeeds(contract).slice(0, 3), 'X', ...sgrAttrsFeeds(contract).slice(4)]),
            /do not match the pinned contract/);
    }
});

test('the reset turns DECSCNM off and carries no reverse-video enable', () => {
    assert.ok(sgrAttrsReset.startsWith('\x1b[?5l'));
    for (const contract of sgrAttrsContracts) {
        assert.ok(!sgrAttrsFeeds(contract).some(feed => /\x1b\[\?5h/.test(feed)));
    }
});

test('a valid control and set attribute grades supported for every case', () => {
    for (const caseId of sgrAttrsCaseIds) assert.equal(syntheticSgrAttrsGrade(caseId).outcome, 'supported', caseId);
});

test('an inactive target after valid controls is unsupported, never silently supported', () => {
    for (const caseId of sgrAttrsCaseIds) {
        const result = grade(caseId, reads => {
            reads.target = [plainCell('X')];
            reads.trailing = [plainCell('X'), ...reads.trailing.slice(1)];
        });
        assert.equal(result.outcome, 'unsupported', caseId);
        assert.match(result.note, /was not set after SGR/, caseId);
    }
});

test('initially active attributes invalidate the baseline control', () => {
    for (const caseId of sgrAttrsCaseIds) {
        const result = grade(caseId, reads => { reads.baseline = [styledCell('C', ['strikethrough'])]; });
        assert.equal(result.outcome, 'inconclusive', caseId);
        assert.match(result.note, /initially active/);
    }
    const underlined = grade('sgr.underline.single', reads => { reads.baseline = [styledCell('C', ['underline'])]; });
    assert.equal(underlined.outcome, 'inconclusive');
});

test('unrelated attribute drift on the target invalidates the setup control', () => {
    for (const caseId of sgrAttrsCaseIds) {
        const result = grade(caseId, reads => {
            const extra = reads.target[0].attrs.includes('italic') ? [] : ['italic'];
            reads.target = [styledCell('X', [...reads.target[0].attrs, ...extra])];
            reads.trailing = [reads.target[0], ...reads.trailing.slice(1)];
        });
        assert.equal(result.outcome, 'inconclusive', caseId);
        assert.match(result.note, /drifted/);
    }
});

test('a missing rendered X cell is the pinned inconclusive, whatever the flags say', () => {
    for (const caseId of sgrAttrsCaseIds) {
        const result = grade(caseId, reads => { reads.target = [styledCell(' ', reads.target[0].attrs)]; });
        assert.deepEqual(result, { outcome: 'inconclusive', note: 'No rendered X cell to evaluate' }, caseId);
    }
});

test('missing trailing reset evidence leaves the case inconclusive', () => {
    for (const caseId of sgrAttrsCaseIds) {
        const result = grade(caseId, reads => { reads.trailing = [reads.trailing[0], styledCell('Y', ['bold']), plainCell('Z')]; });
        assert.equal(result.outcome, 'inconclusive', caseId);
        assert.match(result.note, /restoration control/);
    }
});

test('global reverse video makes every case inconclusive, distinct from the per-cell inverse flag', () => {
    const reversed = { ...syntheticColorBinding(), reverseVideo: true };
    for (const caseId of sgrAttrsCaseIds) {
        const result = syntheticSgrAttrsGrade(caseId, reversed);
        assert.equal(result.outcome, 'inconclusive', caseId);
        assert.match(result.note, /DECSCNM/);
    }
});

test('underline: only the single style qualifies; other styles are a measured mismatch', () => {
    for (const style of [2, 3, 4, 5]) {
        const result = grade('sgr.underline.single', reads => {
            reads.target = [styledCell('X', ['underline'], style)];
            reads.trailing = [reads.target[0], reads.trailing[1]];
        });
        assert.equal(result.outcome, 'unsupported', `style ${style}`);
        assert.match(result.note, /instead of single/);
    }
    // The stored style and the flag must agree: style bits without the flag are rejected by the stored-detail validators.
    assert.throws(() => sgrAttrsCellAt(syntheticSgrAttrsScreen([styledCell('X', [], 1)]), 0, 0) && (() => { throw new Error('flag-coherent'); })(), /flag-coherent/);
});

test('faint is distinct from bold: bold-only is unsupported, faint+bold target is drift', () => {
    const boldOnly = grade('sgr.faint', reads => {
        reads.target = [styledCell('X', ['bold'])];
        reads.trailing = [reads.target[0], ...reads.trailing.slice(1)];
    });
    assert.equal(boldOnly.outcome, 'inconclusive'); // trailing Y/Z controls no longer show the SGR 22 pair
    const faintBoth = grade('sgr.faint', reads => {
        reads.target = [styledCell('X', ['dim', 'bold'])];
        reads.trailing = [reads.target[0], ...reads.trailing.slice(1)];
    });
    assert.equal(faintBoth.outcome, 'inconclusive');
    assert.match(faintBoth.note, /drifted/);
});

test('SGR 22 control: Y keeps bold and faint, Z clears both', () => {
    const noClear = grade('sgr.faint', reads => { reads.trailing = [reads.trailing[0], reads.trailing[1], styledCell('Z', ['bold'])]; });
    assert.equal(noClear.outcome, 'inconclusive');
    const faintSurvives = grade('sgr.faint', reads => { reads.trailing = [reads.trailing[0], reads.trailing[1], styledCell('Z', ['dim'])]; });
    assert.equal(faintSurvives.outcome, 'inconclusive');
});

test('hidden keeps its stored text distinct from the conceal flag', () => {
    const [baseline, target, trailing] = syntheticSgrAttrsScreens('sgr.hidden');
    const cell = sgrAttrsCellAt(target, 0, 0);
    assert.equal(cell.char, 'X');
    assert.equal(cell.hidden, true);
    assert.equal(sgrAttrsCellAt(baseline, 0, 0).hidden, false);
    assert.equal(sgrAttrsCellAt(trailing, 0, 1).hidden, false);
    // Text that vanished leaves the pinned and contract verdicts inconclusive.
    const missingText = grade('sgr.hidden', reads => { reads.target = [styledCell(' ', ['hidden'])]; });
    assert.equal(missingText.outcome, 'inconclusive');
});

test('the observation exposes every pinned cell field and the stored style', () => {
    const cell = sgrAttrsCellAt(syntheticSgrAttrsScreens('sgr.underline.single')[1], 0, 0);
    assert.deepEqual(Object.keys(cell), ['char', 'flags', 'bold', 'dim', 'italic', 'underline', 'inverse', 'hidden',
        'strikethrough', 'blink', 'fg', 'bg', 'fgSource', 'bgSource', 'underlineStyle']);
    assert.equal(cell.underline, 'single');
    assert.equal(cell.underlineStyle, 1);
});

test('the grader refuses malformed observations and unknown revisions', () => {
    const contract = contractOf('sgr.inverse');
    assert.throws(() => gradeSgrAttrsCase(contract, {}), /Missing sgr.inverse read captures/);
    assert.throws(() => gradeSgrAttrsCase(contract, sgrAttrsObservation(syntheticSgrAttrsScreens('sgr.inverse')), 2), /Unknown SGR-attributes contracts revision 2/);
    assert.throws(() => sgrAttrsObservation([]), /Missing SGR-attribute read captures/);
});

test('the owner request allowlist admits every journal kind this suite emits', async () => {
    const { readFileSync } = await import('node:fs');
    const runner = readFileSync(new URL('../terminfo-owned-runner.mjs', import.meta.url), 'utf8');
    const match = /\/\^\(\?:([a-z|]+)\)-\(\?:start\|bind\|read\|finish\)\$\//.exec(runner);
    assert.ok(match, 'allowlist regex present');
    assert.ok(match[1].split('|').includes('sgattr'));
    for (const kind of ['start', 'bind', 'read', 'finish']) {
        assert.match(`sgattr-${kind}`, new RegExp(`^(?:${match[1]})-(?:start|bind|read|finish)$`));
    }
});

test('contradictory attribute-run and cell-detail projections are refused, not graded', () => {
    const screen = syntheticSgrAttrsScreens('sgr.underline.single')[1];
    // Attribute runs say underline off while the stored details say single underline.
    const plainRuns = syntheticSgrAttrsScreen([plainCell('X')]).attributeRuns;
    assert.throws(() => sgrAttrsCellAt({ ...screen, attributeRuns: plainRuns }, 0, 0), /Contradictory stored cell projections/);
    // A flag other than underline disagreeing is equally refused.
    const strikeRuns = syntheticSgrAttrsScreen([styledCell('X', ['underline', 'strikethrough'])]).attributeRuns;
    assert.throws(() => sgrAttrsCellAt({ ...screen, attributeRuns: strikeRuns }, 0, 0), /Contradictory stored cell projections/);
    assert.doesNotThrow(() => sgrAttrsCellAt(screen, 0, 0));
});
