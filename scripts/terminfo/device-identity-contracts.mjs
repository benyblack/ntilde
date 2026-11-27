import { da, leave, rawText } from './sixel-reports-contracts.mjs';

export const scope = Object.freeze({ id: 'ntilde-owned-device-identity-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'identity-';
export const da1 = '\x1b[?62;4;22c';
export const caseIds = ['device.tertiary-da', 'device.xtversion'];
export const requests = ['\x1b[=c', '\x1b[>0q'];
export const nonce = runId => 'NTILDE_ID_' + runId;
export const cursorStyle = Object.freeze({ block: { style: 0, blink: false, isDefault: false },
    beam: { style: 1, blink: false, isDefault: false } });

// Exercise a mix of set and reset modes, including input modes outside readScreen.
const modes = [[1, 1], [6, 1], [7, 2], [1049, 1], [2004, 1], [1004, 2]];
const modeQuery = modes.map(([mode]) => '\x1b[?' + mode + '$p').join('');
export const modeReply = modes.map(([mode, state]) => '\x1b[?' + mode + ';' + state + '$y').join('');
const baselineModes = '\x1b[?1;6;2004l\x1b[?7h';
const startFixture = '\x1b[?1049h\x1b[2J\x1b[H\x1b[31;4m';
const configure = '\x1b[0m\x1b[?1;2004h\x1b[?7l\x1b[2;6r\x1b[?6h\x1b[2;4H\x1b[2 q';
const endFixture = baselineModes + '\x1b[r\x1b[0 q' + leave;

export function requireIdentity(condition, detail) {
    if (!condition) throw new Error('Device identity: ' + detail);
}

export function planFor(runId) {
    const query = (stage, request, id) => ({ stage, request, id, sample: true, queries: [id ? request + da : request, modeQuery] });
    return [
        { stage: 'start' },
        { stage: 'bind', write: startFixture + nonce(runId) + configure },
        { stage: 'modes', sample: true, queries: [modeQuery] },
        { stage: 'beam', write: '\x1b[6 q' },
        query('primary', da),
        query('secondary', '\x1b[>c'),
        query('da3', requests[0], caseIds[0]),
        query('da3-zero', '\x1b[=0c'),
        query('da3-nonzero', '\x1b[=1c'),
        query('version', requests[1], caseIds[1]),
        query('version-default', '\x1b[>q'),
        query('version-nonzero', '\x1b[>1q'),
        { stage: 'finish', write: endFixture },
        { stage: 'seal' },
    ];
}

export function exactReplies(version) {
    const unit = '\x1bP!|00000000\x1b\\', name = '\x1bP>|Ntilde ' + version + '\x1b\\';
    return { primary: da1, secondary: '\x1b[>1;10;0c', da3: unit, 'da3-zero': unit, 'da3-nonzero': '',
        version: name, 'version-default': name, 'version-nonzero': '' };
}

export function verifySamples(step, samples, used, replies) {
    requireIdentity(samples?.length === step.queries.length, 'incomplete query stage');
    const observed = step.queries.map((request, index) => {
        requireIdentity(samples[index].requestBytes === request, 'query bytes or order changed');
        return rawText(samples[index].sample, used);
    });
    requireIdentity(observed.at(-1) === modeReply, 'reported mode state changed');
    if (!step.request) return null;
    const [raw] = observed;
    if (step.id) requireIdentity(raw.endsWith(da1), 'missing exact pinned DA1 sentinel');
    const reply = step.id ? raw.slice(0, -da1.length) : raw;
    const expected = replies[step.stage];
    return { ...(step.id && { id: step.id }), stage: step.stage, request: step.queries[0],
        raw, reply, expected, outcome: reply === expected ? 'passed' : 'failed' };
}
