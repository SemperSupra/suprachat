# SupraChat RDTE receipt-contract proving slice

This directory contains a **public-safe, dependency-free PowerShell 5.1 receipt primitive**, not a new controller, scheduler, release, credential manager, or full local RDTE driver.

## Native qualification

The focused workflow `.github/workflows/rdte-receipt-contract.yml` executes `Test-ReceiptContract.ps1` on a public GitHub-hosted **Windows** runner using `powershell.exe` with **Desktop 5.1**. It tests the exact V6 `$Matches` automatic-variable collision, matching/empty first reads, duplicate/malformed/conflicting markers, embedded kickoff examples, CRLF, NOOP, failed/ambiguous POST and readback confirmation. No private DLE, credentials, account, browser, local host or private Actions are used.

A green test proves this isolated primitive only; it **does not certify the old local V6 script**, native app HIL, actual REST transport, or production release.

## Reduce human toil: workflow boundary

1. **Cloud by default:** GitHub connector + public free-tier GHA handle source-only implementation, native Windows PowerShell tests, deterministic PR census and first-failure triage. Put source, proof links and next workcell in the project-native DLE. Do not ask a human to download local scripts for these tasks.
2. **One-time local bootstrap for genuine HIL only:** Once the complete driver is native-CI qualified and owner-admitted, provide one stable `Start-SupraChat-RDTE.ps1` in `$HOME\Downloads`. It should read an **owner-admitted immutable artifact SHA, path and content digest** from the private DLE, download into a run-owned `$HOME\.local` directory, verify SHA-256 **before execution**, and reuse the installed entry point without ZIP/extract/copy ritual on each version. Require one-time operator authorization/connection. Do not launch the authenticated WebView or touch persistent real browser profiles automatically.
3. **No unqualified auto-update:** A newly pushed commit, PR-green run or arbitrary DLE comment never grants execution by itself. Require independent native acceptance, a trusted owner-signed/owner-authored DLE admission, exact immutable source content and checksum match, and compatible running mode. Pin each execution to its consumed source/digest.
4. **Return receipts:** Publish bounded sanitized results to the owning private DLE, require exact first-line/full-body readback, and reconcile uncertain POST before any retry. The human is not the message bus.
5. **Housekeeping:** Record source-backed exact-head candidates and first failures while working. Preserve unique/divergent/stacked sources; do not merge, close PRs, force push, delete branches, or weaken CI under an automated hygiene census. Previously refused operations remain held.

## Recovery decision already completed

V5 local 55-PR hygiene census is returned at private DLE #2 comment `6673066873`; digest `5c8f74bafd095219708fb38567dae84d7a0b8a7707c5f30e6bba5aff4b9e13bf`. **Do not run V6 RecoverV5 again.**

## Acceptance for follow-on full driver

Before asking the operator to run a new binary/script: native Windows PowerShell 5.1 parser and actual package self-test; isolated filesystem/reparse/ownership/cleanup/atomic replacement tests; fake GitHub pagination, DLE admission and POST readback; restart/idempotency and first-failure recovery; artifact digest; signed/owner-admitted promotion; and zero global installations, profile writes, Docker or existing worktree mutations. Keep the initial browser-authenticated HIL separately consented and explicitly supervised.
