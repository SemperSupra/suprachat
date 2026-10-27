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

**Lifetime:** this is currently a **volatile per-review readback**, not a persisted timeline. The pre-existing aggregate summary continues to be saved locally by the original owner flow. Its validation and storage schema are intentionally unchanged in this candidate. A later independently reviewed, backwards-compatible summary extension would be required to durably preserve individual lifecycle entries.

## Qualification and evidence

Public/free tests use a disposable synthetic loopback WebView; verify control identity, `IsReadOnly`, bounded and deduplicated entries, Stop/expiry state, and absence of prohibited content. Local authorized WinApp CLI v0.7.1 can independently `inspect` and `get-value` on a precisely identified HWND when native HIL is available. These tests do not authorize first-party ChatGPT capture, credential exchange, replay, or traffic modification.

Never infer that UIA readback proves upstream provider effects, signed-in identity, event completeness, or that final local aggregate storage succeeded. Retain the independent postcondition/receipt path and the existing error vocabulary. Preserve old session DLE and owner release gates.

References: [WinApp/UIA findings draft](https://github.com/SemperSupra/engineering-governance-private/pull/219), [SupraChat compatibility review](https://github.com/SemperSupra/suprachat/pull/80), [WinApp CLI existing harness](https://github.com/SemperSupra/suprachat/pull/79).
