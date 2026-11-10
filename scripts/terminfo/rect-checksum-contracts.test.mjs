import assert from 'node:assert/strict';
import { test } from 'node:test';
import { rectChecksumContracts, rectChecksumCaseIds, rectChecksumFixture, rectChecksumModel, rectChecksumReads, rectChecksumFeeds,
    validateRectChecksumFeeds, gradeRectChecksumCase, rectChecksumObservation } from './rect-checksum-contracts.mjs';
import { syntheticRectChecksumGrade, syntheticRectChecksumReads, syntheticRectChecksumScreens } from './fixtures/rect-checksum-evidence.mjs';
import { plainCell, colorCell } from './fixtures/col-edit-evidence.mjs';

const contract = rectChecksumContracts[0];
const { modelReply, rectangleOf, cellValue } = rectChecksumModel;

test('one pinned case whose plan replays the pinned seed and request byte for byte within the observer budget', () => {
    assert.deepEqual([...rectChecksumCaseIds], ['editing.decrqcra']);
    assert.deepEqual([...contract.pinnedFeeds], ['ABCDE\x1b[1;1H', '\x1b[1;1;1;1;1;5*y']);
    const feeds = rectChecksumFeeds(contract);
    assert.equal(feeds[1], 'ABCDE\x1b[1;1H');
    assert.equal(feeds[2], '\x1b[1;1;1;1;1;5*y');
    assert.equal(contract.plan.find(step => step.feed === '\x1b[1;1;1;1;1;5*y').capture, true);
    assert.equal(contract.plan.find(step => step.feed === '\x1b[1;1;1;1;1;1*y').capture, true, 'the pinned terminal-path spelling is a graded exchange');
    // start, bind and finish plus every read must fit the 64-capture budget of the shared observer.
    assert.ok(rectChecksumReads(contract) + 3 <= 64, String(rectChecksumReads(contract)));
    assert.equal(contract.roles[contract.pinnedRead], 'pinned');
    assert.equal(contract.roles[contract.seedRead], 'seed');
    assert.throws(() => validateRectChecksumFeeds(contract, feeds.slice(1)), /do not match/);
    validateRectChecksumFeeds(contract, feeds);
});

test('every request is spelled exactly, and only the exact identity is admitted', () => {
    const bytes = Object.fromEntries(contract.requests.map(item => [item.role, item.bytes]));
    assert.equal(bytes['page-omitted'], '\x1b[7*y');
    assert.equal(bytes['page-partial'], '\x1b[7;;3;5*y');
    assert.equal(bytes['id-omitted'], '\x1b[;1;1;1;1;5*y');
    assert.equal(bytes['attr-bold'], '\x1b[2;1;3;3;3;3*y');
    const rejected = contract.requests.filter(item => !item.admitted).map(item => item.bytes);
    assert.deepEqual(rejected, ['\x1b[?1;1;1;1;1;5*y', '\x1b[>1;1;1;1;1;5*y', '\x1b[1;1;1;1;1;5;1*y', '\x1b[1;1;1:2;1;1;5*y', '\x1b[1;1;1;1;1;5$y',
        '\x1b[1;1;1;1;1;5 *y']);
});

test('the model sums characters and the four xterm weights, nothing else', () => {
    const cell = (char, flags = {}) => ({ char, bold: false, blink: false, inverse: false, underline: false, italic: false, dim: false, ...flags });
    assert.equal(cellValue(cell('A')), 0x41);
    assert.equal(cellValue(cell(' ')), 0x20);
    assert.equal(cellValue(cell('A', { bold: true })), 0x41 + 0x80);
    assert.equal(cellValue(cell('A', { blink: true })), 0x41 + 0x40);
    assert.equal(cellValue(cell('A', { inverse: true })), 0x41 + 0x20);
    assert.equal(cellValue(cell('A', { underline: 'curly' })), 0x41 + 0x10);
    assert.equal(cellValue(cell('A', { italic: true, dim: true })), 0x41);
    assert.equal(cellValue(cell('é')), null, 'outside the ASCII scope');
});

test('rectangles take the page defaults, the DECOM row offset and page clipping', () => {
    const screen = { rows: 13, cols: 60, bufferState: { originMode: false, scrollTop: 0 } };
    assert.deepEqual(rectangleOf([7], screen), { top: 0, left: 0, bottom: 12, right: 59 });
    assert.deepEqual(rectangleOf([7, 0, 0, 0, 0, 0], screen), { top: 0, left: 0, bottom: 12, right: 59 });
    assert.deepEqual(rectangleOf([7, undefined, 3, 5], screen), { top: 2, left: 4, bottom: 12, right: 59 });
    assert.deepEqual(rectangleOf([7, 1, 1, 1, 999, 999], screen), { top: 0, left: 0, bottom: 12, right: 59 });
    const origin = { ...screen, bufferState: { originMode: true, scrollTop: 2 } };
    assert.deepEqual(rectangleOf([7, 1, 1, 1, 2, 12], origin), { top: 2, left: 0, bottom: 3, right: 11 });
    assert.deepEqual(rectangleOf([7, 1, 1, 1, 99, 999], origin), { top: 2, left: 0, bottom: 12, right: 59 }, 'clipped to the page, not the margins');
});

test('the model reproduces the pinned FEB1 and the hand-computed replies of the synthetic fixture', () => {
    const screens = syntheticRectChecksumScreens();
    const seed = screens.find(item => item.role === 'seed').screen;
    assert.equal(modelReply([1, 1, 1, 1, 1, 5], seed), '\x1bP1!~FEB1\x1b\\');
    assert.equal(modelReply([1, 1, 1, 1, 1, 1], seed), '\x1bP1!~FFBF\x1b\\');
    assert.equal(modelReply([7, 1, 1, 5, 1, 1], seed), '\x1bP7!~0000\x1b\\', 'an inverted rectangle sums nothing');
    assert.equal(modelReply([undefined, 1, 1, 1, 1, 5], seed), '\x1bP0!~FEB1\x1b\\', 'an omitted request id is 0');
    const reads = syntheticRectChecksumReads();
    for (const item of contract.requests.filter(request => request.admitted)) {
        const index = contract.roles.indexOf(item.role);
        const before = screens.slice(0, index).filter(read => read.reply === undefined).at(-1).screen;
        assert.equal(modelReply(item.params, before), reads[item.role].reply, item.role);
    }
});

test('the synthetic run grades supported', () => {
    assert.deepEqual(syntheticRectChecksumGrade(), { outcome: 'supported' });
});

const replyOf = role => (_, read) => (_ === role ? read : null);
const mutateReply = (role, reply) => (name, read) => (name === role ? { ...read, reply } : null);
for (const [label, mutate, outcome, note] of [
    ['a wrong checksum', mutateReply('attr-bold', '\x1bP2!~FFBE\x1b\\'), 'unsupported', /attr-bold reply differs from the checksum model/],
    ['an ignored attribute weight', mutateReply('attr-reverse', '\x1bP2!~FFAE\x1b\\'), 'unsupported', /attr-reverse reply differs/],
    ['lower-case hex digits', mutateReply('pinned', '\x1bP1!~feb1\x1b\\'), 'unsupported', /pinned reply differs/],
    ['a wrong request id', mutateReply('id-largest', '\x1bP65534!~0000\x1b\\'), 'unsupported', /id-largest reply differs/],
    ['an unanswered admitted request', mutateReply('page-partial', null), 'unsupported', /page-partial request was not answered/],
    ['an answered non-admitted spelling', mutateReply('identity-dollar', '\x1bP1!~FEB1\x1b\\'), 'unsupported', /identity-dollar request is not the admitted DECRQCRA identity/],
    ['margins clipping the rectangle', mutateReply('origin-clip', '\x1bP7!~0000\x1b\\'), 'unsupported', /origin-clip reply differs/],
    ['a request that moved the cursor', (name, read) => (name === 'terminal' ? { ...read, cursor: [0, 1] } : null), 'unsupported', /terminal request changed the screen/],
    ['a request that wrote a cell', (name, read) => (name === 'page-zero' ? { ...read, rows: [[...'ABCDE', 'Z'].map(char => plainCell(char)), [], read.rows[2]] } : null), 'unsupported', /page-zero request changed/],
    ['a missing pinned seed', (name, read) => (name === 'seed' ? { ...read, rows: [[...'ABCD'].map(char => plainCell(char))] } : null), 'inconclusive', /pinned seed ABCDE was not measured/],
    ['a missing attribute seed', (name, read) => (name === 'attrSeed' ? { ...read, rows: [read.rows[0], [], read.rows[2].map((cell, index) => (index === 2 ? plainCell('B') : cell))] } : null), 'inconclusive', /attribute seed was not measured/],
    ['unmeasured origin margins', (name, read) => (name === 'originSeed' ? { ...read, state: undefined } : null), 'inconclusive', /origin-mode margins were not measured/],
    ['a non-ASCII cell in a rectangle', (name, read) => (['attrSeed', 'originSeed', ...contract.roles.slice(contract.roles.indexOf('attrSeed') + 1)].includes(name)
        ? { ...read, rows: [read.rows[0], [colorCell('é')], read.rows[2]] } : null), 'inconclusive', /outside the stored ASCII scope/],
]) {
    test('the grader refuses ' + label, () => {
        const result = syntheticRectChecksumGrade(undefined, mutate);
        assert.equal(result.outcome, outcome);
        assert.match(result.note, note);
    });
}

test('reverse video that is not proven off is inconclusive', () => {
    const reads = syntheticRectChecksumScreens();
    const screens = reads.map(read => read.screen);
    screens[3] = { ...screens[3], colorBinding: { ...screens[3].colorBinding, reverseVideo: null } };
    const result = gradeRectChecksumCase(contract, rectChecksumObservation(contract, screens, reads.map(read => read.reply ?? null)));
    assert.equal(result.outcome, 'inconclusive');
    assert.match(result.note, /reverse video/);
});

test('the fixture facts name the attribute row, the origin margins and every group', () => {
    assert.equal(rectChecksumFixture.attrRow, 3);
    assert.deepEqual([rectChecksumFixture.originTop, rectChecksumFixture.originBottom], [2, 5]);
    assert.equal(rectChecksumFixture.groups.length, contract.roles.length);
    assert.equal(replyOf('x')('y', {}), null);
});
