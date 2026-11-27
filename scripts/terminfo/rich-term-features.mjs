import { isDeepStrictEqual as same } from 'node:util';
import { parseDevqFrame } from './devq-report.mjs';
import { environmentProjection } from './modern-child-environment.mjs';

export const scope = Object.freeze({ id: 'owned-rich-term-features-direct-v1', persistence: 'Off', deadlineMs: 30000 });
export const controlVariable = 'NTILDE_TERMINFO_TERM_FEATURES_CONTROL';
export const controls = ['clean', 'inherited'];
export const neutralName = 'NTILDE_QA_ENVIRONMENT_CONTROL';
export const names = ['TERM_FEATURES', 'TERM_PROGRAM', neutralName];
export const request = '\x1b]1337;RequestCapabilities\x07';
export const da = '\x1b[c';

export function requireEvidence(condition, message) {
    if (!condition) throw new Error('TERM_FEATURES assessment: ' + message);
}

const projection = environmentProjection(names, requireEvidence);
export const { snapshotEnvironment } = projection;

export function parentValues(control) {
    requireEvidence(controls.includes(control), 'select clean or inherited');
    return snapshotEnvironment({
        ...(control === 'inherited' && { TERM_FEATURES: 'ntilde-qa-outer-features', TERM_PROGRAM: 'ntilde-qa-outer-program' }),
        [neutralName]: 'ntilde-qa-neutral-' + control,
    });
}

// Retain exactly what the same child sent and received, including the independent DA barrier.
export function parseChildOutput(raw) {
    requireEvidence(typeof raw === 'string' && raw.length <= 4096, 'invalid child observation');
    const { environment, preflight, sent, received } = JSON.parse(raw);
    projection.parseChildOutput(JSON.stringify(environment) + '\n');
    const initial = parseDevqFrame(preflight);
    requireEvidence(sent === request + da && initial?.replies.length === 0 && initial.barrier === preflight, 'invalid request or preflight');
    const frame = parseDevqFrame(received);
    requireEvidence(frame && frame.barrier === preflight, 'incomplete or inconsistent exchange');
    const observation = { environment, preflight, sent, received };
    requireEvidence(JSON.stringify(observation) + '\n' === raw, 'unexpected observation fields');
    return observation;
}

// #1031's verified reply when the direct pane has clipboard writes disabled, in spec table order.
export const codes = ['T3', 'Lr', 'M', 'Sc7', 'U', 'Ts3', 'B', 'F', 'Gs', 'Go', 'Sy', 'H', 'No', 'P'];

export function assessPair(clean, inherited) {
    for (const [index, sample] of [clean, inherited].entries()) {
        const expected = parentValues(controls[index]);
        requireEvidence(sample.environment.TERM_PROGRAM.value === 'Ntilde'
            && same(sample.environment[neutralName], expected[neutralName]), 'identity or neutral inheritance control failed');
        const frame = parseDevqFrame(sample.received);
        requireEvidence(same(frame?.replies, ['\x1b]1337;Capabilities=' + codes.join('') + '\x1b\\']),
            'expected exactly one complete setting-disabled capabilities reply');
    }
    requireEvidence(clean.environment.TERM_FEATURES.state === 'absent'
        && same(inherited.environment.TERM_FEATURES, parentValues('inherited').TERM_FEATURES), 'changed direct-local declaration policy');
    return { id: 'device.term-features', directLocal: 'unprovided when absent; inherited verbatim when seeded', replyCodes: codes };
}
