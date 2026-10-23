# W12 research-only scoped metadata writes on existing W6 shared host

The owning requirement in #19 is multiple humans (including assistive users),
automations and agents coordinating in the *same* session while other sessions
advance independently. W12 addresses one interim negative: W6/W11's one
transport bearer previously let a caller falsely label itself `human` or
`agent` on a mutation. This branch composes already-qualified W8 HMAC
semantics (#44) with the exact W11 W6/W9 host (#49).

## Exact scope

- Default historical W6/W11 transport remains unchanged when
  `SUPRACHAT_W12_SCOPED_WRITES` is unset. Such a host is **not suitable
  for authorized multi-principal sharing**.
- Experimental mode is **opt-in only** using
  `SUPRACHAT_W12_SCOPED_WRITES=synthetic`; it requires the explicitly owned
  W11 disposable SQLite host and 32 random signing bytes encoded as base64
  under `SUPRACHAT_W12_SIGNING_KEY`. No listener starts without these.
- All loopback clients must still satisfy the existing bearer/Origin/host
  header rules. However **the bearer alone never permits POST** in W12
  mode. A separate signed, short-lived capability goes in
  `X-SupraChat-Actor-Capability`, including machine CLI/JSON-RPC using
  `SUPRACHAT_W12_ACTOR_GRANT` (not the signing key).
- An explicit offline trusted issuer (not provided as an HTTP endpoint)
  is assumed to have approved an actor and issued the signed capability.
  Token claims contain actor ID, audience, one session, scope and expiration.
  The create-only sentinel session `create-only` and exact
  `session.create` scope permit creating a new metadata-only session,
  without allowing existing session mutation. A scoped
  `session.mutate` claim for a specific immutable session ID permits
  only metadata title/conversation/workspace CAS. No ChatGPT prompts,
  terminal commands, remote actions, approvals, or actual human identity
  have been delegated.
- Every signed actor is reflected in an actor-specific idempotency
  namespace (hash of signed actor ID and client operation key). Two
  independent agents choosing identical operation keys do not replay or
  inherit one another's first outcomes. The same actor can repeat an
  operation with a newly signed short-lived grant and retrieve its
  historical first outcome after W11 host restart.
- The host refuses request-body `audience` mismatches with the signed
  audience, malformed signature, wrong session, expired grant, missing
  mutation scope and all plain common-bearer-only writes. The actual
  `authenticated_audience_identity=false`,
  `authenticated_os_peer_identity=false` and
  `production_durable_admission=false` flags remain.

## Causal qualification

`node tools/qualify-w12-scoped-writes.mjs` generates disposable signing
material in process, starts the *actual* W6/W11 loopback host and uses
separate HTTP and existing agent JSON-RPC client processes with no signer
key in the agent environment. It checks signed human and automation create
operations, agent/human contention on one session revision, another
session B progressing, common bearer-only mutation rejection, forged role,
wrong session/scope/expired/tampered grant, collision of global operation
keys by two actors, old APPLIED and VERSION_CONFLICT first outcomes on
replay after host restart, exact session identities and shared desktop
read-only inventory after restart. It must pass on all three public
GitHub Actions OSes alongside the existing W11 crash/recovery regression.

## No security overclaim

- **HMAC is delegation, not actual login**. A stolen bearer capability can
  impersonate its actor until expiration. This does not verify OS token,
  process, device, human presence, MFA, owner approval or delegated issuer
  identity. The signing key in host environment may be readable by
  untrusted same-user processes. This RDTE is not authorized for private
  conversations, credentials or production data.
- **Revocation is not durable and not implemented in the W12 host.** The
  underlying W8 in-process revocation method is not exposed. Before
  production, persist revocation under the SAME W6 durable state and
  prevent a killed process from resetting a revoked capability.
- W12 is additive to W11 only as a **separate research mode**. The old
  common bearer write bypass remains in historical W6 mode; production
  must require scoped actor mode unconditionally after independent
  approval. The unbound accessible GUI remains read-only.
- No direct provider ChatGPT submit, authenticated conversation binding,
  PTY action or real-world external effect is qualified here.
- Keep exact CI evidence, unrelated Windows intermittent native UIA
  failures, storage owner-only ACL concerns, and all merge/release gates.
