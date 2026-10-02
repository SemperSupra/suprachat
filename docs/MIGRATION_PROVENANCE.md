# Migration provenance

SupraChat was extracted from SemperSupra/agent-dispatch into this dedicated repository with product history preservation.

Qualified source:
- branch: exp/suprachat-macos-seal-repair-qualification-20261002
- source SHA: 627757867ac304c61e57af65e2fe5e32a4300afe
- independent qualification: SemperSupra/agent-dispatch#126, run 37003842653
- source result: Windows x64, Linux x64, and macOS arm64 PASS

Extraction:
- retained prototype/suprachat/**, promoted to repository root
- product commit history preserved with git filter-repo
- qualification-workflow history remains attributable to the source repository; normalized dedicated-repo workflows are added after extraction by the GitHub control plane
- filtered snapshot head before dedicated-repo normalization: 37907140842f14eefa664ae823dc99f8e2c8f710

Canonical authority switches only after this repository reproduces the tri-platform qualification from its own CI.

Private continuation/evidence authority: SemperSupra/suprachat-private#1.
