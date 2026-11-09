import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync, existsSync } from 'node:fs';
import { charsetsContracts, charsetsCaseIds, charsetsContractsVersion, charsetsSuiteId, charsetsNoncePrefix, charsetsReset, charsetsClear,
    charsetsRow, charsetsCellAt, charsetsInk, charsetsObservation, gradeCharsetsCase, validateCharsetsScreen, validateCharsetsFeeds,
    charsetsFeeds, charsetsReads } from './charsets-contracts.mjs';
import { charsetsSeams } from './charsets-grading-collector.mjs';
import { syntheticCharsetsReads, syntheticCharsetsScreen, syntheticCharsetsScreens, syntheticCharsetsGrade } from './fixtures/charsets-evidence.mjs';

const UTF8 = 'charsets.utf8', SPECIAL = 'charsets.dec-special', LINES = 'charsets.dec-line-drawing', G01 = 'charsets.g0-g1-switching';
const contractOf = id => charsetsContracts.find(item => item.caseId === id);
/** The synthetic grade after editing one case's reads. */
const gradeWith = (caseId, edit) => syntheticCharsetsGrade(caseId, id => {
    const reads = syntheticCharsetsReads(id);
    if (id === caseId) edit(reads);
    return reads;
});

test('exactly the four pinned ids in suite order with versioned identity', () => {
    assert.deepEqual([...charsetsCaseIds], [UTF8, SPECIAL, LINES, G01]);
    assert.equal(charsetsContractsVersion, 1);
    assert.equal(charsetsSuiteId, 'ntilde-owned-charsets-graded-v1');
    assert.equal(charsetsNoncePrefix, 'NTILDE_CS_');
    for (const contract of charsetsContracts) assert.equal(contract.probeId, contract.caseId);
    assert.throws(() => gradeCharsetsCase(contractOf(UTF8), charsetsObservation(contractOf(UTF8), syntheticCharsetsScreens(UTF8)), 2), /Unknown charsets contracts revision/);
});

test('the pinned fixture bytes are exactly the bytes the pinned bundle feeds, in order, with the restoration bytes last', () => {
    // The spellings are typed here independently of the contract, then compared with the literals inside the pinned registrations.
    const expected = {
        [SPECIAL]: ['\x0F\x1B(B\x1B)B\x1B[1;1H\x1B[2Kqqq', '\x1B[1;2H', '\x1B(0q\x1B(Bq', '\x0F\x1B(B\x1B)B'],
        [LINES]: ['\x0F\x1B(B\x1B)B\x1B[1;1H\x1B[2Kjklmqxj', '\x1B[1;1H', '\x1B(0jklmqx\x1B(B', '\x0F\x1B(B\x1B)B'],
        [G01]: ['\x0F\x1B(B\x1B)B\x1B[1;1H\x1B[2KAlllZ', '\x1B[1;2H', '\x1B(B\x1B)0l\x0El\x0Fl', '\x0F\x1B(B\x1B)B'],
        [UTF8]: ['\x1B[1;1H\x1B[2KAxy?Z', '\x1B[1;2H', 'é', '\x1B[1;3H', '世'],
    };
    for (const contract of charsetsContracts) {
        const all = contract.pinnedFinally === null ? [...contract.pinnedFeeds] : [...contract.pinnedFeeds, contract.pinnedFinally];
        assert.deepEqual(all, expected[contract.caseId], contract.caseId);
        // The same bytes are the literal ctx.feed(...) arguments of the pinned termless function (the capture path writes different bytes).
        const registration = charsetsSeams.find(seam => seam.includes(`probe("${contract.caseId}"`));
        const termless = registration.slice(0, registration.indexOf('async (ctx)'));
        const literals = [...termless.matchAll(/ctx\.feed\("((?:[^"\\]|\\.)*)"\)/g)].map(match => Function(`return "${match[1]}"`)());
        assert.deepEqual(literals, expected[contract.caseId], `${contract.caseId} pinned source`);
    }
});

test('the UTF-8 samples keep their code points: U+00E9 (two UTF-8 bytes) and U+4E16 (three) are fed as exact text', () => {
    const [, , e, , wide] = contractOf(UTF8).pinnedFeeds;
    assert.equal(e.codePointAt(0), 0x00e9);
    assert.equal(wide.codePointAt(0), 0x4e16);
    assert.deepEqual([...Buffer.from(e)], [0xc3, 0xa9]);
    assert.deepEqual([...Buffer.from(wide)], [0xe4, 0xb8, 0x96]);
    assert.equal(e, e.normalize('NFC'));
    assert.notEqual('é', e);
});

test('every plan starts with the measured reset, replays only its own pinned bytes and reads once per group', () => {
    for (const contract of charsetsContracts) {
        const feeds = charsetsFeeds(contract);
        assert.equal(feeds[0], charsetsReset);
        assert.equal(charsetsReads(contract), contract.roles.length);
        assert.equal(new Set(contract.roles).size, contract.roles.length);
        assert.doesNotThrow(() => validateCharsetsFeeds(contract, feeds));
        assert.throws(() => validateCharsetsFeeds(contract, [...feeds.slice(0, -1), feeds.at(-1) + 'x']), /do not match the pinned contract/);
        // Pinned feeds appear in the plan in order, each as its own write.
        let from = 1;
        for (const pinned of contract.pinnedFeeds) {
            const at = feeds.indexOf(pinned, from);
            assert.ok(at >= from, `${contract.caseId} replays ${JSON.stringify(pinned)}`);
            from = at + 1;
        }
        assert.ok(charsetsReset.includes('\x0f\x1b(B\x1b)B'), 'the reset restores G0/G1 to ASCII with G0 invoked');
        assert.ok(charsetsClear.startsWith('\x0f\x1b(B\x1b)B'));
    }
});

test('overlapping fixtures stay separate registered cases: distinct bytes, shared pieces named, no case grades another', () => {
    const byId = Object.fromEntries(charsetsContracts.map(contract => [contract.caseId, contract]));
    assert.deepEqual(byId[SPECIAL].overlaps.map(item => item.with), [LINES]);
    assert.deepEqual(byId[LINES].overlaps.map(item => item.with).sort(), [SPECIAL, G01].sort());
    assert.deepEqual(byId[G01].overlaps.map(item => item.with), [LINES]);
    assert.deepEqual(byId[UTF8].overlaps, []);
    const fixtures = charsetsContracts.map(contract => JSON.stringify(contract.pinnedFeeds));
    assert.equal(new Set(fixtures).size, 4);
    // A case's grader cannot be fed another case's reads.
    for (const contract of charsetsContracts) {
        for (const other of charsetsContracts.filter(item => item.caseId !== contract.caseId)) {
            const screens = syntheticCharsetsScreens(other.caseId);
            if (screens.length === contract.roles.length) {
                const observation = charsetsObservation(contract, screens);
                assert.notEqual(gradeCharsetsCase(contract, observation).outcome, 'supported', `${contract.caseId} given ${other.caseId} reads`);
            } else assert.throws(() => charsetsObservation(contract, screens), /Missing charset read captures/);
        }
    }
});

test('physical cells come from stored details, not UTF-16 offsets: the wide sample shifts text offsets but not columns', () => {
    const screen = syntheticCharsetsScreens(UTF8).at(5);
    assert.equal(screen.lines[0], 'Aé世Z');
    assert.equal(screen.lines[0][3], 'Z', 'the text offset of Z is 3');
    const cells = Array.from({ length: 6 }, (_, col) => charsetsCellAt(screen, 0, col));
    assert.deepEqual(cells.map(cell => cell.char), ['A', 'é', '世', '', 'Z', ' ']);
    assert.deepEqual(cells.map(cell => [cell.wide, cell.continuation]), [[false, false], [false, false], [true, false], [false, true], [false, false], [false, false]]);
    assert.deepEqual(charsetsInk(screen).map(cell => [cell.col, cell.kind]), [[0, 'narrow'], [1, 'narrow'], [2, 'wide'], [3, 'continuation'], [4, 'narrow']]);
    // A UTF-16 offset projection would have put Z at column 3 and a blank at 4: the contract refuses to grade that shape.
    assert.notEqual(charsetsCellAt(screen, 0, 3).char, 'Z');
});

test('incoherent rows are refused: orphan continuation, wide cell without continuation, leftover text, attribute strings, resolved colors', () => {
    const screen = (rows, edit) => syntheticCharsetsScreen(rows, [0, 0], edit);
    assert.throws(() => validateCharsetsScreen(screen(['AB'], details => { details[1] = { ...details[1], flags: details[1].flags | 0x1000 }; })), /Orphan wide continuation/);
    assert.throws(() => validateCharsetsScreen(screen(['AB'], details => { details[1] = { ...details[1], flags: details[1].flags | 0x800 }; })), /has no continuation/);
    assert.throws(() => validateCharsetsScreen(screen(['AB'], details => { details[1] = { ...details[1], flags: details[1].flags | 0x800 | 0x1000 }; })), /both wide and a continuation/);
    const long = screen(['AB']);
    assert.throws(() => validateCharsetsScreen({ ...long, lines: ['x'.repeat(61), ...long.lines.slice(1)] }), /does not match its physical cells/);
    assert.throws(() => validateCharsetsScreen({ ...long, attributeRuns: [{ count: 360, flags: 0x300, fg: -1, bg: -1 }] }), /must not carry attribute strings/);
    assert.throws(() => validateCharsetsScreen({ ...long, resolvedColors: [] }), /must not carry attribute strings or resolved colors/);
    assert.throws(() => charsetsCellAt(long, 99, 0), /outside the observed screen/);
});

test('extended text makes the rest of the row ambiguous instead of inventing cells', () => {
    const screen = syntheticCharsetsScreen(['éZ'], [0, 2]);
    assert.equal(charsetsCellAt(screen, 0, 0).char, null);
    assert.equal(charsetsCellAt(screen, 0, 0).extended, true);
    assert.equal(charsetsCellAt(screen, 0, 1).char, null, 'the cell after extended text cannot be assigned a character');
    assert.equal(charsetsInk(screen).every(cell => cell.char === null), true);
});

test('every case is supported by a complete, correctly observed synthetic run, with the contract at least as strict as the pinned probe', () => {
    for (const id of charsetsCaseIds) assert.deepEqual(syntheticCharsetsGrade(id), { outcome: 'supported' }, id);
});

test('wrong mappings are unsupported with a diagnosis', () => {
    assert.deepEqual(gradeWith(SPECIAL, reads => { reads.target.rows = ['q│q']; }),
        { outcome: 'unsupported', note: 'The decoded cells differ from the contracted code points or cell structure' });
    assert.match(gradeWith(SPECIAL, reads => { reads.target.rows = ['qqq']; }).note, /designation had no effect/);
    assert.match(gradeWith(SPECIAL, reads => { reads.target.rows = ['q──']; }).note, /stale charset state/);
    assert.equal(gradeWith(LINES, reads => { reads.target.rows = ['┐┘┌└─│j']; }).outcome, 'unsupported');
    assert.match(gradeWith(LINES, reads => { reads.target.rows = ['jklmqxj']; }).note, /designation had no effect/);
    assert.match(gradeWith(LINES, reads => { reads.target.rows = ['┘┐┌└─│┘']; }).note, /trailing ASCII j/);
});

test('wrong designation and invocation are unsupported: unswitched banks, SI that does not return, bank/designation confusion', () => {
    assert.match(gradeWith(G01, reads => { reads.target.rows = ['AlllZ']; }).note, /SO selected no mapping/);
    assert.match(gradeWith(G01, reads => { reads.target.rows = ['Al┌┌Z']; }).note, /SI did not return to G0 ASCII/);
    assert.match(gradeWith(G01, reads => { reads.target.rows = ['A┌┌lZ']; }).note, /designation applied without SO/);
    assert.match(gradeWith(G01, reads => { reads.designateOnly.rows = ['A┌┌┌Z']; }).note, /designation was treated as a bank selection/);
    assert.match(gradeWith(G01, reads => { reads.shiftOnly.rows = ['A┌┌┌Z']; }).note, /shift was treated as a designation/);
    assert.match(gradeWith(G01, reads => { reads.g0Direct.rows = ['AlllZ']; }).note, /directly into G0 did not map/);
    assert.match(gradeWith(G01, reads => { reads.g1Invoked.rows = ['AlllZ']; }).note, /SO with DEC graphics designated into G1 did not map/);
    assert.match(gradeWith(G01, reads => { reads.persistShift.rows = ['Al┌lZ', 'jkl']; }).note, /designation did not persist after SI/);
    assert.match(gradeWith(SPECIAL, reads => { reads.designateG1.rows = ['───']; }).note, /bank was confused with a designation/);
    assert.match(gradeWith(LINES, reads => { reads.designateG1.rows = ['┘┐┌└─│']; }).note, /bank was confused with a designation/);
});

test('an uppercase control shows DEC mapping is character-specific, and an uppercase-only marker never proves restoration', () => {
    assert.match(gradeWith(SPECIAL, reads => { reads.upper.rows = ['───']; }).note, /Uppercase source characters changed/);
    assert.match(gradeWith(LINES, reads => { reads.upper.rows = ['┘┐┌└─│']; }).note, /Uppercase source characters changed/);
    // Stale DEC state after the fixture: A and Z still read fine (uppercase never maps), but the lowercase witness maps, so no conclusive grade.
    for (const id of [UTF8, SPECIAL, LINES, G01]) {
        const stale = gradeWith(id, reads => {
            const target = reads.target.rows[0];
            assert.ok(/^[A-Zq─-┘jlé]/.test(target) || target.startsWith('A'), 'the fixture row keeps its uppercase or ASCII control cells');
            reads.witnessAscii.rows = [reads.witnessAscii.rows[0], '┘┐┌└─│┌'];
        });
        assert.equal(stale.outcome, 'inconclusive', id);
        assert.match(stale.note, /charset restoration is incomplete/, id);
    }
    const marker = gradeWith(G01, reads => { reads.witnessAscii.rows = [reads.witnessAscii.rows[0], 'AZAZAZA']; });
    assert.equal(marker.outcome, 'inconclusive', 'uppercase text in the witness row is not the lowercase source characters');
});

test('restoration after returning from DEC graphics must hold: the after witness and the pinned restoration bytes', () => {
    for (const id of charsetsCaseIds) {
        const witness = gradeWith(id, reads => { reads.witnessAfter.rows[3] = '┘┐┌└─│┌'; });
        assert.equal(witness.outcome, 'inconclusive', id);
        assert.match(witness.note, /returning from DEC graphics|incomplete/, id);
    }
    assert.match(gradeWith(SPECIAL, reads => { reads.restored.rows = ['q─q', 'x']; }).note, /pinned restoration bytes changed cells or the cursor/);
    assert.equal(gradeWith(G01, reads => { reads.restored.cursor = [0, 5]; }).outcome, 'inconclusive');
    // The DEC witness proves the witness characters would map: if it does not, the restoration witness cannot discriminate.
    assert.equal(gradeWith(G01, reads => { reads.witnessDec.rows[2] = 'jklmqxl'; }).outcome, 'inconclusive');
    assert.equal(gradeWith(SPECIAL, reads => { reads.witnessDec.rows[2] = 'jklmqxl'; }).outcome, 'unsupported');
});

test('the UTF-8 fixture needs the exact code points, a wide cell with its continuation and untouched ASCII controls', () => {
    assert.equal(gradeWith(UTF8, reads => { reads.target.rows = ['Ae世Z']; }).outcome, 'unsupported');
    assert.equal(gradeWith(UTF8, reads => { reads.target.rows = ['AééZ']; }).outcome, 'unsupported');
    assert.equal(gradeWith(UTF8, reads => { reads.target.rows = ['Aé世Y']; }).outcome, 'unsupported');
    assert.equal(gradeWith(UTF8, reads => { reads.target.rows = ['Ã©世Z']; }).outcome, 'unsupported', 'mis-decoded UTF-8 bytes');
    assert.match(gradeWith(UTF8, reads => { reads.afterE.rows = ['AÃ©y?Z']; }).note, /two UTF-8 bytes of U\+00E9/);
    assert.match(gradeWith(UTF8, reads => { reads.target.rows = ['Axy?Z']; }).note, /not stored/);
    // Cell-position mistake: the wide sample stored narrow, with the old ? still in column 3.
    const narrow = gradeWith(UTF8, reads => { reads.target.rows = ['Aé世?Z']; });
    assert.equal(narrow.outcome, 'unsupported');
    assert.match(narrow.note, /did not claim a continuation cell/);
    // Z overwritten by the wide character's continuation handling.
    assert.match(gradeWith(UTF8, reads => { reads.target.rows = ['Aé世']; }).note, /following ASCII cell was overwritten/);
    assert.equal(gradeWith(UTF8, reads => { reads.target.cursor = [0, 3]; }).outcome, 'unsupported');
    assert.equal(gradeWith(UTF8, reads => { reads.wideAlone.rows = ['A世', 'Z']; }).outcome, 'unsupported');
    assert.match(gradeWith(UTF8, reads => { reads.precomposed.rows = ['eZ']; }).note, /Precomposed U\+00E9/);
});

test('normalization and extended text: the decomposed control is observed, never graded; a decomposed fixture sample is ambiguous', () => {
    const observed = gradeWith(UTF8, reads => { reads.decomposed.rows = ['Q']; reads.decomposed.cursor = [0, 1]; });
    assert.equal(observed.outcome, 'supported', 'an observed-only role cannot change the grade');
    const decomposed = gradeWith(UTF8, reads => { reads.target.rows = ['Aé世Z']; });
    assert.equal(decomposed.outcome, 'inconclusive');
    assert.match(decomposed.note, /Ambiguous physical cells \(extended text\) in target/);
    // A normalized (precomposed) cell can never read as decomposed text and vice versa: the flag is part of the cell identity.
    const precomposedExtended = gradeWith(UTF8, reads => { reads.precomposed.rows = ['éZ']; });
    assert.equal(precomposedExtended.outcome, 'inconclusive');
});

test('unrelated state drift: stray ink, a moved cursor or a non-neutral attribute changes the grade', () => {
    assert.equal(gradeWith(G01, reads => { reads.target.rows = ['Al┌lZ', '', '', 'x']; }).outcome, 'unsupported');
    assert.equal(gradeWith(LINES, reads => { reads.target.cursor = [0, 5]; }).outcome, 'unsupported');
    for (const id of charsetsCaseIds) {
        const bold = gradeWith(id, reads => { reads.target.edit = details => { details[0] = { ...details[0], flags: details[0].flags | 0x0001 }; }; });
        assert.equal(bold.outcome, 'unsupported', `${id} bold attribute on a fixture cell`);
        const colored = gradeWith(id, reads => { reads.target.edit = details => { details[1] = { ...details[1], fg: 4, flags: (details[1].flags & ~0x100) | 0x4000 }; }; });
        assert.equal(colored.outcome, 'unsupported', `${id} stored foreground drift`);
    }
});

test('missing or unusable control evidence is inconclusive, never unsupported', () => {
    for (const id of charsetsCaseIds) {
        assert.equal(gradeWith(id, reads => { reads.initial.rows = ['x']; }).outcome, 'inconclusive', `${id} dirty initial screen`);
        assert.equal(gradeWith(id, reads => { reads.seed.rows = ['xxxxxxxx']; }).outcome, 'inconclusive', `${id} seed`);
        assert.equal(gradeWith(id, reads => { (reads.cup ?? reads.cup1).cursor = [0, 9]; }).outcome, 'inconclusive', `${id} cup`);
    }
    // Stale DEC state before the fixture: the lowercase seed characters of the line-drawing fixture read as box drawing.
    const staleSeed = gradeWith(LINES, reads => { reads.seed.rows = ['┘┐┌└─│┘']; });
    assert.equal(staleSeed.outcome, 'inconclusive');
    assert.equal(staleSeed.note, 'ASCII seed was not measured before line drawing');
});
