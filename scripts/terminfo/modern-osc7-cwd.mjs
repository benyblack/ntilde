export const caseId = 'extensions.osc7-cwd';
export const scope = Object.freeze({ id: 'owned-modern-osc7-cwd-direct-v1', persistence: 'Off', deadlineMs: 90000 });
export const surface = 'horizontal tab hover tooltip';
export const prefix = 'mcwd-';
export const da = '\x1b[c';

export function requireCwd(condition, reason) {
    if (!condition) throw new Error('Modern OSC 7: ' + reason);
}

export function cwdPlan(runId) {
    requireCwd(/^[a-f\d]{32}$/.test(runId), 'invalid run id');
    const nonce = [...runId.slice(0, 8)].map(digit => 'abcdefghjkmnpqrs'[Number.parseInt(digit, 16)]).join('');
    const root = '/tmp/cwd-' + nonce;
    const uri = path => 'file://localhost' + path;
    const step = (phase, path, code = 7) => ({
        phase, path, uri: uri(path), bytes: `\x1b]${code};${uri(path)}\x07`,
    });
    return [
        { phase: 'launch', bytes: '' },
        step('fixture', root + '/start'),
        { phase: 'incoming', bytes: '' },
        step('control', root + '/check'),
        step('target-a', root + '/east/repo'),
        step('target-b', root + '/west/repo'),
        step('unhandled', root + '/other', 9999),
        step('restore', root + '/start'),
        { phase: 'restored', bytes: '' },
    ];
}

export function expectedAction(step) {
    return (step.bytes + da);
}

export function checkAction(step, action) {
    requireCwd(action?.request === expectedAction(step)
        && typeof action.barrier === 'string' && action.barrier.length <= 128
        && /^\x1b\[\?[\d;]+c$/.test(action.barrier), 'changed action bytes or missing DA fence at ' + step.phase);
}

// The OCR operates on native tooltip pixels. Restrict the projected reading to a
// single absolute path made of the fixture's plain ASCII characters. A missing,
// ambiguous, partial or basename-only observation leaves the surface unqualified.
export function readPublicPath(lines) {
    requireCwd(Array.isArray(lines) && lines.every(line => typeof line.text === 'string'), 'missing raw tooltip transcription');
    const paths = lines.map(line => line.text).filter(text => /^\/(?:[a-z0-9-]+\/)+[a-z0-9-]+$/.test(text));
    return paths.length === 1 ? paths[0] : null;
}

export function gradeCwd(plan, stages) {
    const { fixture, control, 'target-a': targetA, 'target-b': targetB, unhandled } = Object.fromEntries(plan.map(step => [step.phase, step]));
    const path = phase => stages[phase]?.publicPath ?? null;
    const incomingVerified = stages.launch?.metadataPath === null && path('launch') === null
        && ['fixture', 'incoming'].every(phase => path(phase) === fixture.path && stages[phase].metadataPath === fixture.path);
    const controlVerified = path('control') === control.path;
    const negativeHeld = path('target-b') !== null && path('unhandled') === path('target-b') && unhandled.path !== path('target-b');
    const restored = ['restore', 'restored'].every(phase => path(phase) === fixture.path && stages[phase].metadataPath === fixture.path);
    const result = { id: caseId, surface, stages, incomingUri: fixture.uri, incomingVerified, controlVerified, negativeHeld, restored };
    const invalid = reason => ({ ...result, conclusion: 'inconclusive', verdict: 'probe-invalid', reason });
    if (!incomingVerified) return invalid('The prepared incoming URI and its independent public reading were not verified.');
    if (!restored) return invalid('The incoming URI did not have two verified public restoration readings.');
    if (!controlVerified) return invalid('The distinct OSC 7 control did not read back exactly on the public tooltip.');
    if (path('target-a') === null || path('target-b') === null) {
        return { ...result, conclusion: 'inconclusive', verdict: 'to-test', reason: 'A target lacked qualified full-path readback.' };
    }
    if (!negativeHeld) return invalid('The checked unhandled OSC did not leave the observed public path unchanged.');
    if (path('target-a') !== targetA.path || path('target-b') !== targetB.path) {
        return { ...result, conclusion: 'fail', verdict: 'unsupported', reason: 'The qualified public tooltip missed a requested full path.' };
    }
    return { ...result, conclusion: 'pass', verdict: 'supported', reason: 'Both same-basename targets read back with their distinct parent paths. The unhandled OSC held the path and the incoming URI was restored.' };
}
