# R55 phase-limited Windows lifetime experiment

Diagnostic-only under public #152. Exact admitted app source 824147915e7fba498a9b94d9ec22075fe3dd082a, separately checked out from this PR's tree. Four independent fresh public Windows runner jobs, max parallel two. Historical full-product failure is context, not a randomized control.

| Case | UIA access | Launch |
|---|---|---|
| Idle | No UIA | Startup marker only |
| Root | Process-scoped native root query | Startup marker only |
| Descendants | Root plus one full-tree/semantic-ID read | Startup marker only |
| Descendants | Same query | Startup marker plus redirected streams |

All cases use process-bound startup logging and the exact pinned v2 collector. Eight-second warm launch, separate 20-second observation; record phase, liveness, exit, bounded counts and fixed categories, not provider content or raw exceptions. Distinct jobs reduce cross-case profile contamination but runner variance remains. No sign-in, input, credentials, private source, paid minutes or raw-log artifacts. Default signed-out application startup may navigate to its normal public page.

The raw FindAll traversal intentionally exercises the historical probe, not a recommended production algorithm. COM calls can block; step timeout is a diagnostic boundary, not a pass. Missing root with dead process is a process failure, not a runner limitation; live process without root/semantic peers is unqualified. No replacement or weakening of the existing full product UIA gate. A successful probe only shows observed survival.

Upstream leads, not causal proof: [Avalonia WebView issue list](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/issues), [WebView2 accessibility architecture](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/WebView2/WebView2-Accessibility.md), [Microsoft automation-peer exception guidance](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/custom-automation-peers). Distinguish initialization, provider traversal and dispatcher/lifetime failures. Do not suppress exceptions or infer root cause from a category or a green run.

## Completed first wave and next discriminating variable

Run [46137974017](https://github.com/SemperSupra/suprachat/actions/runs/46137974017), all four jobs completed successfully. Actual emitted v1 receipts inspected:

| Job | Case | Root elapsed ms | Raw descendants / named peers | Outcome |
|---|---|---:|---:|---|
| 140957430227 | Idle, startup only | n/a | n/a | alive 20234 ms |
| 140957430228 | Root, startup only | 4284 | n/a | alive 20240 ms |
| 140957430182 | Descendants, startup only | 3041 | 2456 / 592 | alive 20317 ms |
| 140957429982 | Descendants, redirected | 5818 | 2531 / 631 | alive 20332 ms |

All entered managed Main, no fatal marker, no process exit, cleanup true; none claimed product UIA acceptance. Same recorded EXE hash f125602cc79e23e1f3579d1008dcc441859b34efb6896b3f85a1ac52bed49979 is only an apphost hash, **not** proof of identical managed assemblies or dependency trees. The observed survival refutes an unconditional trigger by these phases, not a timing-sensitive interaction or shared cause. Fresh-runner variation and earlier full packaging/installer steps remain uncontrolled.

Do not blindly repeat four green cells. Next bounded matrix changes only the diagnostic probe: after raw descendants and ID enumeration, attempt the first real owned-control RootTabs lookup via bounded ControlViewWalker (exclude Chrome descendants), with startup-only and redirected launch. No Invoke/Select or provider input. Record separate managed assembly and apphost SHA-256, numeric observer HRESULT/inner HRESULT and first-owned lookup elapsed time; v2 schema. This tests one previously unexercised phase on the same source pin. It is not the full product qualification or a proven reproduction. Preserve the original run and #152 gate. If both cases survive, use the full admitted diagnostic workflow with stage/lifetime instrumentation rather than continuing blind standalone experiments.
