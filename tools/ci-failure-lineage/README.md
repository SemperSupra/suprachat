# R49 — Windows UIA host exit 70: cross-head failure evidence

This source-safe triage lane is independent of the R49 pure Go parallel-wave selector, R48 actor coworking, and R46/R47 human-commanded sessions.

Public evidence reviewed on 2026-11-22:

| Exact PR source | GitHub Actions run / job | Windows native UIA observation |
|---|---|---|
| PR #199 head `0c1e0da43e52022a03350dd0294ff8cb0cdaf7a6` | [44974466325](https://github.com/SemperSupra/suprachat/actions/runs/44974466325) / 137061134557 | `UIA_HOST_PROCESS_EXITED_BEFORE_SEMANTIC_PEERS`, exit 70 |
| PR #201 head `731740ee3e71687d3d8aeceebe31c1b3b28f38b4` | [44987324859](https://github.com/SemperSupra/suprachat/actions/runs/44987324859) / 137100698081 | Same terminal symptom |
| PR #200 head `4d0ea216187c29be5e04446cda8dfca31f975710` | [44987447989](https://github.com/SemperSupra/suprachat/actions/runs/44987447989) / 137101062956 | Same terminal symptom |

These were retrieved from GitHub public job logs. They establish a repeated **terminal signature** across distinct source heads, NOT a shared causal root. The installed Windows UIA stage detected a window before the host process exited with code 70 prior to its semantic peer verification. The root cause remains **UNKNOWN**, and the complete product qualification remains **FAILED/HELD** for these exact runs.

The standalone stdlib `classify_uia.py` reads only bounded, redacted run/job/head/marker observations. It rejects arbitrary log text, identity data, unrecognized source pins, conflicting exit codes, and fabricated success. It never executes a desktop, changes a repository, authorizes retries, merges PRs, or waives a CI check. Public CI tests only the synthetic critic, not an actual Windows UIA HIL.

**Independent future packets:** (1) First-party startup/exit-code provenance with safe redacted diagnostic receipts on a disposable Windows runner, (2) same exact WinApp bundle versus isolated UIA provider conditions, and (3) process-lifetime/crash/instrumentation and startup exit trace. Do not suppress failed semantic peers, skip mandatory test stages, or infer a passing baseline. Any actual repair requires same-head UIA peer tree, accessibility verification, complete build/install tests, and owner approval.

No secret data, browser/camera content, local user information, raw traces or private repo data belong in this public file.
