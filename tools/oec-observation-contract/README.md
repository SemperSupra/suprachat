# OEC observable-surface contract v1

This workcell validates **public-safe, metadata-only** user-interface observation receipts. It does not connect to a signed-in browser and is not a bridge implementation.

## Supported evidence
- `PUBLIC_SYNTHETIC_NATIVE`: observed on a disposable public CI runner. Authentication state is `NOT_ASSESSED`.
- `USER_ATTESTED_LOCAL_UIA`: observation supplied by a local operator who explicitly attests to having authenticated. Login is **not** independently verified by the schema.

Only fixed booleans and bounded counts are retained. No text/values, URLs, window titles, screenshots, cookie stores, bearer tokens, API keys, DOM content, session identifiers, process identifiers, or unrestricted browser access.

## Three audiences
- Human: exact PASS/FAIL gates and separate login evidence classification.
- Automation: strict schema, no duplicate/extra fields, deterministic JSON bytes and fail-closed negatives.
- Agent: proposed read-only `observe_environment`, `inspect_ui`, and `compare_state` capabilities; `remote_control=false`.

## Qualification
`python3 -m unittest discover -s tools/oec-observation-contract -p 'test_contract.py' -v`

Cloud CI qualifies the validator on public synthetic fixtures. It does not prove authenticated-WebUI behavior, ownership of local login, or any OpenAI tunnel association.

## Integration sequence
1. Operator signs into local SupraChat and explicitly approves observation. Do not move authentication credentials to CI or public artifacts.
2. Local Windows UIA actor obtains only bounded counts and fixed-ID presence, never raw UI text/DOM or screenshots.
3. Validator rejects any unacceptable fields and labels user-attested evidence separately from direct technical observation.
4. After admission, a separate narrow read-only MCP server may make this receipt available through OpenAI Secure MCP Tunnel. Requires a tunnel ID, runtime API key and account/workspace connector permission. The **earlier refused** `tools/oec-auth-bridge/main.go` write remains refused and is not replaced here.
5. Test unauthorized calls, replay/expiry, credential leakage and teardown before any live authenticated tunnel use.

**Authority:** SupraChat private DLE #2. No native code changes, merges, production credentials, private Actions minutes, release, or deployment.
