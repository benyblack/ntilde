import { mkdtempSync, rmSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { measuredAppRevision, measuredScriptsRevision } from './mex-publication.mjs';
import { loadModernInputs, modernPublication, pinnedJson, digest, parseJson, requireModern, same, requirementsSha256, mappingSha256 } from './modern-inputs.mjs';
import { evaluateModern, modernEvidenceReference } from './modern-grading.mjs';

export const modernRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const evidencePublication = 'docs/qa/terminfo/2026-11-15-mouse-extended';
export const evidenceManifestSha256 = '090807277ee8fe957364345bf9ca73852b6dfa47a66ab46d2dc9b2bcd70110cc';
export const ownedRunId = '59b3fbe52f6062679e5233e0b62fb9d2';

const relatedSources = {
    'sgr.underline.color': 'sgcl-observations.json', 'sgr.blink': 'sgcl-observations.json',
    'cursor.shape': 'curq-observations.json', 'input.modify-other-keys': 'mok-observations.json',
    'input.x10-mouse': 'rin-observations.json', 'input.button-event-mouse': 'rin-observations.json',
    'modes.application-keypad': 'rin-observations.json', 'extensions.osc0-icon-title': 'ost-observations.json',
    'extensions.osc2-title': 'ost-observations.json', 'extensions.osc7-cwd': 'lnk-observations.json',
};

function priorOwnedResult(rule, evidence) {
    const file = rule.file ?? relatedSources[rule.id];
    if (!file) return null;
    const sourceId = rule.sourceId ?? rule.id, report = evidence.read(file);
    const item = report.cases?.find(value => value.caseId === sourceId) ?? (report.pinned?.probeId === sourceId ? report : null);
    requireModern(report.runId === evidence.runId && item, 'missing related owned evidence ' + rule.id);
    const pinned = item.pinned;
    const pinnedOutcome = pinned?.observation?.outcome ?? pinned?.termless?.observation?.outcome
        ?? (pinned?.notTested ? 'inconclusive' : null) ?? (pinned?.pass === true ? 'supported' : null);
    return { file, sourceId, runId: report.runId,
        effectVerdict: item.contractResult?.outcome ?? item.effectContract?.outcome ?? item.effect?.outcome ?? null,
        pinnedTermlessVerdict: pinnedOutcome,
        pinnedTerminalVerdict: item.terminalPinned?.observation?.outcome ?? pinned?.terminal?.observation?.outcome ?? null,
        note: 'Historical pinned/effect results are retained separately; they do not decide a materially different Modern criterion.' };
}

/** Reissue the whole historical receipt with trusted current graders. Archives
 * supply data only; no code from their sources/ tree is imported or executed. */
export function withModernEvidence(body, base = modernRoot) {
    const source = join(base, evidencePublication);
    const manifest = pinnedJson(join(source, 'manifest.json'), evidenceManifestSha256);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-modern-'));
    try {
        extractOwnedArchive(source, manifest, scratch, join(source, 'owned-run.zip'));
        const original = parseJson(join(scratch, 'cell-receipt.json'));
        const context = parseJson(join(scratch, 'context.json'));
        requireModern(context.runId === ownedRunId && original.schemaVersion === 64 && original.fixtureRemoved === true
            && context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v58', 'wrong/incomplete measurement');
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...binding } = original;
        rmSync(join(scratch, 'cell-receipt.json'));
        const rederived = completeOwnedReceipt(scratch, join(scratch, 'already-removed-private-fixture'), binding);
        requireModern(same({ ...rederived, completedAt }, original), 'complete receipt rederivation differs');
        const cache = new Map();
        const read = file => {
            requireModern(/^[a-z][a-z0-9-]*\.json$/.test(file), 'invalid evidence filename');
            if (!cache.has(file)) cache.set(file, parseJson(join(scratch, file)));
            return cache.get(file);
        };
        const raw = read('raw.json');
        requireModern(raw.runId === '29a8f5ad03abca8748199bd253a72924' && raw.probeHash === '16a690f34c0e', 'wrong stock identity');
        return body({ read, raw, context, receipt: original, runId: context.runId, manifest });
    } finally { rmSync(scratch, { recursive: true, force: true }); }
}

/** Grade all 69 rows; missing evidence is an explicit blocker, never a pass. */
export function deriveModernReport(inputs, evidence) {
    const { requirements, mapping } = inputs;
    const rows = requirements.rows.map((requirement, index) => {
        const rule = mapping.rows[index];
        requireModern(rule.id === requirement.id && digest(requirement.requirement) === rule.criterionSha256, 'criterion/mapping mismatch');
        const result = evaluateModern(rule, evidence);
        const stock = evidence.raw.observations.find(item => item.featureId === requirement.id);
        return { ...requirement, ...result, criterionPath: rule.criterionPath,
            evidence: modernEvidenceReference(rule, evidence), stockVerdict: stock?.outcome ?? 'not-graded',
            stockEvidence: stock?.evidence ?? null, priorOwned: priorOwnedResult(rule, evidence) };
    });
    requireModern(rows.length === 69 && new Set(rows.map(row => row.id)).size === 69, 'lost/duplicate Modern row');
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(verdict => [verdict, rows.filter(row => row.conclusion === verdict).length]));
    const { context: c, receipt, manifest, raw } = evidence;
    const firstFrame = evidence.read('cell-observations.json').snapshots[0];
    return {
        schemaVersion: 1, baseline: 'modern', asOf: requirements.asOf, requirementsCount: 69, totals,
        ownedCriterionPassPercentage: totals.pass / 69 * 100,
        scope: mapping.scope,
        evidencePolicy: 'Frozen-criterion reassessment of one historical owned macOS direct-local App run. A pass is limited to the selected semantic predicate and recorded observer. It is not an official terminfo.dev result, upstream App/library execution, cross-terminal ranking or claim about every setting, platform, font or input path. Inconclusive means missing criterion-matched evidence, not a terminal defect. No new desktop collection was performed.',
        requirementsSha256, mappingSha256, upstream: requirements.upstream,
        measurement: { runId: c.runId, startedAt: c.startedAt, appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision,
            appVersion: c.appVersion, appVersionSource: c.appVersionSource, executableSha256: c.executableSha256, assemblySha256: c.assemblySha256,
            sourcePolicy: c.sourceSnapshotPolicy, sourceSnapshotManifestSha256: c.sourceSnapshotManifestSha256,
            receiptSchema: receipt.schemaVersion, receiptCompletedAt: receipt.completedAt, fixtureRemoved: receipt.fixtureRemoved,
            geometry: { rows: firstFrame.screen.rows, cols: firstFrame.screen.cols }, publication: evidencePublication,
            manifestSha256: evidenceManifestSha256, archiveSha256: manifest.archives['owned-run.zip'], newMeasurement: false },
        stock: { runId: raw.runId, counts: raw.observations.reduce((counts, item) => ({ ...counts, [item.outcome]: (counts[item.outcome] ?? 0) + 1 }), {}),
            identity: raw.identity, suiteComplete: raw.suiteComplete,
            note: 'Original whole-suite stock grades, not a Modern score. The measurement uses its retained settings, including opt-in title reporting. Current Modern conclusions never rewrite these grades.' },
        rows,
    };
}

export function buildModernReport(base = modernRoot) {
    const inputs = loadModernInputs(base);
    return withModernEvidence(evidence => deriveModernReport(inputs, evidence), base);
}

const tableText = value => String(value).replaceAll('|', '&#124;').replaceAll('\r', '').replaceAll('\n', ' ');
export function readableModern(report) {
    const t = report.totals, m = report.measurement, cursor = report.cursorMeasurement;
    const lines = [cursor ? '# Ntilde Modern assessment — four cursor requirements' : '# Ntilde Modern baseline — 2026-11-15', '',
        `**${t.pass} pass / ${t.fail} fail / ${t.inconclusive} inconclusive across 69 frozen requirements.**`, '',
        report.evidencePolicy, '', report.scope, '',
        ...(cursor ? ['[Original Modern publication and frozen inputs](../2026-11-15-modern-results/report.md).', ''] : []),
        '## Reading the results', '',
        ...(cursor ? ['- The 69 criteria are loaded from the original frozen requirements and upstream snapshot. The four cursor verdicts below use the new trace.'] : [
        '- The requirements are the exact 69 Modern catalogue rows and live-page membership captured on 2026-11-15. The original upstream source bytes and page excerpt are retained in `upstream-snapshot.zip`; SHA-256 pins admit these bytes. Git blob IDs are retained citation metadata, not security digests.']),
        '- Stored-cell predicates are observed through the owned App projection. They do not establish an upstream headless-library result or an application pixel result. Each row names the selected path.',
        '- Physical/OS-generated input, real-time blink, native focus/paste and changed current controls are not inferred from older flags, synthetic phases, generated events or a similarly named case.',
        '- Pinned 4.0.0 stock grades stay beside the new conclusions. This does not change the 80/80 frozen Core report or the qualification inventory, and the percentage must not be compared with the public terminal leaderboard.', '',
        '## Upstream attribution', '',
        'Catalogue and baseline material: © 2025–2026 Bjorn Stabell, [terminfo.dev](https://terminfo.dev/baseline/modern), [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The original licence is retained as `LICENSE` in `upstream-snapshot.zip`. Catalogue and baseline source bytes are unmodified; the page excerpt, 69-row selection, evidence mapping and assessment are identified Ntilde adaptations. Upstream source revision and Git blob references are retained in `requirements-v1.json`.'.replace('in `requirements-v1.json`', cursor ? 'in the [frozen requirements](../2026-11-15-modern-results/requirements-v1.json)' : 'in `requirements-v1.json`'), '',
        '## Measurement and provenance', '',
        `Reused owned run \`${m.runId}\`, started ${m.startedAt}. App source \`${m.appRevision}\`; measured scripts \`${m.scriptsRevision}\`; App version \`${m.appVersion}\` (${m.appVersionSource}). Geometry ${m.geometry.rows}×${m.geometry.cols}; source policy \`${m.sourcePolicy}\`; receipt ${m.receiptSchema}, completed ${m.receiptCompletedAt}.`, '',
        `The archive is [the existing mouse-extended publication](../2026-11-15-mouse-extended/owned-run.zip), SHA-256 \`${m.archiveSha256}\`. The complete receipt, source graph, observations and cleanup are rederived in an isolated temporary directory. No archived source is executed and no raw artifact is modified.`
        + (cursor ? '' : ' This report is a later evidence reassessment, not a fresh measurement or a source-to-binary attestation.'), '',
        ...(cursor ? readableCursorMeasurement(cursor) : []),
        `Original stock totals: ${report.stock.counts.supported} supported / ${report.stock.counts.unsupported} unsupported / ${report.stock.counts.inconclusive} inconclusive; suiteComplete=${report.stock.suiteComplete}. Those are whole-suite results under the recorded settings, not a Modern score.`, '',
        '## Per-requirement assessment', '', '| ID | Result | Selected observation path | Evidence / blocker |', '| --- | --- | --- | --- |'];
    for (const row of report.rows) {
        const newTrace = cursor && row.evidence?.runId === cursor.runId;
        const detail = newTrace ? `[${row.evidence.sourceId}](#${row.evidence.sourceId})`
            : row.blocker ?? [row.evidence.file, row.evidence.sourceId].join(' — ');
        lines.push(`| \`${row.id}\` | ${row.conclusion} | ${tableText(row.criterionPath)} | ${tableText(detail)} |`);
    }
    if (cursor) lines.push(...readableCursorTraces(report.rows, cursor.runId));
    lines.push('', '## Reproduce', '', 'Requires Node 24+ and Python 3; runs entirely offline without a desktop lease.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check' + (cursor ? ' --cursor' : ''), '```', '',
        cursor
            ? '`report.json` retains every frozen criterion, evidence reference, selected observation, original stock verdict and remaining blocker. The [original publication](../2026-11-15-modern-results/report.md) retains the frozen input files and their upstream attribution.'
            : '`report.json` retains the full frozen criterion, evidence references, selected observations, original stock verdict and exact per-row blocker. `requirements-v1.json` and `mapping-v1.json` are versioned inputs; future criteria need a new version/publication rather than silently changing this result.', '');
    if (cursor) {
        lines.push('For a fresh capture, build the Release App at the recorded App revision using the repository build wrapper. Prepare the pinned collector at the measured scripts revision using the [QA guide](../../QA_TERMINFO.md). Run the owned collector with the standalone App on an awake macOS desktop.', '',
            '```sh',
            'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
            'node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope osc633',
            '```', '');
    }
    return lines.join('\n');
}

function readableCursorTraces(rows, runId) {
    const lines = ['', '## Cursor traces', '',
        'The positions below are zero-based. CPR uses one-based coordinates. Each transition has two agreeing App reads and a raw CPR ahead of its DA fence.', ''];
    const traces = new Map(rows.filter(row => row.evidence?.runId === runId)
        .map(row => [row.evidence.sourceId, row.observed]));
    for (const [id, trace] of traces) {
        lines.push(`### ${id}`, '', '| Step | App position | CPR | Journal frame |', '| --- | --- | --- | --- |');
        for (const step of trace.transitions) {
            lines.push(`| ${step.role} | (${step.position.join(', ')}) | \`${step.cpr.slice(2, -1)}\` | ${step.frameIndex} |`);
        }
        lines.push('');
    }
    return lines;
}

function readableCursorMeasurement(cursor) {
    return [
        `New direct-local macOS cursor run \`${cursor.runId}\`, started ${cursor.startedAt}. App source \`${cursor.appRevision}\`; measured scripts \`${cursor.scriptsRevision}\`; App version \`${cursor.appVersion}\` (${cursor.appVersionSource}). Geometry ${cursor.geometry.rows}×${cursor.geometry.cols}; source policy \`${cursor.sourcePolicy}\`; receipt ${cursor.receiptSchema}, completed ${cursor.receiptCompletedAt}.`, '',
        `The [cursor run archive](owned-run.zip) has SHA-256 \`${cursor.archiveSha256}\`. In it, \`mcur-run.json\` contains the exact writes and replies, \`mcur-snapshots.jsonl\` contains both App reads per frame, and \`mcur-observations.json\` records the derived traces. Each experiment began on a cleared alternate screen at home with origin mode and left/right margin mode off. The final pair matched the incoming main-screen projection after the alternate fixture was cleared and homed. The receipt verifies process and fixture cleanup.`, '',
        ...cursor.attempts.map(attempt => `Retained attempt [${attempt.archive}](${attempt.archive}). ${attempt.reason}`),
    ];
}

export const serializeModern = report => JSON.stringify(report, null, 2) + '\n';
export const modernOutputPaths = base => ({ json: join(base, modernPublication, 'report.json'), markdown: join(base, modernPublication, 'report.md') });
