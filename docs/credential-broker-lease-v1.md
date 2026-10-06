# Credential Authority Broker / Lease API — v1 Design Draft

Status: **IMPLEMENTATION CANDIDATE / BOUNDED RDTE**

Authority: public issue #9. The credential-portability dependency gate is satisfied on canonical main; this candidate authorizes only the transport-independent semantic core and deterministic public-safe qualification described here.

## Purpose

SupraChat needs local and remote consumers to use ChatGPT-plan capabilities without copying renewable SIWC authority into every consumer. The broker is the sole renewable-credential owner. Consumers receive only bounded, short-lived authority.

## Non-goals

The v1 semantic contract does not:
- choose HTTP, WebSocket, stdio, named pipes, Unix sockets, MCP, or any other transport;
- create a multi-provider abstraction;
- put renewable credentials into browsers, notebooks, CI, or client SDK state;
- replace the native desktop UI;
- make Agent Dispatch product authority.

## State

### BrokerState

- host identity
- credential presence
- credential generation
- credential expiration
- serialized refresh ownership/in-progress state
- broker epoch/instance provenance
- active lease records
- revocation state

### Lease

A lease contains only bounded consumer authority and metadata:

- opaque lease ID
- consumer identity/provenance
- consumer-instance binding
- granted capabilities
- issued-at timestamp
- expiry timestamp
- broker epoch/generation at issuance
- revocation state

A lease never contains a refresh token, renewable SIWC credential bundle, browser credential state, or any value from which renewable authority can be reconstructed.

A lease ID is an identifier, not authority by itself. The semantic contract MUST support binding lease use to the consumer instance that acquired it. For remote transports, v1 MUST NOT depend on a freely replayable bearer lease ID alone: the adapter must prove consumer possession or authenticated channel/session binding in a way that is independently testable. The exact transport mechanism remains outside this semantic contract.

## Semantic operations

### broker/status

Read-only redacted broker state. It MUST NOT trigger refresh solely to satisfy a status read.

### lease/acquire

Inputs:

- idempotency/request ID
- consumer identity/provenance
- requested capabilities
- requested TTL, optionally omitted

Behavior:

1. Validate consumer identity, instance binding, and authorization policy.
2. Validate requested capabilities.
3. Under the broker's exclusive credential/refresh lease, obtain a usable broker credential if required.
4. Mint bounded consumer authority bound to that consumer instance.
5. Persist the lease before returning success.
6. Return redacted lease metadata and the minimum bounded consumer handle needed by the chosen transport.

The acquire request must carry or derive a stable consumer-instance binding. A remote adapter must reject subsequent lease use when the caller cannot prove the same binding.

Retry of the same request ID MUST produce the same accepted outcome or a deterministic conflict; it MUST NOT mint extra authority.

### lease/status

Read-only redacted lease state.

### lease/release

Inputs:

- idempotency/request ID
- lease ID

Release is terminal for the lease ID. Repeating the same release is idempotent and returns a stable terminal receipt.

Revocation/release semantics are admission-oriented: after the terminal transition the broker MUST reject admission of any new provider operation for that lease. A provider operation already admitted before revocation may be impossible to cancel upstream; v1 therefore records an explicit `admitted_at` boundary and must deterministically define whether its eventual response is delivered or discarded. The initial fail-closed policy is to discard the result after revocation unless a stronger causal requirement is proven.

### lease/renew

Deferred from v1 unless a concrete consumer proves an explicit renew operation is required. Preferred v1 behavior is to acquire a new bounded lease; renewable SIWC refresh stays broker-internal.

## Core invariants

1. Exactly one refresh owner exists for a renewable SIWC credential lineage.
2. Credential generation cannot regress.
3. Same-generation credentials with conflicting rotating refresh lineage fail closed.
4. Consumer authority lifetime is bounded by broker policy.
5. Consumer authority cannot be used after terminal revocation/expiry outside an explicitly documented in-flight boundary.
6. Consumers cannot obtain or reconstruct renewable SIWC authority.
7. Consequential operations are idempotent and replay-safe.
8. Read operations do not mutate refresh ownership.
9. Transport adapters cannot expand semantic authority.
10. Receipts, diagnostics, logs, CI artifacts, notebook output, and browser storage contain no credential material.
11. Crash/restart recovery fails closed for uncertain lease state.
12. Every lease use is bound to the acquiring consumer instance; possession of an opaque lease ID alone is insufficient authority for remote use.
13. Human, automation, and agent audiences use first-class semantic interfaces rather than scraping each other.

## Conceptual state transitions

```text
NO_CREDENTIAL
    |
    | human authorization / qualified import
    v
READY
    |
    | refresh due
    v
REFRESHING ---- failure ----> AUTH_REQUIRED
    |
    | success, generation monotonic
    v
READY
    |
    | acquire
    v
LEASED ---- release/revoke ----> REVOKED
    |
    +---- ttl boundary ---------> EXPIRED
```

Multiple leases may exist simultaneously, but they are consumers of one broker-owned renewable credential lineage. They do not become independent refresh owners.

### Restart epoch

Broker process restart increments a broker epoch. For v1, all leases issued under an earlier epoch are invalidated on restart. Durable lease resurrection is deliberately deferred until there is evidence that it is worth the additional recovery-state complexity. Clients may reacquire a new lease after reconnecting.

This gives v1 a simple fail-closed restart oracle:

```text
lease.epoch != broker.current_epoch -> BROKER_RESTARTED
```

No pre-restart lease may silently resume authority.

## Stable error classes

- AUTH_REQUIRED
- BROKER_UNAVAILABLE
- CAPABILITY_DENIED
- LEASE_EXPIRED
- LEASE_REVOKED
- LEASE_NOT_FOUND
- REQUEST_CONFLICT
- REFRESH_FAILED
- VERSION_UNSUPPORTED
- CONSUMER_BINDING_FAILED
- BROKER_RESTARTED

Transport-specific failures map to these semantic classes where applicable and must not alter their meaning.

## Acceptance model

Before broker v1 is considered qualified:

1. deterministic transition tests cover every state edge;
2. property tests exercise arbitrary acquire/status/release/retry sequences;
3. concurrent acquire/refresh interleavings do not create split refresh ownership;
4. stale and conflicting credential writes fail closed;
5. replayed operation IDs do not mint additional authority;
6. broker restart/crash reps cover every consequential transition class;
7. revoke-vs-use and expiry-vs-use races have explicit oracles;
8. controlled clock-skew reps are green;
9. sentinel leakage scans cover stdout/stderr, receipts, diagnostics, artifacts, notebook output, browser stores, and CI surfaces;
10. replaying a lease ID from a different consumer binding fails;
11. broker restart invalidates all earlier-epoch leases and clients can reacquire cleanly;
12. revocation-vs-in-flight races prove the admission boundary and post-revoke response policy;
13. one local/native consumer and one remote/ephemeral consumer complete causal model-discovery and inference reps;
14. public/free GHA independently qualifies all public-safe deterministic behavior;
15. real SIWC/OAuth authorization and credential-bound HIL remain local/private.

## Dependency gate

Implementation is blocked until:

1. supplemental local UIA passphrase-readback issue #15 passes;
2. PR #8 is merged by controller authority;
3. canonical main is reconciled and green.

Design/review may proceed before those gates provided it does not modify PR #8.

## Remote bootstrap boundary

Remote transport bootstrap is permitted to establish consumer identity and mint a bounded broker lease only. It MUST NOT expose, derive, or become equivalent to renewable SIWC authority.

Any static bootstrap secret, if a first proving rep genuinely requires one, must be:
- independently scoped from SIWC credentials;
- revocable without reauthorizing the ChatGPT account;
- limited to broker bootstrap/lease acquisition;
- replaceable by a stronger workload-identity or proof-of-possession mechanism.

GitHub Actions should prefer workload identity when a practical zero-cost implementation exists; do not make that a prerequisite for the first local broker causal rep.

## Open questions to falsify before freezing v1

- Which concrete consumer-binding mechanism gives the smallest adequate proof-of-possession for each remote adapter?
- What is the minimum useful TTL ceiling for Colab/GHA/browser consumers?
- Can post-revocation in-flight responses always be discarded locally without creating confusing provider-side side effects?
- Can workload identity replace static bootstrap secrets for GHA in the first remote proving rep?
- Which adapter should be the first causal implementation: local IPC, loopback HTTP, or another existing SupraChat semantic surface?

These are design questions, not permission to broaden v1 scope.
