# R49 — Read-only selection of independent READY batches

A pure Go 1.23 standard-library command for picking the **maximum number of compatible READY work packets** from a privately maintained, R48-compatible workset. It operates entirely on the local input file. It cannot schedule a GitHub Action, call an agent, merge a PR, open a shell command from a task card, or change credential/host/session state.

## Local use

From a trusted local workspace where the public `suprachat/` and private `suprachat-private/` checkouts are sibling directories:

```powershell
python tools/qualification/validate_parallel_workset.py ../suprachat-private/worksets/parallel-batches-r48.json
go run ./tools/parallel-wave-selector -manifest ../suprachat-private/worksets/parallel-batches-r48.json -public-slots 2 -local-slots 0
```

The first command is the **separate R48 fail-closed schema/authority critic**; do not skip it. The second selects a deterministic maximal safe wave with an admitted concurrency limit and returns task cards, ready/deferred/held IDs, and explicit zero-authority flags. `-local-slots 0` is the intended behavior when the local actor is out of quota. The manifest is private; **never upload the private file, credentials, raw workcell information, or actor context to public GHA.** Only in-memory synthetic test data is used in public CI.

R49 does not introduce a new scheduler or bypass the existing Agent Dispatch governor. Disjoint path/branch checks are necessary but not sufficient for independence: the controller must also validate shared hardware, physical resources, pending owner action, W8 authentication, W9 durable epoch, and external tool effects. It does not claim to prove a real task can execute or that a verified human command has stopped a worker.

## Deterministic policy

- Inspect all bounded READY tasks with immutable bases, source branch, exact oracle, negative case and receipts.
- Reject malformed/cost-bearing/authority-widening input before planning.
- Choose maximum **cardinality** under `max_concurrent`, public-GHA slots and admitted local capacity.
- Never choose two tasks with overlapping write directory roots or branches in the same repository.
- Stable tie break is lexicographic task ID, independent of input ordering.
- Report deferred and blocked work rather than silently discarding it.
- Distinguish READ-only planning from actual concurrent execution.
- Owner-controlled integration remains sequential and requires exact CI, independent review and DLE evidence.

The user is the highest-priority authorized human session owner, but a task card or user interface cannot independently authenticate a human or grant a tool capability. Pause/stop remains a real external server/worker HIL gate.
