# AgentDesk Public Repository Implementation Plan

[English](2026-07-17-agentdesk-public-release.md) | [简体中文](2026-07-17-agentdesk-public-release.zh-CN.md)

> **For AI agent workers:** Required subskill: use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task by task. Track progress with the checkboxes below.

**Goal:** Publish the verified AgentDesk Alpha as a compliant, bilingual public repository at `rkshadow999/AgentDesk`.

**Architecture:** Keep the complete `xai-org/grok-build` history and licenses, replace upstream product identity only at AgentDesk-owned entry points, generate desktop dependency notices from source metadata, and enforce the publication contract with a PowerShell validator in CI. The upstream remote becomes fetch-only and the community repository becomes `origin`.

**Tech Stack:** Git, GitHub CLI, PowerShell 7, Markdown, npm lockfiles, .NET 10, Rust 1.92, GitHub Actions.

---

### Task 1: Add a fail-closed public repository validator

**Files:**
- Create: `scripts/agentdesk/Test-AgentDeskPublicRepository.ps1`
- Modify: `.github/workflows/agentdesk-windows.yml`
- Test: `scripts/agentdesk/Test-AgentDeskPublicRepository.ps1`

- [ ] **Step 1: Write the validator before the required files exist**

The script must enumerate required English/Chinese pairs, reject upstream branding in the root README, verify security and contribution URLs, verify secret ignore rules, and ensure every tracked modified upstream Rust file contains the AgentDesk modification notice.

```powershell
$requiredPairs = @(
    @("README.md", "README.zh-CN.md"),
    @("CONTRIBUTING.md", "CONTRIBUTING.zh-CN.md"),
    @("SECURITY.md", "SECURITY.zh-CN.md"),
    @("CODE_OF_CONDUCT.md", "CODE_OF_CONDUCT.zh-CN.md"),
    @("desktop/README.md", "desktop/README.zh-CN.md"),
    @("docs/ROADMAP.md", "docs/ROADMAP.zh-CN.md"),
    @("docs/UPSTREAM.md", "docs/UPSTREAM.zh-CN.md")
)
foreach ($pair in $requiredPairs) {
    foreach ($path in $pair) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $path) -PathType Leaf)) {
            throw "Required public document is missing: $path"
        }
    }
}
```

- [ ] **Step 2: Run the validator and confirm the expected red state**

Run: `pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1`

Expected: FAIL because `README.zh-CN.md` and the other new public documents do not exist yet.

- [ ] **Step 3: Wire the validator into both Linux and Windows CI jobs**

```yaml
- name: Validate public repository contract
  shell: pwsh
  run: ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
```

- [ ] **Step 4: Commit the red publication contract**

```powershell
git add scripts/agentdesk/Test-AgentDeskPublicRepository.ps1 .github/workflows/agentdesk-windows.yml
git commit -m "test: define AgentDesk public repository contract"
```

### Task 2: Replace upstream product identity with bilingual community entry points

**Files:**
- Modify: `README.md`
- Create: `README.zh-CN.md`
- Modify: `CONTRIBUTING.md`
- Create: `CONTRIBUTING.zh-CN.md`
- Modify: `SECURITY.md`
- Create: `SECURITY.zh-CN.md`
- Create: `CODE_OF_CONDUCT.md`
- Create: `CODE_OF_CONDUCT.zh-CN.md`

- [ ] **Step 1: Write the English root README with an explicit independence statement**

```markdown
# AgentDesk

[English](README.md) | [简体中文](README.zh-CN.md)

AgentDesk is an independent, community-maintained Windows 11 desktop client
built on the open-source `xai-org/grok-build` runtime at commit `c68e39f`.
It is not affiliated with or endorsed by xAI, SpaceXAI, OpenAI, or Codex.
```

The README must show the current Alpha screenshot, supported architectures, current features, exact build entry point, NativeProtected warning, WslStrict fail-closed status, roadmap links, and license/provenance links. It must not reuse upstream logos or official installation commands.

- [ ] **Step 2: Write the complete Chinese counterpart**

Use the same section order and link only to Chinese community documents.

- [ ] **Step 3: Replace contribution and security policies**

`CONTRIBUTING.md` must accept issues and pull requests, require focused tests, preserve upstream notices, and prohibit generated/binary churn. `SECURITY.md` must direct confidential reports to:

```text
https://github.com/rkshadow999/AgentDesk/security/advisories/new
```

- [ ] **Step 4: Add Contributor Covenant 2.1 in both languages**

Keep the standard English text and a faithful Chinese translation. Enforcement uses repository moderation and private security reporting, not an upstream corporate contact.

- [ ] **Step 5: Run the validator**

Run: `pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1`

Expected: still FAIL on desktop documentation, notices, or modification headers, but no longer on root community documents.

- [ ] **Step 6: Commit community entry points**

```powershell
git add README*.md CONTRIBUTING*.md SECURITY*.md CODE_OF_CONDUCT*.md
git commit -m "docs: establish bilingual AgentDesk community"
```

### Task 3: Publish bilingual desktop, roadmap, and upstream documentation

**Files:**
- Move content: `desktop/README.md` -> `desktop/README.zh-CN.md`
- Create: `desktop/README.md`
- Create: `desktop/THIRD-PARTY-SOURCE-NOTICE.zh-CN.md`
- Create: `docs/ROADMAP.md`
- Create: `docs/ROADMAP.zh-CN.md`
- Create: `docs/UPSTREAM.md`
- Create: `docs/UPSTREAM.zh-CN.md`

- [ ] **Step 1: Make the desktop README English by default**

The first screenful must state:

```markdown
> [!WARNING]
> AgentDesk Alpha currently exposes only NativeProtected, which is not a kernel sandbox.
> WslStrict intentionally fails closed until complete child-network enforcement can be attested.
```

- [ ] **Step 2: Preserve and update the Chinese desktop guide**

Move the existing Chinese content to `desktop/README.zh-CN.md`, add the same early warning, and keep commands synchronized with the English guide.

- [ ] **Step 3: Add roadmap and upstream synchronization policy**

`docs/ROADMAP*` separates Alpha, Beta, advanced agent workflows, and optional self-hosted cloud. `docs/UPSTREAM*` records base commit `c68e39f`, the `upstream` remote policy, merge procedure, conflict review, and change-notice requirements.

- [ ] **Step 4: Add a Chinese explanatory MPL/source notice**

State prominently that the English notice and actual license texts control in case of conflict.

- [ ] **Step 5: Run Markdown link and publication checks**

Run:

```powershell
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
git diff --check
```

Expected: publication validator now advances to dependency notices or source modification notices.

- [ ] **Step 6: Commit the documentation set**

```powershell
git add desktop/*.md docs/ROADMAP*.md docs/UPSTREAM*.md
git commit -m "docs: add bilingual desktop and roadmap guides"
```

### Task 4: Generate and package desktop third-party notices

**Files:**
- Create: `scripts/agentdesk/Generate-AgentDeskThirdPartyNotices.ps1`
- Create: `desktop/THIRD-PARTY-NOTICES.md`
- Modify: `scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1`
- Modify: `scripts/agentdesk/Build-AgentDeskPackage.ps1`
- Modify: `scripts/agentdesk/AgentDesk.Packaging.targets`
- Modify: `.github/workflows/agentdesk-windows.yml`

- [ ] **Step 1: Add failing release-script assertions**

```powershell
$desktopNotice = Join-Path $repositoryRoot "desktop/THIRD-PARTY-NOTICES.md"
if (-not (Test-Path -LiteralPath $desktopNotice -PathType Leaf)) {
    throw "Desktop third-party notice is missing."
}
foreach ($dependency in @("react", "monaco-editor", "@xterm/xterm", "Microsoft.WindowsAppSDK")) {
    if (-not (Select-String -LiteralPath $desktopNotice -SimpleMatch $dependency -Quiet)) {
        throw "Desktop notice is missing $dependency."
    }
}
```

- [ ] **Step 2: Run the release tests and confirm red**

Run: `pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1`

Expected: FAIL with `Desktop third-party notice is missing.`

- [ ] **Step 3: Implement deterministic notice generation**

The generator reads `desktop/web/package-lock.json`, resolves production package license metadata and license files under `node_modules`, adds Windows App SDK redistribution notice/source references, sorts entries by package name, and writes UTF-8 without BOM. It must fail when a production package has no identifiable license.

- [ ] **Step 4: Include the notice in Portable and MSIX inputs**

Add `AgentDeskDesktopNoticesPath` to packaging targets and copy `desktop/THIRD-PARTY-NOTICES.md` next to the root notices. Include both English and Chinese source-availability notices.

- [ ] **Step 5: Run red-green verification**

Run:

```powershell
pwsh ./scripts/agentdesk/Generate-AgentDeskThirdPartyNotices.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1
```

Expected: PASS and a deterministic notice file with no diff on a second generator run.

- [ ] **Step 6: Commit notices and packaging changes**

```powershell
git add desktop/THIRD-PARTY-NOTICES.md scripts/agentdesk .github/workflows/agentdesk-windows.yml
git commit -m "build: package desktop third-party notices"
```

### Task 5: Add source modification notices and secret guards

**Files:**
- Modify: `.gitignore`
- Modify: every tracked upstream Rust source file changed from `c68e39f`
- Test: `scripts/agentdesk/Test-AgentDeskPublicRepository.ps1`

- [ ] **Step 1: Add failing validator coverage for secret files and change notices**

Required ignore patterns:

```gitignore
.env
.env.*
!.env.example
*.pfx
*.p12
*.pem
*.key
*.snk
.superpowers/
```

The validator obtains modified tracked Rust files with `git diff --name-only c68e39f -- '*.rs'` and requires this exact leading notice:

```rust
// Modified by the AgentDesk project for Windows desktop integration and safety support.
```

- [ ] **Step 2: Run the validator and confirm red**

Expected: FAIL listing the modified Rust files without the notice.

- [ ] **Step 3: Add the notice without changing code behavior**

Place the notice before module documentation/imports in every modified upstream Rust file. New AgentDesk-only files do not need an upstream modification notice.

- [ ] **Step 4: Run formatting and publication checks**

Run:

```powershell
cargo fmt --package xai-proto-build --package xai-grok-pager-bin --package xai-grok-sandbox --package xai-grok-shell -- --check
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
git diff --check
```

Expected: PASS.

- [ ] **Step 5: Commit compliance guards**

```powershell
git add .gitignore crates/build/xai-proto-build crates/codegen/xai-grok-pager-bin crates/codegen/xai-grok-sandbox crates/codegen/xai-grok-shell
git commit -m "chore: record AgentDesk source modifications"
```

### Task 6: Verify and commit the complete Alpha implementation

**Files:**
- Verify all AgentDesk Alpha source, tests, CI, scripts, and documentation

- [ ] **Step 1: Run Web tests and production build serially**

```powershell
Set-Location desktop/web
npm test
npm run build
Set-Location ../..
```

Expected: all Vitest files pass and Vite exits 0.

- [ ] **Step 2: Run .NET tests and formatting after Web build**

```powershell
dotnet test desktop/AgentDesk.sln --no-restore
dotnet format desktop/AgentDesk.sln --verify-no-changes --no-restore
```

Expected: all Core, Engine, Platform, and App tests pass; format exits 0.

- [ ] **Step 3: Run focused Rust and release verification**

```powershell
cargo test --locked -p xai-proto-build
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
git diff --check
```

Expected: all commands exit 0. Enforced Linux sandbox E2E runs in the configured Linux/Docker environment with `SANDBOX_E2E_REQUIRE_ENFORCEMENT=1`.

- [ ] **Step 4: Request independent review and resolve all Critical/Important findings**

- [ ] **Step 5: Commit the remaining Alpha implementation**

```powershell
git add -A
git commit -m "feat: publish AgentDesk Windows alpha"
```

### Task 7: Create and configure the public GitHub repository

**Files:**
- Git remotes and GitHub repository settings only

- [ ] **Step 1: Install and authenticate GitHub CLI**

```powershell
winget install --id GitHub.cli --exact --silent --accept-package-agreements --accept-source-agreements
gh auth login --hostname github.com --git-protocol https --web
gh auth status
```

Expected: authenticated as `rkshadow` with `repo` and workflow access.

- [ ] **Step 2: Create the empty public repository**

```powershell
gh repo create rkshadow999/AgentDesk --public --description "Independent bilingual Windows 11 desktop client for the grok-build agent runtime"
```

- [ ] **Step 3: Rewire remotes safely**

```powershell
git remote rename origin upstream
git remote set-url --push upstream DISABLED
git remote add origin https://github.com/rkshadow999/AgentDesk.git
git branch -M main
git push -u origin main
```

- [ ] **Step 4: Configure community repository features**

```powershell
gh repo edit rkshadow999/AgentDesk --enable-issues --enable-discussions --enable-delete-branch --disable-wiki
gh api --method PUT repos/rkshadow999/AgentDesk/private-vulnerability-reporting
gh repo edit rkshadow999/AgentDesk --add-topic windows-11,winui3,dotnet,rust,acp,ai-agent,chinese
```

- [ ] **Step 5: Protect main after the initial push**

Use the GitHub branch protection API to require pull requests and block force pushes while allowing the initial CI workflow to report status. Do not require an unavailable check name before the first workflow run exists.

- [ ] **Step 6: Verify the public repository**

```powershell
gh repo view rkshadow999/AgentDesk --json nameWithOwner,isPrivate,defaultBranchRef,url
gh run list --repo rkshadow999/AgentDesk --limit 5
git remote -v
```

Expected: public repository, default branch `main`, new `origin`, fetch-only `upstream`, and the first CI run visible.
