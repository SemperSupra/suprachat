# Model A actual compiled CLI handoff rehearsal

This isolated cloud workcell builds and runs the real, already merged SupraChat Go tunnelctl CLI against a Go-compiled synthetic official-client process. It neither provisions real OpenAI Platform tunnels nor connects to ChatGPT, and it uses no user credentials, account secrets, browser profiles, or live plugins.

Public cloud test scenarios:
- Two concurrent independent CLI installation requests with distinct aliases, tunnel IDs, localhost MCP ports and synthetic runtime credential canaries.
- Every result verifies running, healthy, ready and explicitly states ChatGPT connector not created and authenticated WebUI not observed.
- One instance exercises optional Codex plugin installation through a second command without inheriting runtime/admin keys.
- Named local runtime stop, isolated for each installation.
- Fail-closed behavior for missing runtime/admin credentials, implicit mutation, unsafe remote endpoint and wrong tunnel ID.
- Three native public CI operating systems, repeat randomized tests, and Linux race detector.

This complements source-isolated #165 and #166 ownership planning and merged #164 upstream binary/version verification. It cannot prove that a ChatGPT account supports two custom plugin connections; that remains operator-authorized and separate from synthetic proof. Model B shared-gateway work is explicitly out of scope.

The workflow emits an explicitly synthetic fixed-schema receipt only after its real test job succeeds. The receipt is not an account attestation or proof of real Platform tunnel provisioning. It does not waive native Windows UIA protected product acceptance.
