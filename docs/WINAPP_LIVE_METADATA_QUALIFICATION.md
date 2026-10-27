# Opt-in Windows metadata lifecycle qualification with WinApp CLI

Microsoft WinApp CLI provides an independent UI Automation client for the native metadata review.
The local qualification harness uses the Microsoft-signed portable v0.7.1 release. Its UIA pattern
commands operate without the separate Computer Use helper. This harness is never run by public CI
against a first-party profile; only its aggregate validator's synthetic self-test belongs in CI.

## Scope and execution

Run only with explicit operator authorization for the current first-party metadata experiment.
Start the reviewed app with `--first-party-metadata-hil --open-metadata-review` and an absolute
`--metadata-summary-directory=...`. Those flags open an OFF review; the test harness performs the
fresh acknowledgement, preparation and confirmation for each authorized case. No typed phrase or
file picker is used. Normal browser authentication remains authoritative; the harness does not
automate authentication dialogs, extract credentials, submit prompts or request provider actions.

Use `tools/local/Test-SupraChatWinAppLifecycle.ps1 -FirstPartyMetadataHil` with explicit `-WinApp`,
`-SummaryDirectory`, `-Case`, `-TargetProcessId`, `-MainWindow`, optional `-ReviewWindow`, and the
reviewed product `-AssemblyHash`. Paths and window references come from the current local run,
not a checked-in example containing operator identifiers. The default receipt directory is the
configured summary directory; `-ReceiptDirectory` can select another absolute local directory.
The script fails before UI actions without the HIL opt-in switch. Existing files are not overwritten.

Cases: `Expiry`, `Stop`, `Navigation`, `ReviewClose`, `OwnerClose`. Expiry uses monotonic time,
requires active capture at approximately115 seconds and stopped/cleaned capture after125 seconds.
Navigation uses the application's Reload control. Review close requires that review to disappear
while its owner remains. Owner close requires the original process to exit. Each case validates one
fresh aggregate against the reviewed assembly hash, expected first reason and OFF/cleanup flags.
On an incomplete test the harness attempts Stop only after admitting the exact review process/window;
it retains a failure receipt and never blindly retries an uncertain action.

## Version and targeting lessons

The main-branch WinApp documentation may describe commands absent from the released binary.
Version0.7.1 supports scoped `get-property` but not scoped `invoke`. The harness discovers the
native title-bar Close control from a shallow inspection of the exact returned HWND and passes
its current semantic slug to `invoke --action invoke`. Owned windows can otherwise make a main
window's Close lookup ambiguous. Ambiguity is an error, not permission to act on the first match.
The exact HWND, declared root ID and unique native title-bar button must all agree.

## Evidence limits

Local lifecycle receipts join independent UIA/process observations with app-reported aggregate
cleanup. The validator alone cannot prove runtime state or sign-in; its receipt keeps those claims
false/unverified. A separately captured profile-menu/no-Log-in UI indicator is evidence of UI state,
not verification of account identity, human identity or backend authorization. Completeness stays
UNKNOWN; zero incidents does not prove complete traffic delivery. Idle Stop/close cases can contain
zero network events and must retain NO_FIRST_PARTY_OBSERVATIONS. Forced kill/OS shutdown and actual
provider termination remain separate from orderly closure and local observer cleanup.

Keep real aggregate files, UI observations and validation receipts local. Public CI must not upload
profiles, event arrays, account identifiers, private paths, cookies, headers, prompts or responses.
CLI telemetry is opted out for test commands. The tool does not install WinApp, change Windows
settings, widen custody or authorize merges/releases.

References: [Microsoft WinApp UI automation](https://github.com/microsoft/winappCli/blob/main/docs/ui-automation.md),
[v0.7.1 release](https://github.com/microsoft/winappCli/releases/tag/v0.7.1).
