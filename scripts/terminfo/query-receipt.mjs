import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { snapshotHash } from './agent-observer.mjs';
import { querySuiteId, ownedQueryPolicy, buildQueryReport, checkQueries as check } from './queries.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const files = ['query-snapshots.jsonl', 'query-actions.jsonl', 'query-initial.json', 'query-observations.json', 'query-unavailable.json', 'query-failed-observation.json', 'query-raw-failure.json'];
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}
function read(output, file, limit = 16 * 1024 * 1024) {
    const path = join(output, file), stat = lstatSync(path); check(stat.isFile() && !stat.isSymbolicLink() && stat.size <= limit);
    const bytes = readFileSync(path); check(bytes.length <= limit); return bytes;
}
const json = (output, file) => JSON.parse(read(output, file));
const journal = (output, file) => read(output, file).toString().trim().split('\n').filter(Boolean).map(value => JSON.parse(value));
/** Any retained query journal, declaration or import requires the complete v11 query suffix. */
export function includesQueryEvidence(output, evidence, context, proof) {
    if (['readonly-owned-source-graph-v4', 'readonly-owned-source-graph-v5', 'readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7', 'readonly-owned-source-graph-v8', 'readonly-owned-source-graph-v9', 'readonly-owned-source-graph-v10', 'readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18'].includes(context.sourceSnapshotPolicy) || ['readonly-owned-source-graph-v4', 'readonly-owned-source-graph-v5', 'readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7', 'readonly-owned-source-graph-v8', 'readonly-owned-source-graph-v9', 'readonly-owned-source-graph-v10', 'readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18'].includes(proof.sourceSnapshot?.policy)
        || context.querySuiteId !== undefined || context.ownedQueryPolicy !== undefined || evidence.queryEvidence !== undefined
        || Object.keys(context).some(key => key.startsWith('query') && key.endsWith('SourceSha256'))
        || proof.proofs?.some(value => value.queries !== undefined) || files.some(file => present(join(output, file)))) return true;
    if (!present(join(output, 'sources'))) return false;
    return readdirSync(join(output, 'sources')).some(file => file === 'queries.mjs' || file.startsWith('query-') || (file.endsWith('.mjs')
        && /(?:\bfrom|\bimport\s*\()\s*['"]\.\/(?:terminfo\/)?quer(?:ies|y-[a-z-]+)\.mjs['"]/.test(read(output, join('sources', file), 2 * 1024 * 1024).toString())));
}
/** Full query rederivation binds both original journals, exact prefix challenges and process facts. */
export function checkQueryEvidence(output, binding, runId, context, proof, prefixEvidence) {
    const original = proof.proofs?.[0], marker = original?.queries;
    check(['readonly-owned-source-graph-v4', 'readonly-owned-source-graph-v5', 'readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7', 'readonly-owned-source-graph-v8', 'readonly-owned-source-graph-v9', 'readonly-owned-source-graph-v10', 'readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18'].includes(context.sourceSnapshotPolicy) && context.querySuiteId === querySuiteId
        && context.ownedQueryPolicy === ownedQueryPolicy && marker?.policy === ownedQueryPolicy
        && Object.keys(marker).every(key => ['policy', 'binding', 'actions', 'seal'].includes(key))
        && !present(join(output, 'query-failed-observation.json')) && !present(join(output, 'query-raw-failure.json')));
    check(binding?.runId === runId && binding.suiteId === querySuiteId && ['query-observations.json', 'query-unavailable.json'].includes(binding.file)
        && Object.keys(binding).every(key => ['runId', 'suiteId', 'file', 'sha256', 'counts', 'provisionalUntil', 'status', 'reason'].includes(key)));
    const bytes = read(output, binding.file); check(hash(bytes) === binding.sha256);
    const frames = journal(output, 'query-snapshots.jsonl'), actions = journal(output, 'query-actions.jsonl');
    if (binding.file === 'query-unavailable.json') {
        const report = JSON.parse(bytes);
        const prerequisites = ['inputEvidence', 'staticImageEvidence'].filter(key => prefixEvidence[key]?.status === 'unavailable')
            .map(bindingKey => ({ bindingKey, ...prefixEvidence[bindingKey] }));
        check(Object.keys(report).length === 12 && Object.keys(report).every(key => ['schemaVersion', 'runId', 'suiteId', 'status', 'reason',
            'fixtureStarted', 'plannedActions', 'plannedContracts', 'observedActions', 'observedContracts', 'observedFrames', 'prerequisites'].includes(key))
            && report.schemaVersion === 1 && report.runId === runId && report.suiteId === querySuiteId
            && report.status === 'unavailable' && report.reason === 'queryPrerequisiteUnavailable'
            && report.fixtureStarted === false && report.plannedActions === 44 && report.plannedContracts === 40
            && report.observedActions === 0 && report.observedContracts === 0 && report.observedFrames === 0
            && prerequisites.length > 0 && snapshotHash(report.prerequisites) === snapshotHash(prerequisites)
            && binding.status === 'unavailable' && binding.reason === report.reason && snapshotHash(binding.counts) === snapshotHash({})
            && !frames.length && !actions.length && !marker.actions.length && marker.binding === undefined && marker.seal === undefined
            && !present(join(output, 'query-initial.json')) && !present(join(output, 'query-observations.json'))
            && (present(join(output, 'input-unavailable.json')) || present(join(output, 'image-unavailable.json'))));
        return;
    }
    check(binding.status === undefined && binding.reason === undefined && !present(join(output, 'query-unavailable.json')));
    const input = json(output, 'input-observations.json'), image = json(output, 'image-observations.json');
    const prior = [...input.snapshots.filter(frame => frame.capture).map(frame => frame.capture.nonces.join(',')),
        ...image.snapshots.filter(frame => frame.capture).map(frame => frame.capture.nonces.join(','))];
    check(prior.length === 110 && new Set(prior).size === 110 && input.seal.disposed && !input.seal.failed && image.seal.disposed && !image.seal.failed);
    const report = JSON.parse(bytes);
    const initial = json(output, 'query-initial.json');
    check(Date.parse(initial.before.beforeOwnership.at) >= Date.parse(image.seal.ownership.at));
    const derived = buildQueryReport({ binding: marker.binding, priorChallenges: prior, initial, actions, frames, seal: marker.seal });
    check(snapshotHash(report) === snapshotHash(derived) && snapshotHash(actions) === snapshotHash(marker.actions)
        && Date.parse(marker.seal.ownership.at) <= Date.parse(proof.cleanedUpAt));
    const b = derived.binding, sameProcess = (actual, expected) => ['pid', 'ppid', 'uid', 'tty', 'executable'].every(key => actual[key] === expected?.[key]);
    check(b.runId === runId && b.ownerPid === original.app.pid && b.workerPid === original.worker.pid && b.uid === original.worker.uid
        && b.appExecutable === original.app.executable && b.workerExecutable === original.worker.executable && b.tty === original.tty
        && sameProcess(initial.before.beforeOwnership.app, original.app) && sameProcess(initial.before.beforeOwnership.worker, original.worker));
    const cell = json(output, 'cell-observations.json'), screen = initial.before.screen;
    check(cell.snapshots[0].paneId === b.paneId && cell.snapshots[0].screen.rows === screen.rows && cell.snapshots[0].screen.cols === screen.cols);
    const settings = json(output, 'initial-settings.json');
    check(settings.Profiles?.length === 1 && settings.Profiles[0].Name === b.profileName && settings.Profiles[0].Type === 0
        && settings.SessionPersistence === 'Off' && settings.AgentAccessObserveEnabled === true);
    const counts = {};
    for (const item of derived.observations) { const key = item.contractMatched ? 'matched' : 'mismatched'; counts[key] = (counts[key] ?? 0) + 1; }
    check(snapshotHash(binding.counts) === snapshotHash(counts));
}
