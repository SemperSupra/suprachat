# Official OpenAI Secure MCP Tunnel CLI — offline qualification

## Scope
This workcell verifies the *official prebuilt* `openai/tunnel-client` v0.0.19 release ZIPs on disposable **public** Windows and Linux GitHub Actions runners. It does not implement or modify an authenticated browser bridge, create a tunnel, run a remote MCP server, open a session, connect ChatGPT, or touch a local user profile.

Upstream: https://github.com/openai/tunnel-client/releases/tag/v0.0.19 (released 2026-10-30), Apache-2.0; source tag and binaries can be audited separately. Official upstream onboarding: https://github.com/openai/tunnel-client/blob/v0.0.19/docs/onboarding.md.

### Reproducible fixed inputs

| ZIP | Pinned SHA-256 (official release metadata) |
|---|---|
| `tunnel-client-v0.0.19-linux-amd64.zip` | `9ee9448592345a9dd1517fb79791ab54d24f051142eb92bcbe8cd355effc9142` |
| `tunnel-client-v0.0.19-windows-amd64.zip` | `1d0ecc3a54f2a51e27a635ac155f40fbb95df58c52cdeddf6c75532cc0bbeb25` |

The job downloads only these exact release URLs, verifies ZIP hash before extraction or execution, requires an unambiguous installed `tunnel-client` binary, and exercises `--version`, `help quickstart`, `run --help`, and `dev mcp-stub --help` **without any tunnel/control-plane credentials**. CI uses no private Actions minutes. Only fixed metadata receipt JSON is uploaded (no binaries, tokens, config, WebUI data or user session).

### Stage gates

1. **Offline CLI**: two operating-system jobs pass with real official binaries and verified hashes; evidence classification `PUBLIC_OFFLINE_UPSTREAM_BINARY`.
2. **Local observer**: a separately authorized local consented SupraChat WebView2 observation, followed by independent native summary validation and sanitized projection as implemented in source-isolated [PR #159](https://github.com/SemperSupra/suprachat/pull/159). Never substitute a fake signed-in claim.
3. **Local private MCP endpoint**: requires explicit source/owner authorization; the earlier `tools/oec-auth-bridge/main.go` write was refused, so this campaign does not reimplement it through another path.
4. **Secure MCP Tunnel and ChatGPT connector**: requires the operator's Platform tunnel ID, restricted runtime API key with Tunnels Read+Use, outbound client connection, and applicable ChatGPT custom-MCP connector permission. Cloud CI does not obtain those privileges.
5. **First signed-in ChatGPT tool call**: independently verify source SHA, tool scope, expiry/teardown, privacy, caller identity and session boundaries before claiming remote introspection.

**Source custody:** Git/PR/DLE holds code, pinned provenance, CI source and sanitized evidence links. Any authorized signed-in aggregate or credentials stay off public GitHub, public Actions, logs and remote observer; restricted evidence is local/approved private store only. No merge/release/deployment authorized.
### Additional Linux end-to-end transport rehearsal

The Linux runner also invokes the upstream `dev mcp-stub` and `dev proxy` modes from the same verified binary. These create a **loopback-only in-memory control plane** and localhost demo MCP server; the test performs a real MCP `initialize`, `notifications/initialized`, `tools/list`, and `tools/call echo` through that proxy. It verifies the `go-in-memory` backend, exact three-tool catalog, echoed synthetic sentinel, bounded timeouts and process teardown. No hosted OpenAI tunnel is created, and the test never uses a Platform key, ChatGPT account, or source-controlled credential. This qualifies the upstream *local in-memory transport path*, not OpenAI-hosted access or the refused SupraChat observer bridge.

## Local Windows preflight (no authentication or connection)

After downloading the **official** v0.0.19 Windows ZIP from the upstream release, run this source-owned PowerShell 5.1 preflight **before** extracting or executing:

    powershell.exe -NoProfile -NonInteractive -File .\tools\oec-tunnel-client-offline\Test-OpenAITunnelArchive.ps1 -ArchivePath 'C:\ABSOLUTE\PRIVATE\DIR\tunnel-client-v0.0.19-windows-amd64.zip'

The command checks the pinned ZIP SHA-256 and fixed release version, then returns a small JSON object. It does not print the archive path, keys or tunnel ID. A present key/ID is only a boolean, never validated or used. The preflight deliberately reports connector permission as NOT_ASSESSED, ChatGPT disconnected and authenticated WebUI observation false. Public Windows CI exercises a byte-corrupted copy of the official ZIP and requires refusal. The preflight neither installs a binary nor authorizes tunnel provisioning or user authentication.
