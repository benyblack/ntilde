#!/usr/bin/env node
// Offline, criterion-matched Core results. Archives are data: never import their code.
import { readFileSync, writeFileSync, mkdtempSync, rmSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './terminfo/receipt.mjs';

export const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const publication = 'docs/qa/terminfo/2026-11-11-core-results';
export const mappingSha256 = 'a0c95ead8a653c1555379f89d3ae420172926966ebfd86eb6e92de77d683479c';
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const check = (condition, reason) => { if (!condition) throw new Error('Core results: ' + reason); };
const read = path => JSON.parse(readFileSync(path));
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const verdicts = ['pass', 'fail', 'inconclusive'];
const byText = (a, b) => a.localeCompare(b, 'en');
const outcome = value => ({ supported: 'pass', unsupported: 'fail', inconclusive: 'inconclusive', passed: 'pass', failed: 'fail' })[value];

export function assertRequirementRows(rows, expected) {
    check(rows.length === 80 && new Set(rows.map(row => row.id)).size === 80
        && same(rows.map(row => row.id).sort(byText), [...expected].sort(byText)), 'expected 80 exact unique Core IDs');
}

/** The immutable mapping binds IDs, criteria, evidence identities and every archive. */
export function loadMapping(base = root) {
    const bytes = readFileSync(join(base, publication, 'requirements-v1.json'));
    check(digest(bytes) === mappingSha256, 'mapping bytes changed');
    const mapping = JSON.parse(bytes);
    const frozen = read(join(base, 'docs/qa/terminfo/core-baseline-inputs.json'));
    const expected = frozen.rows.map(row => row.id).sort(byText);
    check(mapping.schemaVersion === 1, 'mapping schema differs'); assertRequirementRows(mapping.rows, expected);
    for (const row of mapping.rows) check(['matched', 'partial'].includes(row.criterionMatch)
        && row.rationale && row.siteRequirement && row.catalogueRequirement && row.evidence.length > 0, 'missing criterion match');
    for (const [file, sha] of Object.entries(mapping.archives)) check(digest(readFileSync(join(base, publication, file))) === sha, 'changed archive: ' + file);
    check(digest(readFileSync(join(base, publication, 'app-source-manifest.json'))) === mapping.appSourceManifestSha256, 'source manifest changed');
    return mapping;
}

/** Extract only regular bounded data entries under the fresh directory. */
export function extractOwnedArchive(base, mapping, directory) {
    const archive = join(base, publication, 'owned-run.zip');
    check(digest(readFileSync(archive)) === mapping.archives['owned-run.zip'], 'owned archive digest differs');
    const python = process.env.PYTHON ?? (process.platform === 'win32' ? 'python' : 'python3');
    const script = String.raw`import zipfile,pathlib,sys,stat
with zipfile.ZipFile(sys.argv[1]) as z:
 names=z.namelist()
 assert len(names)==len(set(names)) and len(names)<2000
 assert sum(i.file_size for i in z.infolist())<150*1024*1024
 for i in z.infolist():
  p=pathlib.PurePosixPath(i.filename)
  assert not p.is_absolute() and '..' not in p.parts and '\\' not in i.filename and not stat.S_ISLNK(i.external_attr>>16)
  dest=pathlib.Path(sys.argv[2]).joinpath(*p.parts)
  dest.parent.mkdir(parents=True,exist_ok=True)
  dest.write_bytes(z.read(i))
`;
    const result = spawnSync(python, ['-c', script, archive, directory], { encoding: 'utf8', windowsHide: true });
    check(result.status === 0, 'archive extraction failed: ' + result.stderr);
}

/** Reissue the complete receipt with trusted current independent graders. Missing,
 * stripped, stale and coherently rewritten report/journal bytes fail their bindings. */
export function verifyOwnedRun(directory, mapping) {
    const receipt = read(join(directory, 'cell-receipt.json'));
    const context = read(join(directory, 'context.json'));
    const proof = read(join(directory, 'ownership-proof.json'));
    const m = mapping.measurement;
    check(receipt.schemaVersion === m.receiptSchema && context.runId === m.runId
        && context.sourceSnapshotPolicy === m.sourcePolicy && context.coreGapsSuiteId === 'ntilde-owned-core-gaps-v1'
        && proof.completed && receipt.fixtureRemoved, 'stale/incomplete run identity or schema');
    const evidence = { ...receipt };
    for (const field of ['schemaVersion', 'ownershipProofSha256', 'contextSha256', 'rawSha256', 'fixtureRemoved', 'completedAt']) delete evidence[field];
    const receiptPath = join(directory, 'cell-receipt.json'); rmSync(receiptPath);
    completeOwnedReceipt(directory, join(directory, 'absent-original-fixture'), evidence);
    const rederived = read(receiptPath);
    check(Number.isFinite(Date.parse(receipt.completedAt)) && Date.parse(receipt.completedAt) >= Date.parse(proof.cleanedUpAt), 'receipt timestamp precedes cleanup');
    check(same({ ...rederived, completedAt: receipt.completedAt }, receipt), 'receipt rederivation differs');
    return { context, receipt, proof };
}

/** Controls for stock-supported Core query traces. Positive grades require actual
 * reply sequences, original write traces and criterion-matched controls. */
export function verifyStockTrace(id, raw, geometry) {
    const item = raw.observations.find(row => row.featureId === id);
    check(item, 'missing stock observation ' + id);
    const trace = JSON.parse(raw.rawReplies[id]);
    check(Array.isArray(trace.writes) && Array.isArray(trace.queries) && Array.isArray(trace.events), 'missing stock trace ' + id);
    for (const query of trace.queries) check(query.reason === 'reply' && Buffer.from(query.rawBase64, 'base64').toString() === query.raw
        && typeof query.match?.[0] === 'string' && query.raw.includes(query.match[0]), 'unbound query reply ' + id);
    const events = trace.events.filter(event => event.kind === 'query').map(({ kind, ...q }) => q);
    check(same(events, trace.queries), 'stock query/event order differs ' + id);
    check(same(trace.events.filter(event => event.kind === 'write').map(event => event.sequence), trace.writes), 'stock write/event order differs ' + id);
    const cpr = trace.queries.filter(q => q.sequence === '\x1b[6n').map(q => {
        const match = /^\x1b\[(\d+);(\d+)R$/.exec(q.match[0]);
        check(match, 'invalid CPR ' + id); return [Number(match[1]), Number(match[2])];
    });
    const positions = {
        'cursor.move.absolute': [[1, 1], [5, 10]], 'cursor.move.home': [[1, 1], [1, 4], [1, 1]],
        'cursor.move.forward': [[1, 1], [1, 6]], 'cursor.move.back': [[1, 1], [1, 4], [1, 2]],
        'cursor.move.down': [[1, 1], [4, 1]], 'cursor.move.up': [[1, 1], [6, 1], [4, 1]],
        'cursor.save-restore': [[3, 5], [10, 10], [3, 5]], 'cursor.cup-boundaries': [[1, 1], [geometry.rows, geometry.cols], [1, 1], [geometry.rows, geometry.cols]],
        'cursor.cuu-past-top': [[1, 1], [4, 1], [1, 1]], 'cursor.cud-past-bottom': [[1, 1], [geometry.rows, 1], [geometry.rows, 1]],
        'cursor.vpa': [[1, 1], [3, 5], [10, 5]], 'cursor.cpl': [[1, 1], [6, 10], [4, 1]], 'cursor.hpa': [[1, 1], [3, 1], [3, 15]],
        'text.cr': [[1, 3], [1, 1]], 'text.backspace': [[1, 5], [1, 4]], 'text.newline': [[3, 5], [4, 5]],
        'text.index': [[3, 5], [4, 5]], 'text.next-line': [[3, 5], [4, 1]],
        'text.wrap': [[1, 1], [1, geometry.cols], [2, 2]], 'text.tab': [[1, 1], [1, 9], [1, 6]],
        'text.hts': [[1, 1], [1, 9], [1, 6]], 'text.tbc': [[1, 9], [1, 17], [1, 25], [1, geometry.cols]],
        'text.cht': [[1, 17]], 'text.cbt': [[1, 17]], 'reset.ris': [[5, 5], [1, 1]],
    };
    if (positions[id]) check(same(cpr, positions[id]), 'stock control/target CPR differs ' + id);
    const modeIds = { 'modes.alt-screen.enter': 1049, 'modes.auto-wrap': 7, 'modes.reverse-video': 5, 'reset.soft': 1 };
    if (modeIds[id]) {
        const mode = modeIds[id], replies = trace.queries.map(q => q.match[0]);
        const statuses = replies.map(reply => new RegExp(String.raw`^\x1b\[\?${mode};([1-4])\$y$`).exec(reply)?.[1]);
        check(statuses.every(Boolean), 'mode reply malformed ' + id);
        if (id === 'reset.soft') check(same(statuses, ['2', '1', '2']) && trace.writes.includes('\x1b[!p'), 'DECSTR control differs');
    }
    // These two traces are retained diagnostics, not positive current-Core evidence.
    check(positions[id] || modeIds[id] || ['text.wide.cjk', 'unicode.tab-stops'].includes(id), 'no stock criterion contract ' + id);
    check(item.outcome === 'supported', 'stock-supported evidence no longer supported ' + id);
    return { id, verdict: 'supported', layer: item.evidence, assertions: raw.assertions.filter(a => a.featureId === id), trace };
}

function qualifiedSource(directory, reference, raw, geometry) {
    const data = read(join(directory, reference.file));
    if (reference.kind === 'stock') return { file: reference.file, caseId: reference.caseId, kind: reference.kind,
        result: verifyStockTrace(reference.caseId, raw, geometry) };
    const item = reference.kind === 'case' ? data.cases.find(c => c.caseId === reference.caseId)
        : data.observations.find(c => c.featureId === reference.caseId);
    check(item, 'missing evidence case ' + reference.caseId);
    return { file: reference.file, caseId: reference.caseId, kind: reference.kind,
        suiteId: data.suiteId, runId: data.runId, result: item };
}

function coreConclusion(match, grades) {
    if (match === 'partial' || grades.includes('inconclusive')) return 'inconclusive';
    if (grades.includes('fail')) return 'fail';
    return 'pass';
}
function qualifiedPinnedVerdict(source) {
    const pinned = source.result.pinned;
    if (pinned?.observation?.outcome) return pinned.observation.outcome;
    if (pinned?.pass === true) return 'supported';
    if (pinned?.notTested) return 'inconclusive';
    return 'unavailable';
}

/** Supplemental exact specimens may be reused across IDs only with an explicit
 * source mapping and the actual current requirement checked here. */
export function verifyGreenBackgroundCriterion(sources) {
    const source = sources.find(s => s.file === 'erar-observations.json' && s.caseId === 'erase.el-with-attrs');
    const setup = source?.result.observed?.background?.before;
    check(setup?.bgIndex === 2 && Number.isFinite(setup.resolved?.g) && setup.resolved.g > 100,
        'SGR 42 green-background specimen is missing or does not meet the current threshold');
}

export function verifyMatchedCriterion(entry, sources) {
    if (entry.criterionMatch !== 'matched') return;
    if (entry.id === 'sgr.bg.standard') verifyGreenBackgroundCriterion(sources);
    if (entry.id === 'sgr.fg.standard') {
        const red = sources.find(s => s.file === 'sgrgb-observations.json')?.result.observed?.first;
        check(red?.char === 'X' && red.fgIndex === 1 && red.fg?.r > 100, 'SGR 31 red-channel criterion is not observed');
    }
    if (['text.cr', 'text.backspace'].includes(entry.id))
        check(sources.some(s => s.kind !== 'stock'), 'joined overwrite cells are required; CPR alone cannot match ' + entry.id);
    if (entry.id === 'erase.line.right')
        check(sources.some(s => s.result.feeds?.some(feed => feed.includes('\x1b[1G'))), 'EL 0 home-boundary CSI 1G specimen is required');
}

export function deriveReport(directory, mapping, identity) {
    const raw = read(join(directory, 'raw.json'));
    const core = read(join(directory, 'cg-observations.json'));
    const launchProfile = read(join(directory, 'initial-settings.json')).Profiles[0];
    const geometry = { rows: core.snapshots[0].screen.rows, cols: core.snapshots[0].screen.cols };
    const rows = mapping.rows.map(entry => {
        const sources = entry.evidence.map(ref => qualifiedSource(directory, ref, raw, geometry));
        verifyMatchedCriterion(entry, sources);
        const grades = sources.map(s => s.kind === 'stock' ? outcome(s.result.verdict)
            : outcome(s.result.effectContract?.outcome ?? s.result.contractResult?.outcome ?? s.result.conclusion?.outcome ?? s.result.result));
        check(grades.every(g => verdicts.includes(g)), 'unknown evidence grade ' + entry.id);
        const grade = coreConclusion(entry.criterionMatch, grades);
        const stock = raw.observations.find(row => row.featureId === entry.id);
        return { id: entry.id, requirement: entry.siteRequirement, catalogueRequirement: entry.catalogueRequirement,
            conclusion: grade, criterionMatch: entry.criterionMatch, explanation: entry.rationale,
            observationLayers: sources.map(s => s.kind === 'stock' ? s.result.layer
                : s.result.effectContract?.layer ?? s.result.conclusion?.layers ?? s.result.conclusion?.evidence ?? 'parent-journaled-state'),
            evidence: entry.evidence, measuredRunId: mapping.measurement.runId,
            appRevision: mapping.measurement.appRevision, scriptsRevision: mapping.measurement.scriptsRevision,
            pinnedProbe: { stockRunId: raw.runId, stockVerdict: stock?.outcome ?? 'manual-ungraded',
                qualified: sources.filter(s => s.kind === 'case').map(s => ({ caseId: s.caseId, verdict: qualifiedPinnedVerdict(s),
                    ...(s.result.pinned?.notTested ? { diagnostic: s.result.pinned.notTested } : {}) })) },
            effectVerdicts: sources.filter(s => s.kind !== 'stock').map(s => ({ file: s.file, caseId: s.caseId,
                verdict: s.result.effectContract?.outcome ?? s.result.contractResult?.outcome ?? s.result.conclusion?.outcome ?? s.result.result,
                contract: s.result.effectContract?.id ?? s.suiteId })),
            materialCatalogueChange: entry.catalogueChanged };
    });
    check(rows.length === 80 && new Set(rows.map(row => row.id)).size === 80, 'duplicate/lost rows');
    const totals = Object.fromEntries(verdicts.map(v => [v, rows.filter(row => row.conclusion === v).length]));
    check(Object.values(totals).reduce((a, b) => a + b, 0) === 80, 'incorrect total');
    return { schemaVersion: 1, asOf: mapping.asOf, policy: mapping.policy, totals,
        corePassPercentage: totals.pass / 80 * 100,
        measurement: { ...mapping.measurement, stockRunId: raw.runId, startedAt: identity.context.startedAt,
            executableSha256: identity.context.executableSha256, assemblySha256: identity.context.assemblySha256,
            sourceSnapshotManifestSha256: identity.context.sourceSnapshotManifestSha256, geometry,
            initialSettingsSha256: identity.context.initialSettingsSha256,
            launchProfile: { id: launchProfile.Id, name: launchProfile.Name, type: launchProfile.Type }, child: core.child,
            appVersion: identity.context.appVersion, appVersionSource: identity.context.appVersionSource,
            rawIdentity: raw.identity, suiteComplete: raw.suiteComplete },
        mappingSha256, archives: mapping.archives, attempts: mapping.attempts,
        scope: 'Private macOS direct-local Release sidecar, measured settings and 40x140 geometry. Criterion-matched stored state, raw queries, actual child output and named controlled pixels only. No official terminfo.dev listing/ranking; no other OS, profile, font completeness or general host-icon presentation claim.',
        rows };
}

const cell = value => String(value).replaceAll('|', String.raw`\|`).replaceAll('\n', ' ').replaceAll('\r', '');
export function readable(report, mapping) {
    const { measurement: m, totals: t } = report;
    const lines = ['# Ntilde Core results — 2026-11-11', '',
        `**${t.pass} pass / ${t.fail} fail / ${t.inconclusive} inconclusive — ${report.corePassPercentage}% of the 80 exact Core requirements.**`, '',
        'These are current criterion-matched conclusions, not qualification-inventory coverage. The live site and the current upstream catalogue disagree in places. This report preserves both and conservatively leaves materially unmeasured current criteria inconclusive. Pinned 4.0.0 and independent effect verdicts remain separate.', '',
        report.scope, '',
        `Owned run \`${m.runId}\`; stock run \`${m.stockRunId}\`; started ${m.startedAt}. App source \`${m.appRevision}\`, scripts \`${m.scriptsRevision}\`, receipt ${m.receiptSchema}, source policy \`${m.sourcePolicy}\`. The App was built with the repository wrapper; later integration of CI-only changes did not change its source tree. The app version ${m.appVersion} is operator supplied, not independently verified. The raw collector identity remains \`${m.rawIdentity}\` and suiteComplete remains \`${m.suiteComplete}\`; the independent receipt binds the disposable measured process, executable/assembly digests, source graph, settings, geometry and cleanup.`, '',
        '## Four gaps', '',
        '- **RI:** newly verified existing behavior. Exact pinned MARKER fixture, whole viewport before/after, blank top row, downward shifts, discarded bottom row, cursor and margins; separate interior-region and below-boundary controls. This is distinct from scrollback.reverse-index.',
        '- **reset():** a new explicit asynchronous desktop backend API, implemented by a retained sequence and called three times under measurement. The dirty experiment seeds history, alternate buffer, origin mode, margins, insert mode, disabled wrap, hidden cursor and styling. Attempt 1 exposed IRM surviving RIS, and Codex found that alternate-only DECOM setup could restore a clean main bank. The API now explicitly normalizes the promised modes; standalone dirty-main and dirty-main-before-alternate controls prevent that masking. The pinned Hello World test and the broader clean-state promise both pass. This qualifies that named adapter API, not an upstream stock App reset endpoint or RIS alone.',
        '- **TERM:** actual child output is retained on the qualified screen and bound to the independently proved worker, run and disposable launch profile. It reports xterm-256color without switching profiles. Upstream remains manual/ungraded; this independent environment conclusion is included in the 80-row denominator.',
        '- **OSC 1:** new bounded read-only production observation; existing parser behavior newly verified. Distinct icon payloads, OSC 2 title independence, icon-only stack restoration, unrelated cells/cursor and original title/icon restoration pass. Parser icon state, stack and actual tab title are distinct observations. Host icon presentation is not claimed. The pinned callback remains diagnostic/inconclusive.', '',
        '## Reproduce and validate', '', '```sh', 'node scripts/terminfo-core-results.mjs --check',
        'node scripts/terminfo-core-results.mjs --assert-current-source',
        'node --test scripts/terminfo/core-gaps.test.mjs scripts/terminfo/core-results.test.mjs', '```', '',
        'Requires Node 24+ and Python 3; no desktop, network, git history or archived-code execution is needed for --check. The command validates archive/mapping digests, extracts bounded data, reissues the complete receipt with trusted graders, rebuilds all 80 rows and compares JSON and Markdown. --assert-current-source additionally verifies the integrated checkout against the measured production-source manifest. The frozen historical publications remain unchanged.', '',
        'Sources: [current Core list](https://terminfo.dev/baseline/core), [current catalogue](https://github.com/beorn/terminfo.dev/blob/' + mapping.upstreamRevision + '/content/features.json). The versioned [mapping](requirements-v1.json) contains all exact site criteria, current catalogue criteria, frozen-catalogue differences, evidence references and per-row match rationales. [Catalogue source bytes](catalogue-sources.zip), [complete owned evidence](owned-run.zip), [refused first attempt](attempt-1-refused.zip), [superseded run 2](attempt-2-superseded.zip), [superseded run 3](attempt-3-superseded.zip), [invalid-input attempt](attempt-4-invalid-input.zip), [machine-readable report](report.json), [production-source manifest](app-source-manifest.json).', '',
        'The remaining twenty criteria are listed in the rows below. They are missing or mismatched experiments, so no terminal failure is inferred. The three stock-inconclusive-by-design scrolling cases (set-region, alt-screen, decstbm-reset) use their separately identified same-run effect contracts; their original pinned inconclusive verdicts are preserved.', '',
        '## All 80 Core requirements', '', '| Exact ID / current site requirement | Core | Layer / evidence | Pinned stock / qualified; effect | Match / limitation |', '|---|---|---|---|---|'];
    for (const row of report.rows) {
        const refs = row.evidence.map(e => `${e.file}#${e.caseId}`).join('; ');
        const qualified = row.pinnedProbe.qualified.map(p => p.caseId + ':' + p.verdict).join(', ') || 'n/a';
        const effects = row.effectVerdicts.map(e => e.caseId + ':' + e.verdict).join(', ') || 'query trace';
        lines.push(`| [\`${row.id}\`](${mapping.rows.find(r => r.id === row.id).url}) — ${cell(row.requirement)} | **${row.conclusion}** | ${cell(row.observationLayers.flat().join(', '))}; ${cell(refs)} | ${row.pinnedProbe.stockVerdict} / ${cell(qualified)}; ${cell(effects)} | ${cell(row.explanation)} |`);
    }
    lines.push('', '## Attempts and applicability', '', ...mapping.attempts.map(a => `- \`${a.runId}\` — ${a.status}: ${a.reason}`), '',
        'Only the complete current run supplies positive results. Every predecessor and finding suite reconfirmed in that run; unrelated non-Core failures remain retained and do not change Core totals. Fresh stock-supported rows are checked from their raw query/write traces and setup controls, not copied from a historical outcome map. The source manifest and measured-script hashes distinguish measured code from later report/test changes and a mechanical serial-plan refactor for Sonar. Regression tests prove that each unchanged write/API call and observation completes before the next step, and that a refused read stops the plan. These changes do not alter measured inputs, observations or effect grading and do not require another desktop run.', '', mapping.sourceAttribution);
    return lines.join('\n') + '\n';
}

export function reportText(report) {
    const { rows, ...header } = report;
    return JSON.stringify(header, null, 2).slice(0, -2) + ',\n  "rows": [\n' + rows.map(row => '    ' + JSON.stringify(row)).join(',\n') + '\n  ]\n}\n';
}

export async function buildResults(base = root) {
    const mapping = loadMapping(base), temp = mkdtempSync(join(tmpdir(), 'ntilde-core-results-'));
    try { extractOwnedArchive(base, mapping, temp); const identity = verifyOwnedRun(temp, mapping);
        return { mapping, report: deriveReport(temp, mapping, identity) };
    } finally { rmSync(temp, { recursive: true, force: true }); }
}

export function assertCurrentSource(base = root) {
    const mapping = loadMapping(base), manifest = read(join(base, publication, 'app-source-manifest.json'));
    const gitExecutable = process.platform === 'win32' ? process.env.NTILDE_TERMINFO_GIT_EXECUTABLE : '/usr/bin/git';
    check(typeof gitExecutable === 'string' && /^(?:[A-Za-z]:[\\/]|\/)/.test(gitExecutable), 'an absolute Git executable is required');
    const git = spawnSync(gitExecutable, ['ls-files', '--cached', '--others', '--exclude-standard', '--', 'src', 'assets',
        'Directory.Build.props', 'Directory.Build.targets', 'global.json'], { cwd: base, encoding: 'utf8', windowsHide: true });
    check(git.status === 0 && same(git.stdout.trim().split('\n').sort(byText), Object.keys(manifest).sort(byText)), 'production source file set differs');
    for (const [file, sha] of Object.entries(manifest)) check(digest(Buffer.from(readFileSync(join(base, file)).toString('latin1').replaceAll('\r\n', '\n'), 'latin1')) === sha,
        'reported production source differs: ' + file);
    return mapping.measurement.appRevision;
}

async function main() {
    const mode = process.argv[2]; check(process.argv.length === 3 && ['--check', '--write', '--assert-current-source'].includes(mode), 'use --check, --write or --assert-current-source');
    if (mode === '--assert-current-source') { console.log('Measured production sources match: ' + assertCurrentSource()); return; }
    const { report, mapping } = await buildResults();
    const outputs = { 'report.json': reportText(report), 'report.md': readable(report, mapping) };
    for (const [file, text] of Object.entries(outputs)) {
        const path = join(root, publication, file);
        if (mode === '--write') writeFileSync(path, text);
        else check(existsSync(path) && readFileSync(path, 'utf8').replaceAll('\r\n', '\n') === text, 'stale report ' + file);
    }
    console.log(`Core results: ${report.totals.pass} pass / ${report.totals.fail} fail / ${report.totals.inconclusive} inconclusive; 80 exact rows.`);
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try { await main(); }
    catch (error) { console.error(error.stack); process.exitCode = 1; }
}
