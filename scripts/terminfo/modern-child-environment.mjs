export const scopeName = 'modern-child-environment';
export const scope = Object.freeze({ id: 'owned-modern-child-environment-direct-v1', persistence: 'Off', deadlineMs: 30000 });
export const names = Object.freeze(['COLORTERM', 'TERM_PROGRAM', 'NO_COLOR', 'VTE_VERSION']);
export const controls = Object.freeze(['clean', 'inherited']);
export const ids = Object.freeze(['device.env-colorterm', 'device.env-term-program', 'device.env-no-color', 'device.env-vte-version']);
const inherited = Object.freeze({
    COLORTERM: 'ntilde-qa-inherited-colorterm',
    TERM_PROGRAM: 'ntilde-qa-inherited-term-program',
    NO_COLOR: 'ntilde-qa-user-no-color',
});

export function requireEnvironment(condition, reason) {
    if (!condition) throw new Error('Modern child environment: ' + reason);
}

export function snapshotEnvironment(env) {
    return Object.fromEntries(names.map(name => {
        if (env[name] === undefined) return [name, { state: 'absent', value: null }];
        return [name, { state: env[name] === '' ? 'empty' : 'present', value: env[name] }];
    }));
}

export function parentValues(control) {
    requireEnvironment(controls.includes(control), 'select a clean or inherited launch');
    return snapshotEnvironment(control === 'inherited' ? inherited : {});
}

export function parseChildOutput(raw) {
    requireEnvironment(typeof raw === 'string' && raw.length <= 4096, 'invalid raw child output');
    const values = JSON.parse(raw);
    requireEnvironment(values && JSON.stringify(Object.keys(values)) === JSON.stringify(names), 'unexpected environment names');
    for (const { state, value } of Object.values(values)) {
        requireEnvironment((state === 'absent' && value === null) || (state === 'empty' && value === '')
            || (state === 'present' && typeof value === 'string' && value.length > 0 && !value.includes('\0')), 'invalid child value state');
    }
    const projected = snapshotEnvironment(Object.fromEntries(names.map(name => [name, values[name].value ?? undefined])));
    requireEnvironment(JSON.stringify(projected) + '\n' === raw, 'unexpected fields or framing in child output');
    return values;
}

export function gradeEnvironment(clean, inheritedRun) {
    const outcomes = [
        clean.COLORTERM.value === 'truecolor' && inheritedRun.COLORTERM.value === 'truecolor',
        clean.TERM_PROGRAM.value === 'Ntilde' && inheritedRun.TERM_PROGRAM.value === 'Ntilde',
        clean.NO_COLOR.state === 'absent' && inheritedRun.NO_COLOR.value === inherited.NO_COLOR,
    ];
    const vteAbsent = clean.VTE_VERSION.state === 'absent';
    return ids.map((id, index) => {
        const name = names[index];
        if (name === 'VTE_VERSION') return { id, conclusion: 'inconclusive', applicability: 'VTE-based terminals',
            reason: vteAbsent ? 'The Ntilde App uses Avalonia and its own native PTY integration. The clean child had no VTE_VERSION.'
                : 'The clean child unexpectedly had VTE_VERSION. Its presence cannot establish a VTE widget version for Ntilde.',
            clean: clean[name], inherited: inheritedRun[name] };
        return { id, conclusion: outcomes[index] ? 'pass' : 'fail', clean: clean[name], inherited: inheritedRun[name] };
    });
}
