// Runs `node:test` files in the order given and records how long each file's tests took.
//
// Usage: node scripts/ci/node-tests-run.mjs <timing.json> <file> [<file> ...]
//
// The Script self-tests shards run their files through this instead of `node --test <files>`,
// because the CLI sorts the files by name before it starts them (node 24,
// lib/internal/test_runner/runner.js). scripts/ci/node-tests-shard.py prints a shard's files
// longest first so that the long ones start at once; through the CLI the run went by name
// instead, and a 257 s file that sorts late held windows shard b to 456 s beside 248 and 183
// (run 42912477328). `run({ files })` takes the list as it is.
//
// Otherwise it runs as the CLI does: one process per file, as many at a time as the CLI's default
// (os.availableParallelism() - 1), the spec reporter on stdout, and exit code 1 when any test
// fails or a file cannot load. NODE_TESTS_RUN_CONCURRENCY=<n> runs n at a time instead, which the
// self-test (scripts/tests/node_tests_shard_tests.py) uses to see the start order.
//
// <timing.json> receives {"<file path>": seconds, ...}: the sum of each file's top-level test
// durations, for refreshing node-tests-shard.py's table (`node-tests-shard.py --update`). That
// leaves out the time a file spends importing its modules before its first test, which for these
// files is a small part and the same on every runner, so it does not skew the balance.
import { writeFileSync } from 'node:fs';
import { pipeline } from 'node:stream/promises';
import { run } from 'node:test';
import { spec } from 'node:test/reporters';

const [timingPath, ...files] = process.argv.slice(2);
if (!timingPath || files.length === 0) {
  console.error('usage: node-tests-run.mjs <timing.json> <file> [<file> ...]');
  process.exit(2);
}

const seconds = {};
let failed = false;
const record = (data) => {
  if (data.nesting === 0 && data.file) {
    seconds[data.file] = (seconds[data.file] ?? 0) + (data.details?.duration_ms ?? 0) / 1000;
  }
};

const limit = Number.parseInt(process.env.NODE_TESTS_RUN_CONCURRENCY ?? '', 10);
const stream = run({ files, concurrency: limit > 0 ? limit : true });
stream.on('test:pass', record);
stream.on('test:fail', (data) => {
  failed = true;
  record(data);
});
// end: false - stdout stays open for whatever the process prints after the run.
await pipeline(stream, new spec(), process.stdout, { end: false });

writeFileSync(timingPath, JSON.stringify(seconds, null, 1) + '\n');
process.exitCode = failed ? 1 : 0;
