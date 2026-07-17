# AgentDesk Public Repository Design

[English](2026-07-17-agentdesk-public-release-design.md) | [简体中文](2026-07-17-agentdesk-public-release-design.zh-CN.md)

**Status:** Approved for implementation on 2026-07-17

## Objective

Publish the current AgentDesk Alpha as `rkshadow/AgentDesk`, a public community project that can be forked and extended without presenting itself as an official xAI, SpaceXAI, OpenAI, or Codex product.

## Repository Identity

- The repository name is `AgentDesk` and the default branch is `main`.
- The full upstream Git history is retained. The AgentDesk work starts from `xai-org/grok-build` commit `c68e39f`.
- The root README uses AgentDesk-owned text and screenshots. It does not reuse upstream logos, product screenshots, or official installation commands.
- Every prominent entry point states that AgentDesk is independent, community-maintained, and not endorsed by xAI, SpaceXAI, OpenAI, or Codex.
- The existing `origin` remote is renamed to `upstream` and made fetch-only. The new public repository becomes `origin`.

## Bilingual Documentation Boundary

All AgentDesk-maintained public documentation is paired in English and Simplified Chinese. English uses GitHub-standard filenames and Chinese uses `.zh-CN.md`.

Required pairs:

- `README.md` / `README.zh-CN.md`
- `CONTRIBUTING.md` / `CONTRIBUTING.zh-CN.md`
- `SECURITY.md` / `SECURITY.zh-CN.md`
- `CODE_OF_CONDUCT.md` / `CODE_OF_CONDUCT.zh-CN.md`
- `desktop/README.md` / `desktop/README.zh-CN.md`
- `desktop/THIRD-PARTY-SOURCE-NOTICE.md` / `desktop/THIRD-PARTY-SOURCE-NOTICE.zh-CN.md`
- `docs/ROADMAP.md` / `docs/ROADMAP.zh-CN.md`
- `docs/UPSTREAM.md` / `docs/UPSTREAM.zh-CN.md`
- AgentDesk design and implementation documents under `docs/superpowers/`

Inherited upstream changelogs, prompts, skills, test fixtures, license texts, and Rust user guides remain in their original language. Translating those files would create a divergent copy that cannot be kept accurate. The root documentation explains this boundary.

Every paired document links to its counterpart at the top. License texts and copyright notices remain unmodified; Chinese notice documents are explanatory translations and explicitly defer to the English license text.

## Community and Security

- Contributions are accepted through issues and pull requests under a documented test and review workflow.
- The code of conduct uses Contributor Covenant 2.1 with AgentDesk-specific enforcement contact through repository moderation, not an upstream corporate contact.
- Security reports use GitHub Private Vulnerability Reporting at `https://github.com/rkshadow/AgentDesk/security/advisories/new`.
- Public issues must not be used for undisclosed vulnerabilities, credentials, prompts, or private source code.
- Repository-local Git author identity uses the GitHub noreply address for `rkshadow`.

## License and Provenance

- The root Apache-2.0 `LICENSE`, `THIRD-PARTY-NOTICES`, MPL source-availability text, and all upstream copyright notices remain present.
- Every modified upstream source file receives a short, prominent AgentDesk modification notice to satisfy Apache-2.0 section 4(b).
- Desktop release inputs include an AgentDesk desktop third-party notice covering production npm packages and Windows App SDK redistribution notices.
- The notice generator is deterministic from lockfiles/package metadata and is verified by release-script tests.
- `.gitignore` blocks `.env*`, private keys, PFX/P12 certificates, signing keys, local credentials, build outputs, and visual brainstorming artifacts.
- Release documentation distinguishes unsigned CI MSIX packages from signed tag releases.

## GitHub Repository Settings

- Visibility: public.
- Issues, discussions, vulnerability reporting, and branch deletion after merge: enabled.
- Wiki: disabled; versioned documentation lives in the repository.
- Topics include `windows-11`, `winui3`, `dotnet`, `rust`, `acp`, `ai-agent`, and `chinese`.
- `main` is the default branch. Direct force pushes and branch deletion are blocked after the initial import.
- The first push contains source and documentation only. It does not claim a signed release or publish binaries without the required signing certificate and WSL payload.

## Publication Gate

The repository is not created or pushed until all of the following pass:

1. Secret and large-file scan reports no real credentials or unintended artifacts.
2. English/Chinese navigation links resolve.
3. Rust, Web, .NET, release-script, formatting, and whitespace checks pass.
4. License and modification notices are present in source and package inputs.
5. An independent code review reports no Critical or Important findings.
