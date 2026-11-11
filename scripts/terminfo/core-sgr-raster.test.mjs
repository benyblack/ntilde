import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { sgrRasterExperiments, sgrRasterSteps, sgrRasterReadCount, sgrRasterFrameCount, experimentSteps, subsetsOf, gradeExperiment, fallbackDecision,
    parseDecrqssSgr, decrqssRequest, specimen, controlText, sgrRasterIds, sgrRasterBind, sgrRasterFinish, collectSgrRaster } from './core-sgr-raster.mjs';

const rows = 40, cols = 140;

/** A deliberately separate terminal model: a grid of cells carrying attribute sets, one pen, one DECSC slot and only the controls the plan uses.
 * `behavior` injects the defects the grading must classify; the model never consults the production code. */
function run(experiment, behavior = {}) {
    const grid = Array.from({ length: rows }, () => Array.from({ length: cols }, () => ({ ch: ' ', attrs: new Set() })));
    let row = 0, col = 0, pen = new Set(), saved = { row: 0, col: 0, pen: new Set() };
    const out = { crops: {}, control: {}, coherentStage: {}, stored: {}, queries: {} };
    const sgr = params => {
        for (const code of params.length ? params : [0]) {
            if (code === 0) pen = new Set();
            else if (code === 1) pen.add('bold');
            else if (code === 2) pen.add('faint');
            else if (code === 3) pen.add('italic');
            else if (code === 7) pen.add('inverse');
            else if (code === 22) {
                const cleared = behavior.sgr22 ?? ['bold', 'faint'];
                if (cleared === 'noop') continue;
                for (const name of cleared) pen.delete(name);
            } else if (code === 99 && behavior.sgr99) pen.add(behavior.sgr99);
        }
    };
    const apply = feed => {
        for (let i = 0; i < feed.length; i++) {
            const ch = feed[i];
            if (ch !== '\x1b') { grid[row][col] = { ch, attrs: new Set(pen) }; col++; continue; }
            const next = feed[++i];
            if (next === '7') saved = { row, col, pen: new Set(pen) };
            else if (next === '8') { row = saved.row; col = saved.col; if (!behavior.decrcKeepsPen) pen = new Set(saved.pen); }
            else if (next === '[') {
                let params = '';
                while (!/[@-~]/.test(feed[i + 1])) params += feed[++i];
                const final = feed[++i], numbers = params.replace(/[?!]/g, '').split(';').map(x => (x === '' ? 0 : Number(x)));
                if (final === 'H') { row = (numbers[0] || 1) - 1; col = (numbers[1] || 1) - 1; }
                else if (final === 'm') sgr(params === '' ? [] : numbers);
                else if (final === 'J') grid.forEach(line => line.forEach(cell => { cell.ch = ' '; cell.attrs = new Set(); }));
                else if (final === 'p') { pen = new Set(); saved = { row: 0, col: 0, pen: new Set() }; }
            }
        }
    };
    const renderKey = attrs => [...attrs].filter(name => !(behavior.invisible ?? []).includes(name)).sort().join('+');
    const bytes = (key, size) => Buffer.concat(Array.from({ length: Math.ceil(size / 32) }, (_, n) => createHash('sha256').update(key + n).digest())).subarray(0, size);
    for (const step of experimentSteps(experiment)) {
        apply(step.feed);
        const cells = [grid[specimen.row][specimen.col], grid[specimen.row][specimen.col + 1]];
        const attrs = cells[0].attrs;
        out.crops[step.role] = bytes('crop:' + renderKey(attrs) + (behavior.sgr99Render && step.role.endsWith('/sgr99') ? '+99' : ''), 3 * 4 * 9 * 19 * 4);
        out.control[step.role] = bytes('control', 4 * 9 * 19 * 4);
        const lines = grid.map(line => line.map(cell => cell.ch).join('').trimEnd());
        const stored = ['bold', 'faint', 'italic', 'inverse'].filter(name => attrs.has(name));
        out.stored[step.role] = stored;
        out.coherentStage[step.role] = lines[specimen.row].endsWith('YY') && lines[controlText.row].trim() === controlText.text && row === step.cursor[0] && col === step.cursor[1];
        if (step.query) {
            const mode = behavior.decrqss ?? 'normal', codes = { bold: 1, faint: 2, italic: 3, inverse: 7 };
            const list = [...pen].map(name => codes[name]).sort((a, b) => a - b);
            const reported = behavior.decrqssLies ? [] : list;
            const replies = { normal: { raw: `\x1bP1$r${['0', ...reported].join(';')}m\x1b\\` }, silent: null, invalid: { raw: '\x1bP0$r\x1b\\' },
                malformed: { raw: '\x1bPgarbage\x1b\\' }, wrong: { raw: '\x1bP1$r1;24r\x1b\\' } };
            out.queries[step.role] = parseDecrqssSgr(step.role.endsWith('/final') || mode === 'normal' ? replies[mode] : replies.normal);
        }
    }
    return out;
}
const grade = (index, behavior) => gradeExperiment(sgrRasterExperiments[index], run(sgrRasterExperiments[index], behavior));

test('the suite names the three rows and a plan with 54 reads and 57 frames', () => {
    assert.deepEqual(sgrRasterIds, ['sgr.selective-reset.bold', 'sgr.reset', 'reset.sgr']);
    assert.equal(sgrRasterReadCount, 54);
    assert.equal(sgrRasterFrameCount, 57);
    for (const experiment of sgrRasterExperiments) assert.equal(experimentSteps(experiment).length, 18);
});

test('every experiment draws a neutral specimen, saves it with DECSC and restores with DECRC for all seven nonempty subsets', () => {
    for (const experiment of sgrRasterExperiments) {
        const subsets = subsetsOf(experiment);
        assert.equal(subsets.length, 7);
        assert.equal(new Set(subsets.map(s => s.join('+'))).size, 7);
        const steps = experimentSteps(experiment), feeds = steps.map(step => step.feed);
        assert.ok(feeds[0].includes('YY\x1b[5;13H\x1b7') && feeds[0].includes('CONTROL'), 'neutral specimen is drawn before any attribute and saved');
        assert.equal(feeds.filter(feed => feed.startsWith('\x1b8YY')).length, 7);
        assert.ok(feeds.every(feed => !feed.includes('\x1b[0m') || feed === experiment.reset + '\x1b[5;13HYY' || feed.startsWith('\x1b[!p')), 'no SGR 0 stands in for DECRC');
        assert.ok(feeds.at(-3).includes('\x1b[99m') || feeds.at(-2).includes('\x1b[99m'));
        assert.equal(steps.at(-1).feed, experiment.reset + '\x1b[5;13HYY');
    }
    assert.equal(sgrRasterExperiments[0].reset, '\x1b[22m');
    assert.deepEqual(sgrRasterExperiments[0].attributes, ['bold', 'faint', 'italic']);
    assert.deepEqual(sgrRasterExperiments[1].attributes, ['bold', 'italic', 'inverse']);
    assert.equal(sgrRasterExperiments[1].attributes.join(), sgrRasterExperiments[2].attributes.join());
});

test('the plan equals an independent byte-level terminal model on every stage', () => {
    for (const experiment of sgrRasterExperiments) {
        const modeled = run(experiment);
        for (const step of experimentSteps(experiment)) assert.equal(modeled.coherentStage[step.role], true, step.role);
    }
});

test('a correct terminal is supported by the raster route for all three rows', () => {
    for (const index of [0, 1, 2]) {
        const effect = grade(index);
        assert.equal(effect.outcome, 'supported', sgrRasterExperiments[index].id);
        assert.equal(effect.route, 'raster');
        assert.equal(effect.decidedBy, 'raster');
        assert.equal(effect.raster.matched, sgrRasterExperiments[index].expected);
        assert.ok(Object.values(effect.rasterControls).every(c => c.passed));
        assert.equal(effect.supporting.storedFinalMatchesExpectedReset, true);
    }
});

test('SGR 22 that clears italic too equals neutral and is unsupported under qualified controls', () => {
    const effect = grade(0, { sgr22: ['bold', 'faint', 'italic'] });
    assert.deepEqual([effect.outcome, effect.route, effect.raster.matched], ['unsupported', 'raster', 'neutral']);
});

test('SGR 22 that leaves bold equals another identified subset crop and is unsupported', () => {
    const effect = grade(0, { sgr22: ['faint'] });
    assert.deepEqual([effect.outcome, effect.raster.matched], ['unsupported', 'subset:bold+italic']);
    assert.equal(grade(0, { sgr22: 'noop' }).raster.matched, 'subset:bold+faint+italic');
});

test('SGR 0 that leaves italic equals that subset and is unsupported; a terminal that ignores SGR 0 matches the all-set subset', () => {
    assert.equal(grade(1, {}).outcome, 'supported');
    const experiment = sgrRasterExperiments[2], bad = run(experiment);
    bad.crops['reset.sgr/final'] = bad.crops['reset.sgr/subset/italic'];
    const effect = gradeExperiment(experiment, bad);
    assert.deepEqual([effect.outcome, effect.raster.matched], ['unsupported', 'subset:italic']);
});

test('a final crop matching no admitted crop is invalid, never a verdict', () => {
    const experiment = sgrRasterExperiments[1], data = run(experiment);
    data.crops['sgr.reset/final'] = Buffer.alloc(data.crops['sgr.reset/final'].length, 7);
    const effect = gradeExperiment(experiment, data);
    assert.deepEqual([effect.outcome, effect.reason, effect.route], ['inconclusive', 'probe-invalid-final-crop-matches-no-admitted-crop', 'raster']);
});

test('SGR 99 that changes the crop is invalid under qualified controls and does not fall back to DECRQSS', () => {
    const effect = grade(1, { sgr99: 'bold' });
    // The all-set pen already includes bold in this experiment, so a made-up SGR that sets faint is the observable change.
    assert.equal(effect.outcome, 'supported', 'an SGR 99 that adds nothing visible leaves the crop unchanged');
    const faint = grade(0, { sgr99Render: true });
    assert.deepEqual([faint.outcome, faint.reason, faint.route, faint.decidedBy], ['inconclusive', 'probe-invalid-sgr99-changed-crop', 'raster', 'raster']);
    assert.equal(faint.rasterControls['sgr99-leaves-the-crop-unchanged'].passed, false);
});

test('a failed DECRC restoration (the pen survives) fails the restoration control and DECRQSS decides', () => {
    const effect = grade(1, { decrcKeepsPen: true });
    assert.equal(effect.rasterControls['every-DECRC-restore-reproduces-the-neutral-crop'].passed, false);
    assert.equal(effect.route, 'decrqss-fallback');
    assert.equal(effect.decidedBy, 'query');
    assert.match(effect.claim, /query-based conclusion/);
    assert.match(effect.fallbackReason, /every-DECRC-restore/);
});

test('an invisible attribute (the historical bold-appearance failure shape) fails the subset control and falls back', () => {
    const effect = grade(0, { invisible: ['bold'] });
    assert.equal(effect.rasterControls['every-subset-changes-the-neutral-crop'].passed, false);
    assert.equal(effect.route, 'decrqss-fallback');
    assert.equal(effect.outcome, 'supported');
    assert.equal(effect.fallback.outcome, 'supported');
});

test('italic that renders like faint fails the italic-only separation and falls back without claiming the raster result', () => {
    const experiment = sgrRasterExperiments[0], data = run(experiment);
    data.crops['sgr.selective-reset.bold/subset/italic'] = data.crops['sgr.selective-reset.bold/subset/faint'];
    const effect = gradeExperiment(experiment, data);
    assert.equal(effect.rasterControls['italic-only-crop-differs-from-neutral-and-every-other-subset'].passed, false);
    assert.equal(effect.route, 'decrqss-fallback');
    assert.notEqual(effect.raster.matched, 'italic-only');
});

test('fallback outcomes: a valid failing report is unsupported; silence, refusal, malformed and wrong-setting replies stay inconclusive and distinct', () => {
    const base = { decrcKeepsPen: true };
    assert.equal(grade(0, { ...base, sgr22: ['bold', 'faint', 'italic'] }).outcome, 'unsupported');
    assert.equal(grade(0, { ...base, sgr22: ['bold', 'faint', 'italic'] }).fallback.reportedCodes.length, 0);
    for (const [mode, reason] of [['silent', 'decrqss-silent'], ['invalid', 'decrqss-refused'], ['malformed', 'decrqss-malformed-reply'], ['wrong', 'decrqss-wrong-setting-reply']]) {
        const effect = grade(1, { ...base, decrqss: mode });
        assert.deepEqual([effect.outcome, effect.reason, effect.route], ['inconclusive', reason, 'decrqss-fallback'], mode);
    }
});

test('a fallback whose control queries do not qualify is inconclusive even when the final reply looks right', () => {
    const data = run(sgrRasterExperiments[1], { decrcKeepsPen: true, decrqssLies: true });
    const effect = gradeExperiment(sgrRasterExperiments[1], data);
    assert.deepEqual([effect.outcome, effect.reason], ['inconclusive', 'decrqss-controls-failed']);
    assert.equal(fallbackDecision(sgrRasterExperiments[1], {}).reason, 'decrqss-missing');
});

test('a qualified negative raster result is never overridden by a positive DECRQSS report', () => {
    const experiment = sgrRasterExperiments[0], data = run(experiment, { sgr22: ['bold', 'faint', 'italic'] });
    data.queries['sgr.selective-reset.bold/final'] = parseDecrqssSgr({ raw: '\x1bP1$r0;3m\x1b\\' });
    const effect = gradeExperiment(experiment, data);
    assert.deepEqual([effect.outcome, effect.route], ['unsupported', 'raster']);
    assert.equal(effect.supporting.decrqssFinal.codes.join(), '3');
});

test('structural failures stay inconclusive without any fallback: control drift, blank neutral, incoherent stored state, unrepeatable all-set crop', () => {
    for (const mutate of [
        d => { d.control['sgr.reset/subset/bold'] = Buffer.alloc(d.control['sgr.reset/neutral'].length, 9); },
        d => { d.crops['sgr.reset/neutral'] = Buffer.alloc(d.crops['sgr.reset/neutral'].length, 0); },
        d => { d.coherentStage['sgr.reset/restore/italic'] = false; },
        d => { d.crops['sgr.reset/all'] = Buffer.alloc(d.crops['sgr.reset/all'].length, 3); },
    ]) {
        const data = run(sgrRasterExperiments[1]);
        mutate(data);
        const effect = gradeExperiment(sgrRasterExperiments[1], data);
        assert.deepEqual([effect.outcome, effect.route, effect.reason], ['inconclusive', 'none', 'observation-controls-invalid']);
    }
});

test('stored flags can never replace pixels: a terminal whose stored flags look right but whose crops never change is not supported by the raster route', () => {
    const experiment = sgrRasterExperiments[1], data = run(experiment, { invisible: ['bold', 'italic', 'inverse'] });
    const effect = gradeExperiment(experiment, data);
    assert.equal(effect.route, 'decrqss-fallback');
    assert.equal(effect.decidedBy, 'query');
    assert.equal(effect.rasterControls['every-subset-changes-the-neutral-crop'].passed, false);
});

test('DECRQSS replies are classified distinctly and decoded against specified attribute codes only', () => {
    assert.deepEqual(parseDecrqssSgr(null), { kind: 'absent' });
    assert.equal(parseDecrqssSgr({ raw: '\x1bP0$r\x1b\\' }).kind, 'invalid-request');
    assert.equal(parseDecrqssSgr({ raw: '\x1bP1$r1;24r\x1b\\' }).kind, 'wrong-setting');
    assert.equal(parseDecrqssSgr({ raw: 'nonsense' }).kind, 'malformed');
    assert.equal(parseDecrqssSgr({ raw: '\x1bP1$r0;1;38;5;9m\x1b\\' }).kind, 'unrecognized-parameter');
    assert.equal(parseDecrqssSgr({ raw: '\x1bP1$r0;1;1m\x1b\\' }).kind, 'unrecognized-parameter');
    assert.deepEqual(parseDecrqssSgr({ raw: '\x1bP1$r0;3;1m\x1b\\' }).codes, [1, 3]);
    assert.deepEqual(parseDecrqssSgr({ raw: '\x1bP1$r0m\x1b\\' }).codes, []);
    assert.equal(decrqssRequest, '\x1bP$qm\x1b\\');
});

test('the worker binds, drives every step serially with one DECRQSS exchange where planned, and restores on failure', async () => {
    const calls = [], fence = {
        write: async text => { calls.push(['write', text]); return '\x1b[?1;2c'; },
        dcsReport: async text => { calls.push(['dcs', text]); return { barrier: '\x1b[?1;2c', reply: { raw: '\x1bP1$r0m\x1b\\' } }; },
    };
    const owner = async (kind, barrier, extra) => {
        calls.push([kind, extra?.role, extra?.queries?.length]);
        return { screen: { rows, cols, cursorRow: 0, cursorCol: 0, cursorVisible: true, lines: new Array(rows).fill(''), cursorMode: { style: 2, blink: true, isDefault: true },
            cellDetails: [{ overline: false, count: rows * cols, flags: 768, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null }] } };
    };
    const run = await collectSgrRaster({ config: { runId: 'a'.repeat(32) }, fence, owner });
    assert.equal(run.steps.filter(step => step.kind === 'read').length, sgrRasterReadCount);
    assert.equal(run.steps.filter(step => step.kind === 'query').length, 12);
    assert.equal(calls.filter(call => call[0] === 'dcs').length, 12);
    assert.equal(calls[0][0], 'sr-start');
    assert.deepEqual(calls[1], ['write', sgrRasterBind + 'a'.repeat(32)]);
    assert.equal(calls.at(-2)[1], sgrRasterFinish);
    // The first failing step restores, and the failure is reported with the restoration still attempted.
    const failing = [];
    await assert.rejects(collectSgrRaster({ config: { runId: 'b'.repeat(32) }, fence: { ...fence, write: async text => { failing.push(text); if (failing.length === 4) throw new Error('boom'); return '\x1b[?1;2c'; } }, owner }), /boom/);
    assert.equal(failing.at(-1), sgrRasterFinish);
});
