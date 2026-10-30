<!-- Draft notes for the HiC-approved v0.0.1 scope; update from final exact-head evidence before publication. -->

# SupraChat v0.0.1

SupraChat v0.0.1 is a Windows-first desktop preview that embeds the official ChatGPT web experience and adds a bounded local Sessions workbench.

## Included

- Local session creation and rename through the in-process session ledger.
- Explicit preview of selected local metadata as JSON or Markdown.
- Windows, Linux and macOS package builds with platform-specific launch and install qualification.

## Limits

- Sessions are in-process and are not shared with the CLI or agent shell.
- This preview does not submit prompts, read conversation transcripts, execute commands, or persist local sessions across restarts.
- Windows UI Automation reliability remains bounded by the recorded runner evidence; this release does not claim to fix general UIA root or event failures.
- Provider credential HIL, signed-in original-host admission, and production custody are outside this release's evidence.

See the attached third-party notices and platform packages. Verify the SHA-256 values in `SHA256SUMS` before installation.
