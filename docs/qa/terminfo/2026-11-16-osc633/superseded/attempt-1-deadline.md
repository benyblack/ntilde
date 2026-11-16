# Attempt 1: incomplete after the owned deadline

Run `e4fd81d6c7bb7e0d0557276f1eb740c1` used the Release sidecar built from production revision `b9d7c6273683d8546b874781f605817345e23ce1`, with measured scripts revision `583d23bb7e1dcfc7584255e348c7ce32bdb57072` and operator-supplied App version 0.14.0. The launcher reported `Owned collection exceeded 5 minutes` and released its desktop lease after verified cleanup. The retained `ownership-proof.json` states `completed: false`; no `cell-receipt.json` exists. Nothing in this attempt is admitted as a qualification result.

The target suite completed 63 paired frames and wrote `vs633-run.json` before the deadline. Later, OSC 9 completed at 09:59:54 UTC; the deadline expired at about 10:01:07 UTC during the subsequent notification suite. The unchanged notification plan alone specifies eighteen 8,000 ms waits, so its 144 seconds of planned waits cannot fit in the approximately 73 seconds remaining, even before its other work and later suites. The failed attempt is retained unchanged as [attempt-1-deadline.zip](attempt-1-deadline.zip), SHA-256 `4bcf5a27a3dd3ece0c0d157c5c2fb7e0d9678115e74048b1be4285d81cd0666f`.

Command, from the worktree root:

```sh
/opt/homebrew/Cellar/node@24/24.22.2_2/bin/node scripts/terminfo-owned.mjs --executable /Users/behnam/.ntilde-qa-sidecars/osc633/Release/net10.0/Ntilde --app-version 0.14.0
```
