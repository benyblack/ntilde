import { environmentProjection } from './modern-child-environment.mjs';

export const scopeName = 'rich-vendor-environment';
export const scope = Object.freeze({ id: 'owned-rich-vendor-environment-direct-v1', persistence: 'Off', deadlineMs: 30000 });
export const controls = Object.freeze(['clean', 'inherited']);
export const controlVariable = 'NTILDE_TERMINFO_VENDOR_ENVIRONMENT_CONTROL';
export const neutralName = 'NTILDE_QA_ENVIRONMENT_CONTROL';

export const families = Object.freeze([
    { id: 'device.env-lc-terminal', producer: 'iTerm2', names: ['LC_TERMINAL', 'LC_TERMINAL_VERSION'] },
    { id: 'device.env-kitty', producer: 'Kitty', names: ['KITTY_PID', 'KITTY_WINDOW_ID', 'KITTY_INSTALLATION_DIR'] },
    { id: 'device.env-wezterm', producer: 'WezTerm',
        names: ['WEZTERM_PANE', 'WEZTERM_EXECUTABLE', 'WEZTERM_EXECUTABLE_DIR', 'WEZTERM_CONFIG_DIR', 'WEZTERM_UNIX_SOCKET'] },
    { id: 'device.env-wt-session', producer: 'Windows Terminal', names: ['WT_SESSION', 'WT_PROFILE_ID'] },
]);
const vendorNames = families.flatMap(family => family.names);
export const names = Object.freeze([...vendorNames, 'TERM_PROGRAM', neutralName]);
const sentinel = name => 'ntilde-qa-vendor-' + name.toLowerCase().replaceAll('_', '-');

export function requireVendorEnvironment(condition, reason) {
    if (!condition) throw new Error('Rich TUI vendor environment: ' + reason);
}

export const { snapshotEnvironment, parseChildOutput } = environmentProjection(names, requireVendorEnvironment);

export function parentValues(control) {
    requireVendorEnvironment(controls.includes(control), 'select a clean or inherited launch');
    const seeded = control === 'inherited' ? [...vendorNames, 'TERM_PROGRAM'] : [];
    return snapshotEnvironment({
        ...Object.fromEntries(seeded.map(name => [name, sentinel(name)])),
        [neutralName]: 'ntilde-qa-neutral-' + control,
    });
}

function observation(input, child) {
    if (child.state === 'absent') return { input, child, origin: 'absent' };
    if (input.state === 'absent') return { input, child, origin: 'app-provided' };
    return { input, child, origin: input.value === child.value ? 'inherited' : 'changed' };
}

export function assessEnvironment(clean, inherited) {
    const inputs = controls.map(parentValues);
    const samples = Object.fromEntries(names.map(name => [name, {
        clean: observation(inputs[0][name], clean[name]),
        inherited: observation(inputs[1][name], inherited[name]),
    }]));
    const identity = samples.TERM_PROGRAM;
    const neutral = samples[neutralName];
    requireVendorEnvironment(identity.clean.child.value === 'Ntilde' && identity.inherited.child.value === 'Ntilde'
        && neutral.clean.origin === 'inherited' && neutral.inherited.origin === 'inherited',
    'Ntilde identity or neutral inheritance control failed');
    identity.inherited.origin = 'app-provided';
    return {
        controls: { TERM_PROGRAM: identity, [neutralName]: neutral },
        rows: families.map(({ id, producer, names }) => {
            const variables = Object.fromEntries(names.map(name => [name, samples[name]]));
            const expected = Object.values(variables).every(sample => sample.clean.origin === 'absent' && sample.inherited.origin === 'inherited');
            return { id, producer, applicability: expected ? 'not-applicable' : 'requires-investigation', variables };
        }),
    };
}
