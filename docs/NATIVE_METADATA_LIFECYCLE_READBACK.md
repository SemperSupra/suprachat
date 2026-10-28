# Native metadata lifecycle UI Automation readback

Status: experimental candidate stacked on [draft #79](https://github.com/SemperSupra/suprachat/pull/79) for [#51](https://github.com/SemperSupra/suprachat/issues/51). Non-production, non-authoritative human/UIA projection; not a new session ledger, consent mechanism, broker or capture entitlement.

## Why

The existing `Native.Metadata.Status` control exposes a large serialized native status object with session identity. An external automation client can interpret a sanitized slice, but application-authored, explicitly bounded readback is easier for users, assistive technology, WinApp CLI and WinInspect to inspect without handling the raw status object.

## Interface

- `Native.Metadata.LifecycleReadback`: named, read-only Avalonia TextBox in the native metadata review window, exposing ordinary Windows UIA value semantics when supported by the actual provider.
- Value schema: `suprachat-native-lifecycle-readback/v1`.
- Fields: `schema`, `build_version`, `completeness=UNKNOWN`, `is_authoritative_for_consent=false`, `events`.
- Entries are bounded to the newest **24** app-local observations/actions and contain: elapsed monotonic milliseconds from review creation; closed-vocabulary action; normalized state/end reason; activity, observer, cleanup, enable and disable booleans.
- Raw event payloads, account/user/session IDs, process paths, remote URLs, document text, cookies, headers, OAuth and passwords are deliberately excluded.
- Polling unchanged state must not append duplicate `Observed` entries. Closed/unknown state and unproved cleanup must not be interpreted as success.
- `build_version` alone does not prove an exact artifact identity; existing local summary `assembly_sha256` is the authoritative reported binary hash.

**Lifetime:** the UIA control itself is volatile while the native review window is open. In the separately stacked [draft #83](https://github.com/SemperSupra/suprachat/pull/83), the same bounded, sanitized history is now included **inside the one existing locally saved aggregate** (not in a new file or ledger). New first-party summaries have schema `suprachat-first-party-metadata-hil-summary/v3`; new disposable synthetic summaries have `suprachat-synthetic-native-metadata-summary/v2`. Older v2/v1 aggregates remain readable by their original contracts; a summary captures history **as of the successful save**, so an explicit Save followed by review-window closure does not retroactively rewrite it. Close-triggered saves include an actual `Closed` transition. No separate post-close history file is written, no raw events or secrets are exported, and a failed save is never considered durable evidence.

**Compatibility qualification:** the strict first-party validator accepts older v2 snapshots and requires the bounded readback field and matching first lease end reason in new v3 snapshots; synthetic native tests validate actual saved v2 files. The exact saved file remains the evidence anchor and its source bytes are hashed by the existing independent validation harness. New schema admission remains a draft until native and contract CI complete.

## Qualification and evidence

Public/free tests use a disposable synthetic loopback WebView; verify control identity, `IsReadOnly`, bounded and deduplicated entries, Stop/expiry state, and absence of prohibited content. Local authorized WinApp CLI v0.7.1 can independently `inspect` and `get-value` on a precisely identified HWND when native HIL is available. These tests do not authorize first-party ChatGPT capture, credential exchange, replay, or traffic modification.

Never infer that UIA readback proves upstream provider effects, signed-in identity, event completeness, or that final local aggregate storage succeeded. Retain the independent postcondition/receipt path and the existing error vocabulary. Preserve old session DLE and owner release gates.

References: [WinApp/UIA findings draft](https://github.com/SemperSupra/engineering-governance-private/pull/219), [SupraChat compatibility review](https://github.com/SemperSupra/suprachat/pull/80), [WinApp CLI existing harness](https://github.com/SemperSupra/suprachat/pull/79).
