# Headless SupraChat prompt executor — bounded consumer

**Status: RDTE candidate.** This is a small executable consumer of SupraChat's existing first-class JSON-RPC stdio surface, **not a scheduler, queue, model abstraction, browser automation API, or Agent Dispatch authorization service**.

The user-selected topology is: `xyOps schedules/receives webhook → Agent Dispatch authorizes exact workcell and actor → WOW admits execution → SupraChat submits the prompt through a supported provider session → downstream validator reads a structured receipt → project DLE accepts or rejects effect`.

## Actual SupraChat interface

Public SupraChat `SupraChat.Automation stdio` exposes `responses/create`. The implementation returns `{schema:"suprachat-machine/v1",binding:"siwc-responses",completed:true,request_id,output_text}` only when the provider stream reaches `response.completed`. The helper here starts that local program directly (**no shell**), writes exactly one JSON-RPC request, closes stdin, reads the correlated response, and outputs a single receipt. It never creates or obtains auth credentials. The underlying local SupraChat runtime must already have an authorized supported provider/session binding. A signed-in ChatGPT WebView is NOT automatically available headlessly by virtue of this API; no cookies, undocumented ChatGPT internals or native ChatGPT Tasks API are used.

### Minimal request (private local runtime, never public CI with real prompts)

```json
{
  "schema": "suprachat-executor-request/v1",
  "operation_id": "workcell-01234567",
  "assignment_id": "assignment-01234567",
  "authority_revision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "model": "approved-model",
  "prompt": "Return a JSON object with decision and evidence fields.",
  "require_json_object": true
}
```

The required assignment and authority fields are **correlation claims only**. Their presence does not validate GitHub authority or confer a grant. **Agent Dispatch must independently bind the existing assignment and approved revision before invoking.** A future admitted runtime may bind these fields to its current assignment/readback but must never accept a fresh arbitrary assignment as authority.

Build with `GO111MODULE=off go build -o suprachat-headless-executor ./tools/headless-executor`. Use one trusted, locally installed, published `SupraChat.Automation` apphost. The runtime starts the apphost with fixed argument `stdio`; model and prompt are sent over stdin, never argv. For a .NET DLL deployment, publish a platform-specific apphost rather than granting arbitrary command execution to an untrusted task payload.

```text
suprachat-headless-executor -executable /trusted/path/SupraChat.Automation -timeout 2m < approved-private-request.json
```

Output: `suprachat-executor-receipt/v1`, with `COMPLETED | INVALID_RESULT | REFUSED | UNKNOWN`, optional `output_text`, optional parsed `parsed_object`, provider request ID, source correlation, and `owner_effect_accepted:false`. Real model text is returned to the **local admitted downstream consumer only**, not sent to public logs. JSON parsing validates syntax/object shape, not policy claims or provenance. A provider-completed response is not proof of owner task success.

**Fail-closed semantics:** malformed input, request ID mismatch, malformed/duplicate responses, stream incomplete, process error or timeout cannot become COMPLETED; attempt uncertainty is terminal to this invocation. There is **no automatic retry** after any attempt. The owner must reconcile potentially ambiguous provider effects and acceptance before scheduling a successor. No completed job, status update or GitHub write occurs here.

## Qualification and integration holds

- Public-safe tests use a synthetic JSON-RPC child process, exercising complete/invalid/incomplete/auth denied/wrong ID/duplicate/notification/timeout/no-runtime/extra fields. This is *not* a live provider-model call.
- Separate CI builds the actual SupraChat.Automation project and tests a credential-free read and malformed-request refusal on `stdio`. This validates native protocol behavior but explicitly **does not** test an authenticated ChatGPT model run, signed-in official ChatGPT product UI, private prompts, durable external continuation, or owner DLE receipt.
- Existing SupraChat shared-session experiments remain distinct; `W6` metadata host is disposable and `W12` scoped grants are not proof of an OS human principal. Future admitted HIL must reconcile real provider identity/credentials, session isolation, consent and cancellation before using this with live tasks.
- No secret, task prompt, token or provider transcript should be included in public Actions logs or artifacts. Public fixtures are synthetic.
- Owner authority: [SupraChat private #2](https://github.com/SemperSupra/suprachat-private/issues/2) and [Agent Dispatch private #110](https://github.com/SemperSupra/agent-dispatch-private/issues/110). xyOps is the clock, Agent Dispatch the work authority, WOW trusted execution, and SupraChat a **candidate executor only**. No scheduling logic belongs in this directory.
