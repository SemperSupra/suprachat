# Windows UIA provider tree: real disposable-runner witness

**Authority:** [SupraChat issue #152](https://github.com/SemperSupra/suprachat/issues/152), [private DLE #2](https://github.com/SemperSupra/suprachat-private/issues/2), approved COA C. A source-isolated, **read-only** diagnostic for a public synthetic SupraChat process.

## Execution

Dedicated public Windows 2025 GitHub Actions workflow:

1. Parse the PowerShell source and demand that a non-SupraChat process is refused.
2. Build the **unmodified actual application** via `dotnet publish` with .NET 10, launch only that disposable runner-owned binary.
3. In Windows PowerShell **5.1**, sample the UIAutomation control view for 10 observations of the same expected `SupraChat.MainWindow` process. Record root/child/descendant counts, `Tab.Sessions` matches, process/root consistency and provider exception *types*.
4. Kill the fixture and upload only an allowlisted metadata receipt with explicit `result_class`, including `UIA_ROOT_NOT_OBSERVED` as a legitimate UNKNOWN.

The recorder never reads names, text or values from controls; it neither clicks nor types, invokes UIA action patterns, changes protected source, uses credentials, launches browsers or exports host paths/PIDs. It bounds sample count and tree breadth. It cannot guarantee that a provider COM call will return promptly; the runner job has a hard timeout. Only a real successful Windows workflow run establishes **native source execution**.

This workflow is **diagnostic, not acceptance**: it does not weaken or supersede the existing full-product Windows W1/W2 and accessibility tests. A successfully captured missing-tree result is a real diagnostic observation, not a product PASS. A stable tree does not prove the sporadic native test is repaired. Follow up with randomized/temporal repetition on the same authorized fixture if needed, preserving first failures. Observations belong in Git/DLE as small metadata; larger binaries remain in private rights-cleared Drive if admitted.

**Ownership:** changes only `tools/oec-uia-readonly/**` and `.github/workflows/qualify-oec-uia-readonly.yml`. No edits to `.github/workflows/qualify-suprachat.yml`, Avalonia source or protected local worktrees. No merges, release, deployment or private Actions runs by implication.
