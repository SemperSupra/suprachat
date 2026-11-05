# Actual native UI Automation apartment differential

**Scope:** An additional read-only cloud diagnostic for issue #152. This is not a repair, release qualification replacement, or permission to change protected native source.

The workflow builds one unmodified SupraChat.exe on a disposable Windows 2025 public GitHub Actions runner and observes its UI Automation provider with three client configurations: PowerShell 7 default apartment, Windows PowerShell 5.1 STA, and PowerShell 7 STA. It repeats the default PS7 observation at the end to help distinguish client-apartment effects from ordinary startup time.

The probes inspect only root identity validity, FindAll and ControlViewWalker descendant counts and whether the fixed AutomationId Tab.Sessions exists. They never read UI names, values, browser text, cookies, secrets, credentials, conversation data or user-specific host information, and never invoke a control.

The diagnostic accepts partial or missing target observations as **measurements**, not as an accessibility PASS; it requires a real native process/root and a fixed-shape metadata receipt. The original full-product Windows accessibility smoke remains unchanged and is still mandatory before release.

The result discriminates:
- default PS7 (typically MTA) versus explicitly STA client;
- PS5.1 STA versus PS7 STA implementation differences;
- FindAll versus ControlViewWalker visibility on the same process;
- startup/time drift using repeated default PS7 probes.

Do not infer that COM apartment is the cause until direct Windows evidence supports it; investigate provider lifecycle, root identity and timing as competing hypotheses. No private GitHub Actions minutes, local actor/worktree, authenticated account, or production site access are needed.

Evidence is a small public-safe JSON summary from the runner with exact source SHA and artifact identity provided by GitHub Actions. The native owner issue #152 remains the authoritative corrective workset.
