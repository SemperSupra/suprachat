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
