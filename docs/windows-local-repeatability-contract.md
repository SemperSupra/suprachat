# SupraChat Windows qualification: discovery and four properties

Authoritative work root supplied by operator:
C:\Users\Mark\Projects\SemperSupra\SupraChat

This folder contains source checkouts and potential previously installed tools.
Their existence is not proof that any SDK, Node runtime or container is usable.

## Discover -> Observe -> Plan -> Apply -> Verify

The local actor begins with the system PATH inventory and bounded, read-only
discovery of the established SupraChat workspace. Eligible roots are the
workspace's .local, _tools, suprachat/.local, suprachat/_tools,
_dogfood/.local, _dogfood/_tools and suprachat-private/.local directories.
Discovery is capped at three child-directory levels and 200 directories; it
checks versions of named dotnet.exe and node.exe, and refuses junction paths.
It does not search browser profiles, credentials, account data, or arbitrary
source trees. If usable tools are found, only the actor process PATH changes.

Command for discovery alone from an existing disposable branch checkout:

    pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\qualify-chatgpt-windows-local.ps1 -DiscoverOnly -WorkspaceRoot 'C:\Users\Mark\Projects\SemperSupra\SupraChat'

No SDK/Node download, browser navigation, authenticated action, or private Git
checkout occurs in DiscoverOnly mode.

## Reproducibility

The receipt records the public Git commit, verifies the exact private
PR #104 commit when private checks are enabled, and captures tool versions.
For byte-identical tool provenance, use pinned portable Microsoft .NET SDK
10.0.402 and Node v24.21.0 official archives, SHA-512/SHA-256 verified.
Reusing a workspace binary only establishes version compatibility, not
byte identity; no checksum is claimed for reused binaries. A moving branch
and a live provider site cannot guarantee identical future responses.

## Repeatability

Offline fixtures and build checks may be rerun without ChatGPT account
side effects. Each qualification creates a distinct timestamped evidence
directory. Receipts are intentionally append-only, not identical file trees.

## Reversibility

Portable dependencies are installed only below the disposable checkout's
.local/suprachat-qualification directory. An ownership marker records its
checkout identity, and any preexisting unowned directory blocks writes.
Cleanup is dry-run by default and -Apply removes ONLY that verified owned
namespace. Never delete the established work root or private code.

    pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\cleanup-chatgpt-windows-local.ps1
    pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\cleanup-chatgpt-windows-local.ps1 -Apply

The first command previews, the second performs the owned-tools removal.
Both refuse unowned paths and symlinks. Actor receipts and temporary source
checkouts are separate owner-managed items and are not removed by cleanup.

## Idempotence

The default runs offline tests only. Repeating a successful bootstrap
reuses verified local installed versions; repeated cleanup is a no-op.
The -ForcePortableTools option deliberately rewrites the pinned local
toolchain and is NOT a no-op. Native browser tooling, NuGet and Playwright
caches, and timestamped receipts may mutate local disposable state while
keeping the same accepted test result.

A real anonymous ChatGPT prompt creates an external side effect and is
NOT idempotent. To authorize exactly one benign anonymous attempt on each
explicit invocation, add -RunLiveGuest. This is not suitable for unattended
retry loops. No signed-in ChatGPT prompt is authorized or automated.

## Normal batch — workspace root is NOT a Git checkout

The established Windows work root has several child folders and no .git
directory at its root. Do not issue git pull or use relative tools/ paths
from C:\Users\Mark\Projects\SemperSupra\SupraChat. Resolve a separate,
actual disposable checkout first, and pass WorkspaceRoot independently.

    $workspace = 'C:\Users\Mark\Projects\SemperSupra\SupraChat'
    $checkout = 'C:\Users\Mark\AppData\Local\Temp\suprachat-qual-2a135668507f48ca93fa2529acba9c53'
    $branch = 'feat/19-chatgpt-control-contract-20261020'
    if (Test-Path (Join-Path $checkout '.git')) {
        $actual = (& git -C $checkout branch --show-current).Trim()
        if ($actual -ne $branch) { throw "Unexpected checkout branch: $actual" }
        git -C $checkout pull --ff-only origin $branch
    } else {
        $checkout = Join-Path $env:TEMP ('suprachat-qual-' + [guid]::NewGuid().ToString('N'))
        git clone --depth 1 --branch $branch https://github.com/SemperSupra/suprachat.git $checkout
    }
    if ($LASTEXITCODE -ne 0) { throw 'Checkout update/clone failed' }
    $runnerScript = Join-Path $checkout 'tools\qualify-chatgpt-windows-local.ps1'
    if (-not (Test-Path $runnerScript)) { throw 'Actor script not present in checkout' }
    pwsh -NoProfile -ExecutionPolicy Bypass -File $runnerScript -WorkspaceRoot $workspace -DiscoverOnly
    if ($LASTEXITCODE -ne 0) { throw 'Discovery failed' }
    pwsh -NoProfile -ExecutionPolicy Bypass -File $runnerScript -WorkspaceRoot $workspace -IncludePrivateBridge -NativeBrowserSmoke

Do not run with -RunLiveGuest unless an external prompt is desired. Share
only the newly emitted summary.json and keep private bridge checkout files
local. Existing authenticated-chat bridge control remains gated by the
independently recorded lost-acknowledgement quarantine defect on PR #104.

The public CI Windows portable-tool job tests force bootstrap, workspace
discovery, repeat no-op bootstrap, dry-run cleanup, actual owned cleanup,
and repeat cleanup. It does NOT exercise any existing user workspace.
