# SupraSession controller-observation composition (I4/E1 candidate)

Owner: [SupraChat #19](https://github.com/SemperSupra/suprachat/issues/19). Cross-product cold-start locator: [suprachat-private #6](https://github.com/SemperSupra/suprachat-private/issues/6). Autonomous follow-through owner: [Agent Dispatch private #110](https://github.com/SemperSupra/agent-dispatch-private/issues/110).

## Minimum contract

The source of truth remains a **project-native authoritative work snapshot**. An optional **normalized controller observation** may enrich the derived projection. The controller input is **not** a second workstream state, not proof of source freshness, and not a grant to run any action.

```text
project-native owning authority -> AuthoritativeWorkSnapshot ---+
                                                              +---> WorkProjectionEngine.Project(...) -> HumanWorkProjection
Agent Dispatch read-only execution observation -> caller-bound --+
```

The input schema `suprachat-controller-observation/v1` requires:

- `schema`: exact schema above
- `workstream_id`: exact match to the owning snapshot; mismatches fail closed
- `source_system`: exactly `agent-dispatch` for this bounded candidate
- `delegation_id`: non-empty, owner-correlated logical delegation
- `execution_state`: one of `UNKNOWN`, `READY`, `RUNNING`, `WAITING`, `BLOCKED`, `COMPLETED`, `FAILED`, `CANCELLED`
- `information_class`: `public`, `internal`, or `restricted`

Only those structured fields are retained. No raw Sidecar payload, free-form provider strings, source generation, credentials, grants, executable commands, or controller-created actions enter the result. A caller must establish owner correlation externally; string fields are not an authentication mechanism.

When visible to the chosen projection context, the controller view always reports `Freshness=UNKNOWN_NO_SOURCE_REVISION`, `Acceptance=NOT_ESTABLISHED`, `RequiresPointOfUseReconciliation=true`. Even `COMPLETED` never means accepted. Consequential actions remain with Agent Dispatch and MUST re-read owning authority at point of use. A disallowed-class controller observation is entirely withheld except for a count; all owner state and allowed action IDs remain unchanged.

## Surface access

Operator-local CLI (explicit files only):

```text
SupraChat.Automation work-projection --fixture owner.json --profile rich --controller-observation observation.json
```

Agent JSON-RPC: `work/projection/read` takes `{ "profile": "rich", "snapshot": { ... }, "controller_observation": { ... } }`; unlike the local CLI, JSON-RPC never takes a file path for either object.

The observation is optional, so all earlier clients continue to work. The rich, compact, and restricted human renderers continue projecting the owning snapshot. This tranche does **not** pretend that the static dogfood fixture is a live GitHub reader or that a remote security boundary exists.

## Read-only evidence and boundaries

- Public PR #20 qualified the prior I0/I1/I2 core through existing Windows/Linux/macOS workflows at exact head `1d3c039b1d9f1c7ef7bb8a2f29245f42fc2bf368` (run `37456129753`).
- Sidecar read-only `validate-projection` is currently reachable and returns the existing least-authority `execution-contract`, but **no assignments** are present in that delegation; no dispatch was attempted.
- The live sidecar contract does not supply a trusted owning-workstream generation to SupraChat. A higher-layer caller cannot invent one. The owning #19 fixture is not a live source reader.
- Contract tests in this candidate exercise owner/observation separation, disclosure suppression, mismatched identity, provider-completion != acceptance, and immutable owner semantics. These are source-level candidates until independently qualified at the new exact head.
- Public `main` includes the merged credential PR #8 and has diverged from PR #20. This work is stacked on PR #20 instead of force-rebasing or merging unrelated security branches.

## Next falsifiable natural rep

Choose a real, already-authorized project-native work item with a genuine controller assignment/run. Read owning authority, then obtain the currently admitted read-only Sidecar observation, normalize only the approved fields, render through CLI/JSON-RPC, verify matching owner identity and masking, and ensure an observed completion leaves acceptance unasserted. When a consequential action is independently authorized, require Agent Dispatch to reconcile at point of use and return exact evidence. Do not dispatch merely to populate a demo, nor permit this projection to own lifecycle state.

The smallest warranted future adapter should be introduced only after the natural rep shows that manual normalization has recurring cost or a specific failure. UI/mobile and remote brokerage remain separate downstream work.