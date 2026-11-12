// Experimental contract for OSC 0/1/2. This is not a publication or a measurement.
// The pinned termless feeds are copied from terminfo.dev@4.0.0, independently per case.
export const oscTitleCases = Object.freeze({
    'extensions.osc0-icon-title': Object.freeze({ code: 0, fixture: '\x1b]0;My Title\x07' }),
    'extensions.osc1-icon': Object.freeze({ code: 1, fixture: '\x1b]1;test-icon\x07' }),
    'extensions.osc2-title': Object.freeze({ code: 2, fixture: '\x1b]2;Test Title\x07' }),
});

const osc = (code, value, terminator = '\x07') => `\x1b]${code};${value}${terminator}`;

export const oscTitlePrefix = 'ost';
export const oscTitleSuiteId = 'ntilde-owned-osc-title-graded-v1';
export const oscTitleContractsVersion = 1;
export const oscTitleCaseIds = Object.freeze(Object.keys(oscTitleCases));

/** Reset to a prepared observable baseline; original null metadata is not restorable. */
export function oscTitleFixture(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid OSC title run identity.');
    const title = `OT-base-${runId.slice(0, 8)}`, cwd = '/ntilde-qa/osc-title-base', nonce = `NTILDE_OT_${runId}`;
    const reset = osc(8, ';') + osc(9, '4;0') + osc(133, 'D;0') + osc(133, 'A')
        + '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H\x1b[128M\x1b[2J\x1b[H'
        + '\x1b[23;0t'.repeat(12) + osc(0, title) + osc(7, `file://localhost${cwd}`);
    const stage = reset + '\x1b[10;1H' + nonce;
    return { title, cwd, nonce, reset, stage, bind: stage + osc(4, '7;rgb:12/34/56'), finish: reset + osc(104, '7') };
}

/** Every observation follows a distinct, observed setup. Control payloads are not pinned grades. */
export function oscTitleExperiments(runId) {
    const fixture = oscTitleFixture(runId);
    const controls = [
        ['empty-bel', '', '\x07'], ['blank-st', '   ', '\x1b\\'],
        ['spaces-bel', ' padded ', '\x07'], ['unicode-st', '東京-λ', '\x1b\\'],
        ['semicolon-bel', 'one;two', '\x07'], ['ordinary-bel', 'terminator-control', '\x07'],
        ['ordinary-st', 'terminator-control', '\x1b\\'],
    ];
    return Object.entries(oscTitleCases).flatMap(([owner, spec], caseIndex) => {
        const cases = [['pinned', null, null], ...controls];
        return cases.map(([name, payload, terminator], index) => {
            const marker = `${runId.slice(0, 8)}-${caseIndex}-${index}`;
            const title = `title-${marker}`, icon = `icon-${marker}`;
            const feed = name === 'pinned' ? spec.fixture : osc(spec.code, payload, terminator);
            const value = name === 'pinned' ? ['My Title', 'test-icon', 'Test Title'][caseIndex] : payload;
            return { caseId: `${owner}/${name}`, owner, role: name === 'pinned' ? 'pinned' : 'control',
                baseline: { title, icon }, expected: { title: spec.code === 1 ? title : value, icon: spec.code === 2 ? icon : value },
                plan: [{ feed: fixture.stage }, { feed: osc(2, title) + osc(1, icon) }, { read: 'before', kind: 'setup' },
                    { feed }, { read: 'after', kind: 'effect' }] };
        });
    });
}
