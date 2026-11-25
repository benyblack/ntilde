import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';
import { id, replayPaste } from './modern-x11-clipboard-paste.mjs';
import { buildPastePublication, pastePaths, publication, readablePaste, verifyPasteRun } from './modern-x11-clipboard-paste-results.mjs';

function withArchive(check) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-clipboard-paste-test-'));
    try {
        const directory = join(modernRoot, publication);
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        return check(scratch);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

const retained = () => withArchive(scratch => verifyPasteRun(scratch).run);
const hex = text => Buffer.from(text, 'ascii').toString('hex');

test('rederive all 69 Modern rows with only the frozen App paste criterion updated', () => {
    const report = buildPastePublication();
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    const index = report.rows.findIndex(row => row.id === id);
    const { observed, requirement, stockVerdict } = report.rows[index];
    assert.deepEqual(report.rows.with(index, previous.rows[index]), previous.rows);
    assert.equal(requirement, previous.rows[index].requirement);
    assert.equal(stockVerdict, previous.rows[index].stockVerdict);
    assert.equal(observed.shortcut, 'Ctrl+V');
    assert.equal(observed.selection, 'CLIPBOARD');
    assert.equal(observed.keyHex, '61');
    assert.deepEqual(observed.reads.map(step => step.rawHex), [
        hex(observed.nonce), hex('\x1b[200~' + observed.nonce + '\x1b[201~'), hex(observed.nonce),
    ]);
    assert.equal(observed.restoredMode, observed.incomingMode);
    assert.equal(observed.restoredSelection, observed.baseline);
    assert.equal(observed.status, 'supported');
    assert.deepEqual(report.totals, { pass: 67, fail: 0, inconclusive: 2 });
    const paths = pastePaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readablePaste(report));
});

function replacePaste(phase, rawHex) {
    const { native, capture } = phase.action;
    const fenceStart = BigInt(native.fence.started);
    const delimiter = capture.chunks.filter(chunk => BigInt(chunk.at) > fenceStart);
    assert.equal(delimiter.map(chunk => chunk.hex).join(''), hex(native.fence.tag));
    capture.chunks = rawHex ? [{ at: (fenceStart - 1n).toString(), hex: rawHex }, ...delimiter] : delimiter;
}

test('raw mode-on is unsupported only with complete off-phase and restoration controls', () => {
    const run = retained();
    const [trial] = run.cases;
    const rawNonce = hex(trial.nonce);
    replacePaste(trial.phases[1], rawNonce);
    assert.equal(replayPaste(run)[0].status, 'unsupported');
    replacePaste(trial.phases[2], '');
    assert.throws(() => replayPaste(run), /both off phases/);
});

test('missing, partial, duplicate and extraneous mode-on data invalidate collection', () => {
    const run = retained();
    const [trial] = run.cases;
    const [offBefore, on] = trial.phases;
    const wrapped = hex('\x1b[200~' + trial.nonce + '\x1b[201~');
    for (const rawHex of ['', wrapped.slice(0, -2), wrapped + wrapped, wrapped + '61']) {
        replacePaste(on, rawHex);
        assert.throws(() => replayPaste(run), /unexpected bytes/);
    }
    replacePaste(on, wrapped);
    replacePaste(offBefore, wrapped);
    assert.throws(() => replayPaste(run), /both off phases/);
});

test('replay restores an incoming set mode to set', () => {
    const run = retained();
    run.incoming.replyHex = hex('\x1b[?2004;1$y');
    run.restore.modes.replyHex = run.incoming.replyHex;
    run.restore.modes.writeHex = hex('\x1b[?2004h\x1b[?2004$p');
    assert.equal(replayPaste(run)[0].restoredMode, true);
});

test('refuse changed binding, owner, phase, capture fence or restoration', () => {
    const original = retained();
    const changes = [
        [/custom shortcut/, run => { run.settings.Keybindings = { paste: 'Shift+Insert' }; }],
        [/owner changed/, (_run, trial) => { trial.nonceRead.result.text = trial.baseline; }],
        [/owner changed/, (_run, trial) => {
            trial.phases[0].read.before.ownerWindow++;
            trial.phases[0].read.after.ownerWindow++;
        }],
        [/phase order/, (_run, trial) => { trial.phases[0].name = 'on'; }],
        [/mode observations/, (_run, trial) => { trial.phases[1].mode.replyHex = trial.phases[0].mode.replyHex; }],
        [/PTY fence/, (_run, trial) => {
            const { capture } = trial.phases[0].action;
            capture.chunks.push({ hex: '61', at: (BigInt(capture.sealed) - 1n).toString() });
        }],
        [/mode observations/, run => { run.restore.modes = null; }],
        [/owner changed/, (_run, trial) => { trial.restoreRead.result.text = trial.nonce; }],
        [/terminal restoration/, run => { run.terminal.restored[0]++; }],
    ];
    for (const [message, change] of changes) {
        const run = structuredClone(original);
        change(run, run.cases[0]);
        assert.throws(() => replayPaste(run), message);
    }
});

test('archive replay requires independent peer cleanup and matching recorded grade', () => {
    withArchive(scratch => {
        const path = join(scratch, 'observation.json');
        const { run } = verifyPasteRun(scratch);
        const altered = structuredClone(run);
        altered.results[0].conclusion = 'fail';
        writeFileSync(path, JSON.stringify(altered));
        assert.throws(() => verifyPasteRun(scratch), /recorded grading/);
        run.cleanup.peers.pop();
        writeFileSync(path, JSON.stringify(run));
        assert.throws(() => verifyPasteRun(scratch), /GTK peer cleanup/);
    });
});
