# W11 — single-authority storage reconciliation and hardening decision

**Scope:** public synthetic SupraChat #19/#38 research, not production admission.
**Chosen integration baseline:** W6 SharedSessionLedger + W9 transactional checkpoint
+ W10 read-only accessible observer, stacked #37 -> #42 -> #45 -> #47 -> W11.
No branches are merged and no real ChatGPT provider may be invoked.

## Evidence comparison — competing W7 approaches

| Criterion | Existing W6 + W9/W10 (#45/#47) | Direct managed SQL store (#46/#48) |
|---|---|---|
| Authoritative mutation core | Same W6 atomic in-process ledger across ephemeral and opt-in durable modes | Alternate `ManagedDurableLedger` mutation implementation in opted-in host |
| Mutation atomicity | W6 monitor + WAL FULL checkpoint before first ACK | SQL transaction over rows/outcomes/events |
| Event/receipt recovery | Complete bounded snapshot of event history, session state and immutable outcomes | Normalized sessions/outcomes/events |
| Independent 3-OS tests | Real multiple clients, racing CAS, pre/postcommit lost ACK, desktop inventory and Windows UIA | SQL first outcomes and separate real host restart |
| One authoritative session model | W6 implementation reused (W9 is backing store, not second in-memory registry) | More than one mutation implementation to preserve/qualify |
| Per-write cost | O(total bounded checkpoint), with 2MB cap | Incremental SQL rows, preferable at scale |
| Storage admission before W11 | Existing directory, implicit initial DB creation; weak custody | Explicit owned temp directory, file-exists mode and opt-in |
| GUI integration | Read-only observer against exact same durable W6 authority | No composed GUI source or UIA |
| Production actor identity | No | No |
| Physical power loss / tamper resistance | Not established | Not established |

**Decision:** Advance the W9/W10 *composition* as a bounded short-term
integration candidate, not as an everlasting canonical on-disk format.
Its already-qualified W6 mutation semantics and accessible GUI integration
avoid a second source of session authority. Reuse the better explicit owner
admission and patched dependency precedent from #46. Retain #46/#48 as a
valuable alternative for possible *future* incremental-store replacement if
checkpoint latency/capacity becomes a measured problem, not a third live
host variant. Formal reviewer/owner selection for production still pending.

## W11 corrections and qualification assertions

1. Opt-in `SUPRACHAT_W9_DISPOSABLE_ONLY=yes` before any SQLite access;
   path must be absolute `.sqlite` inside an already existing named
   `suprachat-w9-owned-*` directory. Reject symlink DB/parent/owner lock
   and POSIX parent directory permissions open to group/others. Windows ACL
   and hard-link custody remain independent unqualified OS-security gates.
2. `SUPRACHAT_W9_CREATE_ONCE=yes` only on first deliberate owner-side
   synthetic initialization. Atomic `FileMode.CreateNew` creates a fresh
   SQLite file, and one SQLite transaction commits a genesis checkpoint
   before the host can listen. Any subsequent start MUST open an existing
   file without create mode; missing original database fails closed,
   including if an old sidecar/lock file still exists.
3. An existing SQLite store missing an expected `checkpoint(slot=1)`
   row is refused as unknown/corrupt—never reconstructed empty. Every
   mutation still passes the existing W6 gate and SQLite FULL WAL before
   ACK, including cached rejections.
4. Align `Microsoft.Data.Sqlite.Core` with independently tested #46 at
   10.0.13; explicit native SQLite bundle 3.0.5; retain high-severity
   NuGet advisory-as-error gate. No automatic credential store or actor
   issuance service added.
5. Original W9 cross-process tester now creates the store once in a
   disposable private directory then reopens across retries, exercising
   separate human/automation/agent calls, A CAS collision, B progress,
   first-result accepted and rejected replay after restart, pre/postcommit
   crashes, altered-idempotency denial, duplicate owner denial, corrupted
   store refusal, missing-store/unsafe-parent/unauthorized-create refusals.
   The same W10 desktop observer and Windows live UIA semantic checks
   remain required. None of these tests qualify actual full-machine power
   loss, ENOSPC, NFS, SMB, SSD/controller cache barriers, symlink races,
   Windows ACL permissions, or human proof of identity.

## Sequential admission frontier

- First: exact-source Windows/Linux/macOS acceptance + independent
  storage/security review. Keep experimental flag `true` only for a
  successfully admitted disposable store; it is not production approval.
- Next: W8 authenticated actor admission must derive actor identity and
  per-session scopes from a verified principal, with trusted grant issuer,
  durable revocation and *no legacy shared-token mutation bypass*.
- Then: authoritative GUI session selection with event cursor catchup,
  explicit participant join and CAS policy; keyboard and screen-reader
  usability and real desktop HIL independently required.
- Later: separately authorized private ChatGPT bridge for one conversation
  used simultaneously by human, automation and agents while B progresses.
  Real provider submit/terminal operations remain disabled until owner gate.

No merges, private source, local Windows Docker installation, real ChatGPT
prompts, physical TrueNAS usage or production rollout were authorized here.
