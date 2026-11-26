# R48 — Parallel execution way of working (human, automation, agent)

**Status:** durable engineering policy proposal. Public product/source/qualification: `SemperSupra/suprachat`. Private DLE/assignment authority: `SemperSupra/suprachat-private#2` and `worksets/active-parallel-workset.json` pointing to the latest bounded manifest (until its private PR is integrated, follow the exact linked branch/issue receipt). Current bounded mission: `SemperSupra/suprachat#195`; predecessor #19/#38/#39 and architecture #7. Actor Dispatch provides authorized execution, not a duplicate source of truth or session state.

## Default scheduling behavior

At **every iteration**, fetch live GitHub issue/PR/CI status, both `AGENTS.md` contracts, and the private DLE workset. Construct a *dependency DAG* of bounded work packages. Select a **maximal safe antichain** of at least two independently READY packages if possible, rather than just the highest READY package. Give priority to independent critical-path work, failure-domain isolation and throughput per iteration; do not conflate "two GHA matrix jobs" with two independent engineering packages.

An independent package has:
- one repo, issue/owner, a unique branch or isolated worktree, and disjoint mutable file-tree ownership;
- exact task class, version/commit/input fixtures, changed-path allowlist and 1–3 concrete acceptance oracles;
- explicit actor and capability envelope, tools and cost/authority limits;
- negative tests, evidence and rollback/retirement policy; no ambient credentials;
- its own receipt, branch/head, PR and CI run URL, plus a DLE continuation baton.

Use two to four independently owned lanes per iteration; `max-parallel: 2` for any single public hosted-GHA workflow. If local actor capacity or free-tier quota is exhausted, continue source review, bounded offline fixtures and other public-safe lanes. Do **not** claim that starting a matrix or opening a PR is execution/qualification. Do not relaunch failed/stale work without reconciling its exact branch/head first. No private GHA minutes; no paid API; no recursion until authorized.

The controller/actor must maintain three explicit sets: **READY**, **ACTIVE**, and **HELD/BLOCKED**. READY tasks may run as a batch only when they have no outstanding dependencies, overlapping write ownership, common non-shareable physical resource, or unapproved privilege boundary. Recompute the antichain at each reconciliation. Tasks behind a gate remain HELD while unrelated lanes continue. If only one task is runnable, record the proof of why the width is one and what unblocks the next parallel lane; do not simulate concurrency.

## Sequential critical path, explicit gates

1. Read current DLE and validate authority, ownership and exact immutable baselines; human session owner may preempt at any time.
2. Where parallelizable: separate code contracts, adversarial tests, docs/source review, OS/platform adapters, independent protocol qualification, accessibility and UX.
3. **Gate** shared semantics/contract compatibility before integration; no races in common files.
4. **Gate** externally authenticated session-owner admission and durable W9 control epoch before a real tool action; source-policy CI does not qualify actual human preemption.
5. **Gate** single-writer integration and exact-head CI/independent review before applying shared state; never concurrently mutate the same ledger or guarded durable event.
6. **Gate** real authorized HIL, W8/W9 identity/revocation, ownership-safe cancellation, cross-device session continuity and three-audience same-task parity before release.
7. **Gate** merge/release/deletion/credential/paid resource operations on explicit owner authority as specified in the current private workset. Avoid deleting branches/failed receipts solely to make a clean board.
8. Reconcile GitHub issues, PR reviews, CI, DLE statuses, evidence links, exact owner decisions and next batons *for the whole batch* before new work.

At any human instruction to pause/change intent, admit a separately authenticated human-owner control action via the actual trusted session owner; fence affected work, preserve unrelated safe lanes, and reconcile. Never interpret a prompt, HID event or UI click as authentication. Distinguish intervention **requested**, worker **quiesced**, and external effect **verified stopped**.

## Small-model actor assignment contract

The local actor must not infer new architecture or implement a sprawling plan. It should receive each tiny job as a schema-bounded record:
`{task_id, authoritative_issue, repo, exact_base_sha, allowed_paths, objective, allowed_tools, oracle_command, negative_test, max_cost, expected_receipt, blocked_by}`.
Include one negative test and one deterministic PASS oracle. Scope at most one subsystem per task. An actor may propose the next dependency, but may not silently expand its own authority. A failed run returns typed `BLOCKED`, `FAIL`, or `EVIDENCE_INCOMPLETE`, with minimal proof and next action, never a fabricated success. Humans should not shuttle batons between agents; link GitHub DLE.

## Three audiences and cross-device invariant

Every implementation preserves human UX (including keyboard/screen-reader and available HID modalities), machine automation JSON/CLI, and typed agent JSON-RPC or qualified protocol. All attach to the **same server-owned session, context and history**, with different device-aware presentations. The designated authenticated human session owner commands guidance/pause/stop/resume; automation and agents cannot self-approve. A per-session control epoch fences stale effects; actual stop requires independent evidence. Avoid maintained forks, new independent session registries, and model-dependent hidden state.

## Verification and local startup

The public offline critic `tools/qualification/validate_parallel_workset.py` validates the workset contract, dependency DAG, path disjointness, role parity, human preemption and zero-paid/free-tier guardrails. Public fixture is *synthetic only*; never upload the private manifest or evidence into public CI. The private workset may be checked **locally** with the public critic after both trusted repositories are available.

The existing PS5.1-safe `tools/local/Enter-SupraChatCowork.ps1 -Mode Apply` refreshes the sibling repositories and emits `SUPRACHAT-LOCAL-ACTOR.txt`; it does not reset dirty worktrees, copy secrets or grant new authority. The human's cold-start message can be simply: **"Resume SupraChat from the DLE; run independent READY batches under AGENTS.md."** All detailed instructions belong in this policy, the workset, and the issue receipts.

From the public checkout, set `$approvedPrivateRevision` to the full SHA explicitly approved in private issue #2. Supply the workspace root containing the private sibling checkout. A proposal branch alone is insufficient.

```powershell
$selection = ./tools/local/Enter-SupraChatCowork.ps1 -Mode ResolveWorkset `
    -Root (Split-Path -Parent (Get-Location).Path) -ApprovedPrivateRevision $approvedPrivateRevision |
    ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $selection.Status -ne 'VERIFIED_PRIVATE_PARALLEL_WORKSET') {
    throw $selection.Status
}
$selection.Path
```

This command verifies and prints the path for local review. Card generation requires a separately available critic. Use a separate checkout if the current private checkout is at a different revision.

### Fail-closed current-workset selection

The local bootstrap reads `worksets/active-parallel-workset.json` from the checked-out **private** DLE. Without an approved revision or a committed pointer, the baton states `UNRESOLVED_FROM_PRIVATE_DLE`. Malformed, stale, missing-target, and candidate-only inputs return distinct refusal statuses. Any refusal blocks card emission. Never fall back silently to the historically valid R48 workset: it can contain superseded assignments and already completed tasks. First reconcile the latest branch/owner decision and only then run the read-only task-card critic. Historical R48/R49 manifests remain evidence; only the verified pointer selects the current wave.
