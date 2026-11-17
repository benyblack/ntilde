import assert from 'node:assert/strict';
import test from 'node:test';
import { osc720Bare, osc720CaseId, osc720Experiments, osc720Fixture, osc720Pinned, osc720PinnedLines, osc720ScrollbackState } from './osc720-contracts.mjs';
import { planFeeds, planReads } from './graded-plan.mjs';

const runId = 'b'.repeat(32);

test('the pinned feeds are exactly the bundle’s thirty lines then the bare request, and the terminal writes are exact', () => {
    assert.equal(osc720PinnedLines.length, 30);
    assert.deepEqual(osc720PinnedLines.slice(0, 2), ['scroll-0\r\n', 'scroll-1\r\n']);
    assert.equal(osc720Bare, '\x1b]720\x07');
    assert.deepEqual(osc720Pinned.termless.feeds, [...osc720PinnedLines, osc720Bare]);
    assert.deepEqual(osc720Pinned.terminal.writes, ['\x1b[1;1H\x1b[2K', osc720Bare]);
});

test('every experiment starts from the same bounded history and holds the observer capture budget', () => {
    const fixture = osc720Fixture(runId), contracts = osc720Experiments(runId);
    assert.equal(contracts.length, 10);
    assert.ok(3 + contracts.reduce((sum, item) => sum + planReads(item), 0) <= 64);
    for (const contract of contracts) {
        const feeds = planFeeds(contract);
        assert.deepEqual(feeds.slice(0, 3), [fixture.reset, fixture.filler, fixture.pinnedFeed], contract.caseId);
        assert.equal(planReads(contract), 2);
        assert.deepEqual(contract.plan.filter(step => step.read !== undefined).map(step => step.read), ['before', 'after']);
        assert.equal(contract.owner, osc720CaseId);
    }
    assert.ok(fixture.filler.includes('\x1b[999;1H'), 'the cursor goes to the last row so exactly thirty rows scroll into history');
    assert.equal(fixture.pinnedFeed, osc720PinnedLines.join(''));
    assert.equal(fixture.finish, fixture.reset);
    assert.ok(fixture.reset.includes('\x1b[3J'));
});

test('the protocol experiments use the documented count forms and the controls use other operations', () => {
    const byName = Object.fromEntries(osc720Experiments(runId).map(item => [item.name, item]));
    assert.equal(byName['pinned-bare'].probe, '\x1b]720\x07');
    assert.deepEqual(byName['pinned-bare'].expected, { kind: 'none' }, 'the bare form has no `;`, so urxvt does not dispatch it');
    assert.equal(byName['count-empty'].probe, '\x1b]720;\x07');
    assert.deepEqual(byName['count-empty'].expected, { kind: 'erase-saved' });
    assert.equal(byName['count-zero'].probe, '\x1b]720;0\x07');
    assert.equal(byName['count-3'].probe, '\x1b]720;3\x07');
    assert.equal(byName['count-3-st'].probe, '\x1b]720;3\x1b\\');
    assert.deepEqual(byName['count-clamped'].expected, { kind: 'view-up', count: 100000 });
    assert.equal(byName['count-2-from-scrolled'].setup.scrolled, true);
    assert.deepEqual(byName['count-2-from-scrolled'].plan.map(step => Object.keys(step)[0]).slice(3, 5), ['action', 'wait']);
    assert.equal(byName['ed3-control'].probe, '\x1b[3J');
    assert.equal(byName['wheel-control'].probe, null);
    assert.deepEqual(osc720Experiments(runId).filter(item => item.role === 'control').map(item => item.name), ['unmarked', 'ed3-control', 'wheel-control']);
    assert.throws(() => osc720Fixture('x'), /Invalid OSC 720 run identity/);
});

test('the scrollback state mapping follows the termless definition: the absolute row where the viewport starts', () => {
    const facts = (offset, scrollback) => ({ viewport: { scrollOffset: offset, maxScrollOffset: scrollback }, scrollbackLines: scrollback });
    assert.deepEqual(osc720ScrollbackState(24, facts(0, 30)), { viewportOffset: 30, totalLines: 54, screenLines: 24 });
    assert.deepEqual(osc720ScrollbackState(24, facts(30, 30)), { viewportOffset: 0, totalLines: 54, screenLines: 24 });
    assert.deepEqual(osc720ScrollbackState(24, facts(0, 0)), { viewportOffset: 0, totalLines: 24, screenLines: 24 });
});

test('a scrolled experiment restores the live view before the next reset', () => {
    const byName = Object.fromEntries(osc720Experiments(runId).map(item => [item.name, item]));
    for (const name of ['count-2-from-scrolled', 'wheel-control']) {
        const tail = byName[name].plan.slice(-3);
        assert.deepEqual(tail.map(step => Object.keys(step)[0]), ['action', 'action', 'wait'], name);
        assert.deepEqual(tail.slice(0, 2).map(step => step.action), ['scroll-down', 'scroll-down'], name);
    }
    for (const name of ['pinned-bare', 'unmarked', 'ed3-control']) assert.equal(byName[name].plan.some(step => step.action !== undefined), false, name);
});
