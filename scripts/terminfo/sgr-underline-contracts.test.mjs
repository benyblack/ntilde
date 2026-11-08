import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sgrUnderlineContracts, sgrUnderlineCaseIds, sgrUnderlineContractsVersion, sgrUnderlineLadder, sgrUnderlineSuiteId,
    sgrUnderlineNoncePrefix, gradeSgrUnderlineCase, sgrUnderlineObservation, sgrUnderlineFeeds, sgrUnderlineReads,
    validateSgrUnderlineFeeds, pinnedNoRenderedCellNote } from './sgr-underline-contracts.mjs';
import { sgrAttrsContracts } from './sgr-attrs-contracts.mjs';
import { syntheticSgrUnderlineReads, syntheticSgrUnderlineScreens, syntheticSgrUnderlineGrade, styled, colorCell, plainCell,
    paletteBinding } from './fixtures/sgr-underline-evidence.mjs';
import { paletteChannel, literalChannel } from './fixtures/sgr-palette-evidence.mjs';

const byId = id => sgrUnderlineContracts.find(contract => contract.caseId === id);
const grade = (caseId, edit, binding) => syntheticSgrUnderlineGrade(caseId, binding, id => {
    const reads = syntheticSgrUnderlineReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});

test('the contracts pin exactly the four verified feature ids, spellings and exact-style checks', () => {
    assert.deepEqual([...sgrUnderlineCaseIds], ['sgr.underline.double', 'sgr.underline.curly', 'sgr.underline.dotted', 'sgr.underline.dashed']);
    assert.deepEqual(sgrUnderlineContracts.map(contract => [contract.sequence, contract.spelling, contract.style, contract.styleCode, contract.pinnedCheckSource]), [
        ['\x1b[21m', 'SGR 21', 'double', 2, 'cell.underline === "double"'],
        ['\x1b[4:3m', 'SGR 4:3', 'curly', 3, 'cell.underline === "curly"'],
        ['\x1b[4:4m', 'SGR 4:4', 'dotted', 4, 'cell.underline === "dotted"'],
        ['\x1b[4:5m', 'SGR 4:5', 'dashed', 5, 'cell.underline === "dashed"'],
    ]);
    assert.equal(sgrUnderlineContractsVersion, 1);
    assert.equal(sgrUnderlineSuiteId, 'ntilde-owned-sgr-underline-styles-graded-v1');
    assert.equal(sgrUnderlineNoncePrefix, 'NTILDE_SU_');
    for (const contract of sgrUnderlineContracts) {
        assert.deepEqual([...contract.pinnedFeeds], [contract.sequence + 'X']);
        assert.equal(contract.pinnedExpected, `${contract.caseId}: attribute applied to the rendered X cell`);
        assert.match(contract.appearanceScope, /not claimed/);
        assert.equal(sgrUnderlineReads(contract), 4);
        assert.doesNotThrow(() => validateSgrUnderlineFeeds(contract, sgrUnderlineFeeds(contract)));
        assert.throws(() => validateSgrUnderlineFeeds(contract, [...sgrUnderlineFeeds(contract).slice(0, -1), 'x']), /do not match/);
    }
});

test('the pinned checks accept only their own exact style; none is the generic truthy single-underline check', () => {
    const names = ['single', 'double', 'curly', 'dotted', 'dashed', false];
    for (const contract of sgrUnderlineContracts) {
        for (const name of names) assert.equal(contract.pinnedCheck({ underline: name }), name === contract.style, `${contract.caseId} vs ${name}`);
    }
    // The single case lives in the attribute batch and accepts any underline; these four never do.
    assert.equal(sgrAttrsContracts[0].pinnedCheck({ underline: 'double' }), true);
});

test('SGR 21 and SGR 4:2 are distinct spellings: only SGR 21 is graded, 4:2 is a labeled control', () => {
    assert.equal(byId('sgr.underline.double').spelling, 'SGR 21');
    assert.ok(!sgrUnderlineContracts.some(contract => contract.sequence.includes('4:2')));
    const spellings = sgrUnderlineLadder.map(entry => entry.spelling);
    assert.ok(spellings.includes('SGR 21') && spellings.includes('SGR 4:2'));
    assert.deepEqual(sgrUnderlineLadder.map(entry => [entry.spelling, entry.style]), [
        ['SGR 4', 'single'], ['SGR 4:1', 'single'], ['SGR 21', 'double'], ['SGR 4:2', 'double'],
        ['SGR 4:3', 'curly'], ['SGR 4:4', 'dotted'], ['SGR 4:5', 'dashed']]);
    // A broken 21 with a working 4:2 is an observed SGR 21 mismatch, never a double-underline pass.
    const result = grade('sgr.underline.double', reads => {
        reads.target = [styled('X', 'single')]; reads.trailing = [styled('X', 'single'), plainCell('Y')];
        reads.ladder[2] = styled('R', 'single');
    });
    assert.deepEqual(result, { outcome: 'unsupported', note: 'SGR 21 stored the single underline style instead of double' });
    // A working 21 with a broken 4:2 control still grades SGR 21 as supported: the control carries no grade.
    const control = grade('sgr.underline.double', reads => { reads.ladder[3] = styled('S', 'single'); });
    assert.deepEqual(control, { outcome: 'supported' });
});

test('every case is supported on exact stored style with all controls valid', () => {
    for (const id of sgrUnderlineCaseIds) assert.deepEqual(syntheticSgrUnderlineGrade(id), { outcome: 'supported' }, id);
});

test('a wrong-style substitution is unsupported and names the stored style', () => {
    for (const contract of sgrUnderlineContracts) {
        for (const other of ['single', 'double', 'curly', 'dotted', 'dashed'].filter(style => style !== contract.style)) {
            const result = grade(contract.caseId, reads => {
                reads.target = [styled('X', other)]; reads.trailing = [styled('X', other), plainCell('Y')];
            });
            assert.deepEqual(result, { outcome: 'unsupported', note: `${contract.spelling} stored the ${other} underline style instead of ${contract.style}` }, `${contract.caseId} ${other}`);
        }
    }
});

test('a generic underline flag (single) is not the requested style, and no underline at all is unsupported', () => {
    const generic = grade('sgr.underline.curly', reads => { reads.target = [styled('X', 'single')]; reads.trailing = [styled('X', 'single'), plainCell('Y')]; });
    assert.equal(generic.outcome, 'unsupported');
    assert.match(generic.note, /stored the single underline style instead of curly/);
    const none = grade('sgr.underline.dashed', reads => { reads.target = [plainCell('X')]; reads.trailing = [plainCell('X'), plainCell('Y')]; });
    assert.deepEqual(none, { outcome: 'unsupported', note: 'The stored underline attribute was not set after SGR 4:5' });
});

test('unsupported needs a readback that can tell styles apart; a collapsed ladder is inconclusive', () => {
    const collapsed = grade('sgr.underline.dotted', reads => {
        reads.target = [styled('X', 'single')]; reads.trailing = [styled('X', 'single'), plainCell('Y')];
        reads.ladder = [...sgrUnderlineLadder.map(entry => styled(entry.char, 'single')), plainCell('N')];
    });
    assert.equal(collapsed.outcome, 'inconclusive');
    assert.match(collapsed.note, /collapsed styles/);
});

test('a style the readback exposes for another spelling does not change a supported grade', () => {
    // 4:4 stores dotted even though the 4:5 ladder cell stores single: the dotted case is still supported.
    assert.equal(grade('sgr.underline.dotted', reads => { reads.ladder[6] = styled('V', 'single'); }).outcome, 'supported');
});

test('the missing rendered X cell and DECSCNM make the case inconclusive before any verdict', () => {
    assert.deepEqual(grade('sgr.underline.double', reads => { reads.target = [styled('Z', 'double')]; reads.trailing = [styled('Z', 'double'), plainCell('Y')]; }),
        { outcome: 'inconclusive', note: pinnedNoRenderedCellNote });
    const reversed = grade('sgr.underline.double', null, { ...paletteBinding(), reverseVideo: true });
    assert.equal(reversed.outcome, 'inconclusive');
    assert.match(reversed.note, /DECSCNM/);
});

test('an initially active attribute or color invalidates the neutral baseline', () => {
    for (const baseline of [styled('C', 'single'), colorCell('C', { attrs: ['bold'] }), colorCell('C', { fg: paletteChannel(1) }),
        styled('C', 'double', { underlineColorIndex: 3 }), colorCell('C', { attrs: ['overline'] }), plainCell('Q')]) {
        const result = grade('sgr.underline.curly', reads => { reads.baseline = [baseline]; });
        assert.equal(result.outcome, 'inconclusive', JSON.stringify(baseline));
        assert.match(result.note, /Neutral baseline|rendered X/);
    }
});

test('unrelated attribute, color and underline-color drift on the target is inconclusive, never unsupported', () => {
    const drifts = [{ attrs: ['bold'] }, { attrs: ['dim'] }, { attrs: ['inverse'] }, { attrs: ['italic'] }, { attrs: ['strikethrough'] },
        { attrs: ['hidden'] }, { attrs: ['blink'] }, { attrs: ['overline'] }, { fg: paletteChannel(2) }, { bg: paletteChannel(4) },
        { fg: literalChannel(1, 2, 3) }, { bg: literalChannel(9, 8, 7) }, { underlineColorIndex: 5 }, { underlineArgb: 0xffff0080 }];
    for (const drift of drifts) {
        for (const style of ['double', 'single']) { // a matching style and a wrong one: drift voids both
            const result = grade('sgr.underline.double', reads => { reads.target = [styled('X', style, drift)]; reads.trailing = [reads.target[0], plainCell('Y')]; });
            assert.equal(result.outcome, 'inconclusive', JSON.stringify(drift));
            assert.match(result.note, /Unrelated attributes or colors drifted/);
        }
    }
});

test('incomplete restoration (SGR 24) is inconclusive for supported and unsupported targets alike', () => {
    const broken = [
        reads => { reads.trailing = [reads.target[0], styled('Y', 'double')]; },                // Y still underlined
        reads => { reads.trailing = [reads.target[0], styled('Y', 'single')]; },                // generic flag left behind
        reads => { reads.trailing = [reads.target[0], colorCell('Y', { attrs: ['bold'] })]; },  // unrelated drift on Y
        reads => { reads.trailing = [reads.target[0], styled('Y', 'single', { underlineColorIndex: 2 })]; },
        reads => { reads.trailing = [plainCell('X'), plainCell('Y')]; },                         // X lost its style
        reads => { reads.trailing = [styled('X', 'curly'), plainCell('Y')]; },                   // X changed style
        reads => { reads.trailing = [reads.target[0], plainCell('Z')]; },                        // Y missing
    ];
    for (const edit of broken) {
        const supported = grade('sgr.underline.double', edit);
        assert.equal(supported.outcome, 'inconclusive');
        assert.match(supported.note, /Reset\/restoration control/);
        const mismatch = grade('sgr.underline.double', reads => {
            reads.target = [styled('X', 'single')]; reads.trailing = [styled('X', 'single'), plainCell('Y')]; edit(reads);
        });
        assert.equal(mismatch.outcome, 'inconclusive');
    }
});

test('a malformed control ladder is inconclusive: wrong cells, no plain-SGR-4 single, drift or a dirty neutral cell', () => {
    const broken = [
        reads => { reads.ladder[0] = styled('P', 'double'); },                                    // plain SGR 4 not single
        reads => { reads.ladder[0] = plainCell('P'); },
        reads => { reads.ladder[4] = styled('Z', 'curly'); },                                     // wrong character
        reads => { reads.ladder[1] = styled('Q', 'single', { attrs: ['bold'] }); },               // unrelated drift
        reads => { reads.ladder[5] = styled('U', 'dotted', { fg: paletteChannel(1) }); },
        reads => { reads.ladder[2] = styled('R', 'double', { underlineColorIndex: 1 }); },
        reads => { reads.ladder[7] = styled('N', 'single'); },                                    // neutral cell still underlined
        reads => { reads.ladder[7] = colorCell('N', { bg: paletteChannel(2) }); },
        reads => { reads.ladder = reads.ladder.slice(0, 7); },                                    // truncated: neutral cell missing
    ];
    for (const edit of broken) {
        const result = grade('sgr.underline.curly', edit);
        assert.equal(result.outcome, 'inconclusive');
        assert.match(result.note, /control ladder/);
    }
});

test('the grader refuses malformed observations and unknown revisions', () => {
    const contract = byId('sgr.underline.double');
    const observation = sgrUnderlineObservation(contract, syntheticSgrUnderlineScreens('sgr.underline.double'));
    assert.deepEqual(gradeSgrUnderlineCase(contract, observation), { outcome: 'supported' });
    assert.throws(() => gradeSgrUnderlineCase(contract, observation, 2), /Unknown SGR-underline contracts revision/);
    assert.throws(() => gradeSgrUnderlineCase(contract, { ...observation, ladder: observation.ladder.slice(1) }), /Missing/);
    assert.throws(() => gradeSgrUnderlineCase(contract, { ...observation, trailing: observation.trailing.slice(1) }), /Missing/);
    assert.throws(() => gradeSgrUnderlineCase(contract, null), /Missing/);
    assert.throws(() => sgrUnderlineObservation(contract, syntheticSgrUnderlineScreens('sgr.underline.double').slice(1)), /Missing SGR-underline read captures/);
});

test('contradictory stored projections and malformed style/detail combinations are refused outright', () => {
    const contract = byId('sgr.underline.double');
    const screens = syntheticSgrUnderlineScreens('sgr.underline.double');
    const observe = mutate => { const copy = structuredClone(screens); mutate(copy[1]); return sgrUnderlineObservation(contract, copy); };
    // Attribute-run flags say "no underline" while the stored detail holds a style.
    assert.throws(() => observe(screen => { screen.attributeRuns[0].flags &= ~0x08; }), /Contradictory|Invalid|differ/i);
    // The stored detail drops the style while the attribute-run flag still says underlined.
    assert.throws(() => observe(screen => { screen.cellDetails[0].underlineStyle = 0; }), /Contradictory|Invalid|differ/i);
    // An undefined style code.
    assert.throws(() => observe(screen => { screen.cellDetails[0].underlineStyle = 6; }), /Invalid|Contradictory/i);
    // An underline color with no stored underline style.
    assert.throws(() => observe(screen => { screen.cellDetails[0].underlineStyle = 0; screen.attributeRuns[0].flags &= ~0x08; screen.cellDetails[0].flags &= ~0x08; screen.cellDetails[0].underlineColorIndex = 4; }), /Invalid/);
    // A flag/detail disagreement on an unrelated bit.
    assert.throws(() => observe(screen => { screen.cellDetails[0].flags |= 0x01; }), /Contradictory|Invalid|differ/i);
});
