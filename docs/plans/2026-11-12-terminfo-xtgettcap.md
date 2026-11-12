# XTGETTCAP qualification working plan

Target only `device.xtgettcap`. The pinned `terminfo.dev@4.0.0` bundle inspected
for this plan has original SHA-256
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`
(collector revision `cf7d8759…`, probeHash `16a690f34c0e`). This plan is not
measurement evidence.

## Pinned definition (read from the bundle, byte for byte)

```js
deviceQuery({
  id: "device.xtgettcap",
  query: "\x1BP+q544e\x1B\\",            // DCS + q 544e ST  (asks for "TN")
  valid: /\x1bP1\+r544e=(?:[0-9A-Fa-f]{2})+\x1b\\/i,
  refusal: /\x1bP0\+r\x1b\\/,
  malformed: /\x1bP[01]\+r/,
  expected: "complete XTGETTCAP status 1 for TN with even-length hex value and ST"
})
```

The termless path is `deviceReplyResult(spec, ctx.feedCapture(query), …)`: a
`1+r544e=<even hex>` value reply is **supported**; the bare `0+r` refusal is
**inconclusive** (`insufficient-evidence`, "Requested setting or name refused;
other settings or names unmeasured"); silence/`invalid-reply` stay inconclusive.
The terminal path writes the same query with a sentinel DA (the stock grade).
Ntilde deliberately refuses `TN` — its honest value is the session's TERM, which
VT does not know (exact-query-audit, #807/#861; `TerminalCapabilityTests`).
**The pinned verdict therefore stays inconclusive and is never upgraded**: no TN
value is invented and none is inferred from another capability, the application
name or child-TERM evidence. The stock baseline keeps `device.xtgettcap`
inconclusive.

## Effect contract (separately identified)

`ntilde-xtgettcap-capability-report-effect-v1` grades XTGETTCAP
**capability-report replies only** from raw fenced exchanges (exact request and
reply bytes retained; DA ordering barrier; independent reply oracle written from
the documented #844 contract, sharing no code with the parser):

- the exact pinned request `544e` → `DCS 0+r ST`;
- numeric replies `Co` (`436f`) and `colors` → `DCS 1+r<name>=323536 ST`;
- flag reply `RGB` (`524742`) (and `Tc`) → `DCS 1+r<name> ST`;
- one string reply (`Smulx`) → hex-encoded value, distinguishing string/number
  replies from flag replies;
- multi-name lists in exact order with per-name outcomes (`436f;544e;5463;436f`,
  the Q12 witness `544e;436f`);
- documented unavailable names: `TN`, `kcuu1`, the case-sensitivity refusal
  `co` (`636f`), the uppercase echo `436F`, and a controlled unknown
  (`not-a-capability`);
- bounded malformed requests: odd-length hex (`5`), non-hex (`zz`), empty
  name/list, mixed valid/invalid (`436f;5`) — each exactly one `DCS 0+r ST`;
- verified parser recovery: after each malformed request a valid request still
  answers exactly;
- every exchange must leave cells, cursor, rendition, margins, modes and the
  active screen unchanged (parent-journaled complete reads), and the fixture
  restores.

An advertised RGB flag or color count proves the reply only — never rendering,
color accuracy, or execution of advertised control strings (those are the
owning VT tests' scope). The settings-dependent `Ms` clipboard capability is out
of scope, as are X resources (`xtgetxres` is a different case), clipboard
permissions, remote delivery and other platforms.

## Machinery

Layered collector extension `ntilde-owned-xtgettcap-grading-v1` over the
title-stack extension (sibling of the pending OSC-title layer; restack if that
batch merges first), exposing the pinned termless and terminal callbacks. The
pinned termless code replays over the journaled raw reply; the terminal path
replays over the same journaled bytes and stays inconclusive. Receipt suffix
`includesXtgettcap` (schema 54 assuming the OSC-title layer takes 53), source
policy `readonly-owned-source-graph-v48` (inventory 305 → 313 files), suite
prefix `xgtc`, worker position after the title-stack suite. Version numbers are
re-read from main at PR time; merge serially and remeasure when integration
changes measured inputs.

Use the shared owned collector and desktop lock; measurement binds receipt to
scripts, source, binary, geometry and pane identity without asserting a
stronger binary/source link than the runner proves. Refusal tests cover wrong
requests, substituted names/values, wrong status, partial/malformed/missing/
extra/reordered replies, stale evidence, identity mismatch, state drift,
tampering and incomplete restoration.
