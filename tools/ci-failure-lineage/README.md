# R49 — Windows UIA host exit 70: cross-head failure evidence

This source-safe triage lane is independent of the R49 pure Go parallel-wave selector, R48 actor coworking, and R46/R47 human-commanded sessions.

Public evidence reviewed on 2026-11-22 and reconciled on 2026-11-28:

| Exact PR source | GitHub Actions run / job | Windows native UIA observation |
|---|---|---|
| PR #199 head `0c1e0da43e52022a03350dd0294ff8cb0cdaf7a6` | [44974466325](https://github.com/SemperSupra/suprachat/actions/runs/44974466325) / 137061134557 | `UIA_HOST_PROCESS_EXITED_BEFORE_SEMANTIC_PEERS`, exit 70 |
| PR #201 head `731740ee3e71687d3d8aeceebe31c1b3b28f38b4` | [44987324859](https://github.com/SemperSupra/suprachat/actions/runs/44987324859) / 137100698081 | Same terminal symptom |
| PR #200 head `4d0ea216187c29be5e04446cda8dfca31f975710` | [44987447989](https://github.com/SemperSupra/suprachat/actions/runs/44987447989) / 137101062956 | Same terminal symptom |
| PR #202 head `2b511e1f972ad2d4961c0809dd0404f5f145e202` | [44988987711](https://github.com/SemperSupra/suprachat/actions/runs/44988987711/job/137105743861) / 137105743861 | Root observed, exit 70 before semantic peers |
| PR #204 head `312df56db5ee0675bfbdff751789926b08a79d9e` | [45101928196](https://github.com/SemperSupra/suprachat/actions/runs/45101928196/job/137454604770) / 137454604770 | Root observed, exit 70 before semantic peers |
| PR #204 head `e745b2fd322ed214b482b52436aeed08d08f439d` | [46129109656](https://github.com/SemperSupra/suprachat/actions/runs/46129109656/job/140930171205) / 140930171205 | Root observed, exit 70 before semantic peers |
| PR #204 head `a91ddf11edc9ecf2d4d9228102f5c83f0c71ea62` | [46135854410](https://github.com/SemperSupra/suprachat/actions/runs/46135854410/job/140950878700) / 140950878700 | Root observed, exit 70 before semantic peers |

These were retrieved from GitHub public job logs. They establish a repeated **terminal signature** across distinct source heads, NOT a shared causal root. The installed Windows UIA stage detected a window before the host process exited with code 70 prior to its semantic peer verification. The root cause remains **UNKNOWN**, and the complete product qualification remains **FAILED/HELD** for these exact runs.

The standalone stdlib `classify_uia.py` reads only bounded, redacted run/job/head/marker observations. It rejects arbitrary log text, identity data, malformed source pins, duplicate run/job identities, duplicate JSON keys, conflicting exit codes, and fabricated success. It is a structural consistency critic: a syntactically valid SHA or tuple is not independent verification of GitHub provenance. Source bindings and job observations must be independently checked before adding them. The original fixture filename is retained for compatibility, although it now includes seven distinct failing source heads, including three revisions of PR #204; these are not seven independent causal experiments. It never executes a desktop, changes a repository, authorizes retries, merges PRs, or waives a CI check. Public CI tests only the synthetic critic, not an actual Windows UIA HIL.

**Independent future packets:** (1) First-party startup/exit-code provenance with safe redacted diagnostic receipts on a disposable Windows runner, (2) same exact WinApp bundle versus isolated UIA provider conditions, and (3) process-lifetime/crash/instrumentation and startup exit trace. Do not suppress failed semantic peers, skip mandatory test stages, or infer a passing baseline. Any actual repair requires same-head UIA peer tree, accessibility verification, complete build/install tests, and owner approval.

No secret data, browser/camera content, local user information, raw traces or private repo data belong in this public file.

## Positive observations are not causal closure

The failure-only fixture deliberately does not combine successes with failures. Later independent Windows smoke passes include #199 at `8f8189462a55a2f2d2da6ca827131186e2d8eef3` ([46131285877](https://github.com/SemperSupra/suprachat/actions/runs/46131285877)), #205 at `834d58c477bde3eef0ddb6150cddc47d8776648d` ([46129188857](https://github.com/SemperSupra/suprachat/actions/runs/46129188857)), #207 at `e5671e354c2fcc91575d26f135d5993f9714d6ae` ([46132977396](https://github.com/SemperSupra/suprachat/actions/runs/46132977396)), and #208 at `b846715d8dc986eb7664006450cd99ed1ee3cd12` ([46133284987](https://github.com/SemperSupra/suprachat/actions/runs/46133284987)). These are different head/run observations, not evidence that the historical failure was repaired. The #205 v2 diagnostic can produce bounded process-specific exception-category observations; category alone does not establish cause. See #152 for the protected qualification gate. Do not infer failure probability from this selected evidence set.

The later #204 `02289c93eb1800bf05f75eeee1ceea9c07e7977a` full run [46138010629](https://github.com/SemperSupra/suprachat/actions/runs/46138010629) and #203 `177e016e379f2d299ba90d81cd7de5d2e7758c17` full run [46137973956](https://github.com/SemperSupra/suprachat/actions/runs/46137973956) also have actual live UIA smoke PASS. The four standalone phase observations are in CAUSAL_PHASES.md; causal status remains UNKNOWN.
