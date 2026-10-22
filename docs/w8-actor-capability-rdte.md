# W8 bounded signed participant grants — synthetic RDTE

This is a **standalone crypto/semantics experiment** for [#39](https://github.com/SemperSupra/suprachat/issues/39), based on W6 [#37](https://github.com/SemperSupra/suprachat/pull/37). It does not change W6's shared bearer host or confer production authentication.

## What is actually qualified

A trusted *in-process issuer* creates random-ID HMAC-SHA256-signed opaque capabilities, valid at most 30 minutes. The claims bind actor ID, audience label, single session, an explicit set of actions, issue and expiry times, and a grant ID. Verification checks MAC in constant time, rejects altered claims, wrong session, claimed actor/audience mismatch, missing scope, out-of-window issuance and revoked grant IDs. An agent or automation capability cannot acquire human approval scope. No callback or network endpoint allows a bearer holder to mint grants. Authorization results derive from signed claims, **never a request body's audience label**.

Three-platform CI tests human/automation/agent scoped grants, two sessions, fake forged actors/roles, expired grants, tampering, revocation, bounded TTL and parallel read validation. No provider messages, storage or credentials.

## Critical boundaries

- **Not a login or real participant identity proof.** A stolen bearer still impersonates its granted actor. No OS peer identity, device identity, channel binding, human presence, issuer enrollment, vault/storage protection or local source custody is established.
- **Revocation is process-local, ephemeral** and not distributed nor durable across crashes; remote agents cannot be considered revoked after restart without a durable revocation ledger. W7 is evaluating that separately.
- This is a pure local RDTE; there is no production token issuance endpoint and no independent human approval or actor enrollment mechanism. It must never be exposed as a public token minting service.
- No live ChatGPT prompt, terminal execution, browser session, cross-host access, or replacement of W6's provisional single shared bearer. No merge/deployment authorized.

## Acceptance before W6 integration

1. Qualify exact code on Windows/Linux/macOS, including independent adversarial credential handling and binary/object lifetime review.
2. Establish trusted issuer admission and owner-managed grant/revoke flows for each principal; key custody and rotation policy; per-session and per-action restrictions. Disable all caller-supplied audience-as-authority.
3. Persist revocation and short-lived grant metadata through an independently accepted W7 durability authority; prevent replay across actor/session operations with identities scoped into idempotency keys.
4. Bind grants to independently verified principals or peer contexts where possible; attest these per OS. Do not call this **user authentication** on bearer signatures alone.
5. Protect browser/loopback CSRF and local origins, prevent secret logging and inherited environment leakage, prove lease expiry under clock skew and host restart, and test multiple simultaneous sessions/actors through actual W6 API and accessible human UI.

No new Docker runtime or private workload is needed.
