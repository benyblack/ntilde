// A `node --test` reporter that writes how long each test file's tests took, as JSON:
// {"<file path>": seconds, ...}. The Script self-tests shards run it beside the spec reporter
// (`--test-reporter=./scripts/ci/node-tests-timing.mjs --test-reporter-destination=<file>`) and
// upload the result, so scripts/ci/node-tests-shard.py's table can be refreshed from a real
// windows run with `node-tests-shard.py --update <files>`.
//
// Seconds are the sum of each file's top-level tests (their own duration_ms), which leaves out
// the time a file spends importing its modules before its first test. For these files that is a
// small part (checked against wall-clock per-file runs when the table was first made), and it is
// the same on every runner, so the balance the table gives is not skewed by it.
export default async function* timing(source) {
  const seconds = {};
  for await (const event of source) {
    if ((event.type === 'test:pass' || event.type === 'test:fail') && event.data.nesting === 0 && event.data.file) {
      seconds[event.data.file] = (seconds[event.data.file] ?? 0) + (event.data.details?.duration_ms ?? 0) / 1000;
    }
  }
  yield JSON.stringify(seconds, null, 1) + '\n';
}
