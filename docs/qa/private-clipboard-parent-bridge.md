# Parent-side private clipboard callback bridge

This is the parent-side seam for #774. It is **not wired into the owned collector**.
No App is launched and no new source graph, receipt, OSC52 permission, OS clipboard
provider or Windows desktop qualification is introduced here. The existing private
controller, request transport and source preparation retain their own boundaries.

The later versioned integration will let the unchanged pinned Q11 callback request
these controls through the already authenticated owner channel. The parent will
own `PrivateClipboardBridge`, the existing controller, and all private App RPCs.
Before routing **every** frame, that owner must authenticate its capability/run ID,
prove the original App/worker/PTY/source/pane binding under its shared deadline,
and reject unauthenticated frames without invoking this bridge or its abort path.
The constructor's independent `verifyBinding` callback remains mandatory and is
checked by the controller before and after each App operation. A digest is only
an equality witness; neither this helper nor an App response proves ownership.

## Callback lifecycle

The bridge accepts only exact frames with these fields:

| Frame | Additional field | Parent effect |
| --- | --- | --- |
| `start` | `probeId` | Lazily start the next of the three fixed Q11 callbacks; expose controls only after begin, restore and seed readback succeed. |
| `arm` | `value` | Await the private write arm before acknowledging; the worker may then emit the unchanged original OSC52 write. |
| `read` | none | Return only the controller's independently validated expected private value. |
| `seed` | `value` | Await the fixed private seed operation and independent readback. |
| `complete` | none | Settle the current callback and await restore plus independent fixed-seed readback. |
| `finish` | none | After all three callbacks and exactly two accepted terminal writes, independently read the restored seed and await the App's final close. |

The bridge does not accept callback results, grader overrides, arbitrary App
methods or capability fields. The eventual worker wrapper must preserve the
original callback result only after `complete` acknowledges restoration. The
roundtrip/read empty OSC52 denial remains negative/inconclusive evidence. An
accepted write here establishes private App storage only; it establishes neither
native interchange nor user consent to clipboard reads.

Every control is serialized. Unknown fields, malformed nonces, missing phases,
wrong callback order, concurrent requests and binding failures are sticky. A
collector catching an exception cannot resume the bridge or manufacture a final
close claim. An original begin failure settles the start request even when no
callback fixture was exposed. Parent cancellation uses `abort(error)`, rejects
the active callback, and awaits the controller's bounded restoration attempt;
original and cleanup failures survive together. Binding loss is checked before
any cleanup RPC. Repeated abort does not replay operations. After a successful
finish, later frames are refused and abort is inert.

Completion requires both the controller's actual final close and the bridge's
uninterrupted final acknowledgment. If another request arrives during close,
the actual close result may be retained in the controller journal, but bridge
completion remains false. The returned evidence is a defensive copy; it does
not replace the later independent worker/App exit and fixture/source removal
requirements.

## Verification and next integration

The deterministic bridge tests cover healthy ordered callbacks, waiting for arm
acknowledgment, malformed/pre-callback refusal, original begin error retention,
missing callback phases, external abort, binding loss, overlapping controls,
abort during begin, a competing request during final close, and simultaneous
callback/restore failure. They use synthetic private storage and no desktop,
process or native clipboard. The Ubuntu/Windows foundation workflow runs them
alongside the existing controller, source and transport tests. Its established
Windows Unix-socket skips remain explicit; this bridge's tests have no skips.

The final versioned collector integration must add the worker wrapper and lazy
Q11 tail wiring, prove source/operation/binding journals against the retained
original bundle and actual raw results, freeze the v11 74-file map, and introduce
its own complete new source policy and receipt suffix. All historical archives,
reports and grades remain unchanged. Only after that integration's reviews and
CI pass may a separately announced owned GUI run establish runtime evidence.
