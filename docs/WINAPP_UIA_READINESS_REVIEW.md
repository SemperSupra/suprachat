# SupraChat native Windows UIA / WinApp CLI readiness review

Review date: **2026-10-27**  
Status: **review candidate — design/read-only source findings, not an acceptance or release verdict**  
Source: public `main` as inspected on this date, plus explicitly **unmerged** draft [#79](https://github.com/SemperSupra/suprachat/pull/79) (and its stacked source).  
Owner: [#19](https://github.com/SemperSupra/suprachat/issues/19) human/automation/agent UI/UX/DX and [#51](https://github.com/SemperSupra/suprachat/issues/51) native metadata lifecycle; no new orchestration or session authority.

## What WinApp CLI should and should not be used for

Use the pinned Microsoft WinApp CLI as an **independent Windows UIA client** to validate native controls, provider patterns, focus, dialogs, and sanitized postconditions. SupraChat's semantic core, CLI, and agent JSON-RPC remain the canonical automation/agent interfaces. Never make agent operation depend on pixel targeting, GUI scraping, a second browser profile, an undocumented ChatGPT endpoint, or extraction of browser/session/credential data.

WinApp CLI's app UIA tree and the embedded ChatGPT WebView's accessible content are separate surfaces; the first-party webpage's DOM and service are not SupraChat-owned APIs. Explicit user opt-in still governs first-party experiments.

## Source inventory: existing strengths

| Surface | Existing evidence | Qualification boundary |
|---|---|---|
| Main app shell | `SupraChat/MainWindow.axaml` declares `SupraChat.MainWindow`, `RootTabs`, `Tab.ChatGPT`, `Tab.AgentLab`, `Tab.AutomationAgents` | Main/root/tab accessibility metadata exists in source; must verify actual external UIA selection and focus |
| Native app controls | Nav `Nav.Back/Forward/Reload/Home/OpenExternal`; Auth; AgentLab; Responses; Codex; Browser; RemoteControl; accessibility actions have semantic AutomationIds | Declared ID is not runtime proof of expected Invoke/Toggle/Value/Selection pattern |
| Accessible state | `Auth.Status`, `Auth.PortableStatus`, `Accessibility.Status`, machine/diagnostics statuses have names, textual states, and live settings where applicable | Runtime UIA readback/announcements and busy->ready transitions need causal tests |
| Keyboard + accessibility | `ACCESSIBILITY.md`, `SupraChat/Core/AccessibilityContract.cs`: shortcuts, zoom, reduced motion, cross-audience semantic contracts | Static descriptor booleans are expectations, not external Windows provider proof |
| Passphrase | `SupraChat/SecurePassphraseTextBox.cs` custom peer deliberately withholds `IValueProvider` and suppresses Text-property notifications; existing [#15](https://github.com/SemperSupra/suprachat/issues/15)/[#17](https://github.com/SemperSupra/suprachat/issues/17) record masked-input UIA leakage remediation | Independently re-probe every relevant external UIA read path with synthetic secret; do not infer `IsPassword=true` from the fix |
| Native metadata review | Draft [#79](https://github.com/SemperSupra/suprachat/pull/79) adds opt-in pinned v0.7.1 WinApp lifecycle harness, typed scalar output parsing and synthetic validator | Draft/stacked/unmerged; local live tests and CI evidence must remain separately classified and attached to their owning workset |
| Multi-session workbench | [#19](https://github.com/SemperSupra/suprachat/issues/19) and review-only W1/W6/W16 candidates | Do not claim current `main` implements the entire sessions UI merely because draft code or issue requirements exist |

## Prioritized gaps / improvements (proposals, not runtime-failure claims)

### P0 — Close the missing native app-owned lifecycle evidence projection (#51, #79 continuation)

The native metadata review window in the stacked candidate uses stable IDs for `Native.Metadata.Acknowledge/Prepare/Confirm/Stop/Save/Status/Message/Readiness`. Its read-only Status textbox emits a serialized diagnostic state; the independent WinApp harness parses a tightly allowlisted scalar projection. The private DLE identifies a further desired app-owned lifecycle-history surface.

**Recommendation:** retain the secure full-status path only within permitted scope and expose a separate **sanitized, read-only, human-legible history/status** projection of lifecycle transitions. Fields should be stable enum state, monotonic time/age when useful, build/version, operation class, first ending reason, cleanup and save outcomes, completeness marker, and explicit unknowns — no session/actor/account identifiers, URLs, event payloads, transcript, headers or tokens. Align with existing `suprachat-trace/v1` metadata authority rather than creating a second ledger. The app owns this evidence; WinApp should only **observe it independently**, never supply the application's truth.

Acceptance: UIA control can be identified by stable ID and read without machine parsing a full raw trace; synthetic OFF -> review -> active -> Stop/expiry/close states match the authoritative app receipt; closed/expired value cannot appear active; no incomplete state is coerced into success. Do not broaden first-party capture.

### P1 — Turn XAML identity assertions into real UIA semantic action tests (#19)

`SupraChat.ContractTests/Program.cs` checks for required IDs and accessible descriptor invariants. Existing Windows UIA smoke probes can find the root and selected controls but have had historical transient attach/lookup failures. These checks do **not** independently establish every control's provider pattern or postcondition.

Use a **small synthetic native Windows fixture** for:
- `Tab.AgentLab` SelectionItem.Select, then verify selected tab, focus and `Auth.SignIn` identification without executing authentication.
- `Accessibility.ScaleUp/ScaleReset` Invoke and readback via `Accessibility.Status`, plus CLI/agent equivalent preference state. Use no sensitive content.
- `AgentLab.WebSearch` Toggle (synthetic/local state only) and check actual state; don't blindly repeat Toggle on uncertain response.
- `Nav.Reload` on a disposable synthetic local WebView, not signed-in ChatGPT, with explicit native owner/child HWND scope.
- `Native.Metadata.Acknowledge` / `Prepare` / `Confirm` eligibility in the separate local synthetic metadata fixture, with negative/off/refusal assertions.
- Unavailable/disabled control, duplicate IDs scoped to a root, stale element recreation, modal owner mismatch, and wrong foreground; verify fail-closed reasons.

Pin WinApp CLI v0.7.1 for this harness. Its released `invoke` does **not** accept all the `--root`/`--type` qualifiers shown by the development-branch docs; resolve exact owning HWND and fresh unique control, then use supported syntax. Avoid version-blind `--help` assumptions. Use native WinApp commands only where those primitives are available, not for domain authority.

### P1 — Scope and identity for dynamic session controls (#19)

Static `RootTabs` identifiers are useful; repeated session-tab controls and terminal panes (when introduced/promoted from draft) need stable identity **within the parent session/workbench**. Generate structured IDs from stable nonsecret internal session tokens only if safe for public accessibility exposure, or use appropriately scoped anonymous child IDs plus independent session selection. Never expose sensitive account/workspace IDs, private paths, window content, or secrets as AutomationIds or names.

For each generated tab/pane ensure correct accessible Name, selected/running/waiting state, keyboard navigation, UIA Selection/ExpandCollapse availability, deterministic disposal/recreate behavior, and click-equivalent semantic core API. A tab's mutable visible title must not be the sole selector.

### P1 — Secret and data-leak negative tests

Maintain the custom passphrase peer fix and add an independent Windows client test for Name/Help/Value/Text/current property/notification leakage on a synthetic sentinel. Include sensitive-looking transcript, raw Responses body/events, terminal output, pairing-code, file-path and diagnostic textboxes in a **readability classification**: intentionally readable user content must be qualified as sensitive and **not harvested in generic WinApp logs or public CI**. Password/pairing controls require their own scoped decisions; do not globally disable UIA accessibility.

Use separate explicit human/local authorizations for reading real content. No browser sign-in automation, tokens, real credentials, or real first-party prompts in GitHub artifacts.

### P2 — Localization, modal identity and WebView boundary

Run EN/DE/TH name/localization perturbations while IDs remain stable; test Unicode/IME, scaling/DPI, high-contrast, reduced motion, keyboard-only and screen-reader behavior. Test native owner/window selection with multiple simultaneous windows and modal dialogs; never target a Close control on a process name alone. Treat first-party WebView contents as opaque unless the specific test has separate authority; do not promote the presence of the WebView host ID into a claim of product DOM access.

## Minimal acceptance evidence

- **PUBLIC STATIC/SYNTHETIC**: build + tests on 3 OS; independent ID uniqueness/labels/security peer assertions; syntax-parsing, schema, negative/refusal checks; no live first-party automation on public runners.
- **NATIVE WINDOWS UIA**: pinned binary and app artifact; exact PID/HWND/owner and build identity; inspect/act/readback on disposable app fixture; independent semantic state proof, negative ambiguity/staleness, localized controls. Classify as runtime tests, distinct from static XML checks.
- **AUTHORIZED LOCAL FIRST-PARTY HIL**: only explicitly bounded #51 lifecycle; receipt contains sanitized scalar outcomes, initial/end state, and `UNKNOWN` completeness/provider-effect flags; real artifacts local. Never reinterpret an app summary as an independent service/provider verification.
- **OWNER PROMOTION**: review tests/first failures, UI/UX/DX parity and existing workset authority before merge/release. This document grants none.

## Existing hooks and source references

- [ACCESSIBILITY.md](../ACCESSIBILITY.md) — current product accessibility contract.
- [MainWindow.axaml](../SupraChat/MainWindow.axaml) — native semantic controls.
- [SecurePassphraseTextBox.cs](../SupraChat/SecurePassphraseTextBox.cs) — secret-bearing peer behavior.
- [AccessibilityContract.cs](../SupraChat/Core/AccessibilityContract.cs) — assertions vs runtime evidence.
- [ContractTests/Program.cs](../SupraChat.ContractTests/Program.cs) — current static invariants.
- [WinApp lifecycle draft #79](https://github.com/SemperSupra/suprachat/pull/79), [#19](https://github.com/SemperSupra/suprachat/issues/19), [#51](https://github.com/SemperSupra/suprachat/issues/51).
- Microsoft official UIA docs: https://github.com/microsoft/winappCli/blob/main/docs/ui-automation.md ; released CLI v0.7.1: https://github.com/microsoft/winappCli/releases/tag/v0.7.1.

No behavior/source modification, merge, live test, first-party profile read, or implementation approval is implied by this review.
