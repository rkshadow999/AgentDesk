# Upstream Provenance And Synchronization

[English](UPSTREAM.md) | [简体中文](UPSTREAM.zh-CN.md)

## Project Identity

AgentDesk is an independent, community-maintained project. It is not affiliated with or endorsed by xAI, SpaceXAI, OpenAI, or Codex. The repository preserves the complete `xai-org/grok-build` history for license compliance, review, and future synchronization; preserving history does not make AgentDesk an official upstream distribution.

## Recorded Base

- Upstream repository: `https://github.com/xai-org/grok-build.git`
- Initial AgentDesk base: `c68e39f` (`Publish harness and TUI open-source`)
- Current synchronized upstream commit: `c68e39f`

The initial base is a permanent provenance marker. After an accepted upstream synchronization, update the current synchronized commit in both language versions of this document and update any validation logic that compares AgentDesk modifications with the synchronized upstream tree.

## Remote Policy

The canonical community repository is `origin`. The original repository is named `upstream`, is used for fetches, and has a deliberately disabled push URL:

```powershell
git remote -v
git remote get-url upstream
git remote get-url --push upstream
```

The expected upstream fetch URL is `https://github.com/xai-org/grok-build.git`; the expected push URL is `DISABLED`. Maintainers configuring a fresh checkout use:

```powershell
git remote add upstream https://github.com/xai-org/grok-build.git
git remote set-url --push upstream DISABLED
```

Never push AgentDesk branches, tags, release metadata, or credentials to `upstream`. If a generally useful fix should be offered upstream, prepare it separately through an appropriate fork and follow upstream's contribution process without implying endorsement.

## Synchronization Procedure

Upstream synchronization must be a dedicated pull request. Do not combine it with an AgentDesk feature, refactor, dependency upgrade, or repository-wide formatting change.

1. Fetch and inspect the candidate range from the recorded current synchronized commit:

```powershell
git fetch --prune upstream
git switch main
git pull --ff-only origin main
git switch -c chore/sync-upstream-YYYY-MM-DD
git log --oneline c68e39f..upstream/main
git diff --stat c68e39f..upstream/main
```

2. Read upstream release notes and inspect license, dependency, generated-workspace, authentication, tool execution, sandbox, ACP, and packaging changes before merging.

3. Merge while preserving upstream history:

```powershell
git merge --no-ff upstream/main
```

4. Resolve every conflict by understanding both sides. Do not accept all of `ours` or `theirs`. Give additional review to AgentDesk ACP extensions, authentication and credential cleanup, permission mapping, sandbox and child-network enforcement, process lifecycle, generated Cargo metadata, public branding, and legal notices.

5. Reapply or adapt AgentDesk changes in small commits when necessary. Preserve user-visible risk wording and fail-closed behavior unless a separately reviewed security design proves a replacement.

6. Update the current synchronized upstream commit above to the merged upstream commit. Update the Chinese counterpart in the same change.

7. Run the affected Rust suites plus all AgentDesk Web, .NET, release, publication, formatting, and whitespace checks documented in [`desktop/README.md`](../desktop/README.md#tests). Enforced Linux sandbox tests must run in an environment that can require enforcement.

8. In the pull request, record the old and new upstream commits, reviewed upstream range, conflicts and resolutions, AgentDesk adaptations, notice changes, verification evidence, and any deferred incompatibility. Require independent review for changes to trust boundaries.

For later synchronizations, replace `c68e39f` in the range-inspection commands with the recorded current synchronized upstream commit. The initial base field remains `c68e39f`.

## Conflict Review Rules

- Keep AgentDesk-owned root and desktop entry points independent and bilingual; do not reintroduce upstream logos, product screenshots, official installation commands, or corporate support contacts.
- Preserve the ACP/NDJSON process boundary. An upstream internal API is not a substitute for a negotiated desktop protocol.
- Treat changes to authentication, environment inheritance, permissions, process creation, WSL, sandboxing, and network access as security-sensitive.
- Keep `NativeProtected` visibly non-sandboxed. Keep `WslStrict` fail-closed unless the complete attestation contract is satisfied.
- Review changes to `Cargo.toml`, lockfiles, generated files, vendored code, and package metadata for reproducibility and license impact.
- Preserve accessibility behavior, bilingual strings, architecture parity, and release signing gates.

## License And Modification Notices

Do not delete or rewrite upstream copyright statements, `LICENSE`, `THIRD-PARTY-NOTICES`, vendored notices, or source-availability text. Third-party files retain their original licenses.

An inherited Rust source file changed by AgentDesk relative to the recorded synchronized upstream tree must keep this exact leading notice before module documentation and imports:

```rust
// Modified by the AgentDesk project for Windows desktop integration and safety support.
```

New AgentDesk-only files do not need an upstream modification notice, but they still require the correct repository license and third-party attribution. After changing the recorded synchronized commit, update the modification audit and public-repository validator so upstream-only changes are not mislabeled as AgentDesk modifications and local modifications remain covered.
