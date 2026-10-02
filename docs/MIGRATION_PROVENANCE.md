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
- qualification-workflow history remains attributable to the source repository; normalized dedicated-repo workflows were added after extraction by the GitHub control plane
- filtered snapshot head before dedicated-repo normalization: 37907140842f14eefa664ae823dc99f8e2c8f710

Dedicated-repository qualification:
- extracted branch: migration/extracted-qualified-20261002
- qualified head: 841a82ed4207457495254ba690544f00535f5d38
- public GHA run: 37023386130
- Windows x64: PASS
- Linux x64: PASS
- macOS arm64: PASS
- deep Linux/macOS clean-room census is a separate manual oracle plane and was intentionally skipped in the product cutover gate

Canonical authority:
- main was moved directly from the seed history to the independently green extracted history after run 37023386130 passed.
- SemperSupra/suprachat is now the public product/source/CI authority.
- SemperSupra/suprachat-private#1 remains the private DLE/oracle/evidence continuation authority.
- Agent Dispatch remains an execution/orchestration substrate rather than product authority.
