# Release AgentDesk

[English](RELEASING.md) | [简体中文](RELEASING.zh-CN.md)

This is the maintainer checklist for the intended public repository, `rkshadow999/AgentDesk`. A release is complete only when the workflow succeeds for the tag and the published assets are independently inspected. Never turn a failed or unsigned CI artifact into a release manually.

## Current Release Evidence

The public source repository exists at [rkshadow999/AgentDesk](https://github.com/rkshadow999/AgentDesk). A signed public MSIX, published update channel, and release tag are not claimed until the exact tagged workflow and independent asset inspection succeed.

The latest Windows x64 evidence includes Web `245/245` plus a successful Web build; Core `37/37`, Engine `215/215`, App `627/627`, Windows platform `53/53`, Cloud Client `116/116`, real Kestrel `3/3`, Updater Core `114/114`, Provider smoke unit tests `8/8`, Process Job launcher `1/1`, and Cloud Server `37/37`; the desktop Release solution exited 0 and the application build reported zero warnings and zero errors. Rust proto/ACP contract/focused Marketplace regression/Memory/session-transfer are recorded at `4/4`, `12/12`, `2/2`, `292/292`, and `3/3`. The ACP contract result uses the required `--test-threads=1`; default parallel execution is not release evidence because these tests intentionally share a process-level `OnceLock`. The real-process Cloud database maintenance E2E, release-contract scripts, final x64 sidecar/8 MiB stack gate, and sanitized live Chat Completions and Responses provider smokes also passed without retaining response bodies or credentials. Local package inputs and unsigned MSIX artifacts remain development-only. ARM64 has project/workflow configuration but no recorded real-device launch for this source tree. `NativeProtected` must be released as **Native compatibility (not sandboxed)**, and `WslStrict` remains fail-closed without a usable result in a real WSL2 environment. Portable background checks default off, run periodically only after opt-in, and never apply an update automatically; worktree review uses a separate editable preparation step before the standard prompt/permission chain. The Cloud server, Runner workflow, and Windows Automation executor remain experimental and are not production release claims or equivalents of official Codex private services.

## One-Time Repository Settings

The repository must be public and use `main` as its default protected branch. Enable Issues, Discussions, automatic head-branch deletion, and GitHub Private Vulnerability Reporting; disable the unused wiki. Apply the project topics from the public-release plan.

Require pull requests, successful AgentDesk CI checks, resolved conversations, and protection from force pushes/deletion on `main`. Restrict tag/release creation to maintainers. Use a protected GitHub Environment for release signing when available and require reviewer approval.

Repository secrets required for tag packaging:

- `AGENTDESK_MSIX_PFX_BASE64`: Base64 of the code-signing PFX.
- `AGENTDESK_MSIX_PFX_PASSWORD`: PFX password.
- `AGENTDESK_UPDATE_ECDSA_PRIVATE_KEY_PKCS8_BASE64`: Base64 of an ECDSA P-256 private key in PKCS#8 DER form.

The repository-pinned MSIX Publisher is `CN=AgentDesk`; neither the package being verified nor its manifest may replace that trust value. The optional repository variable `AGENTDESK_MSIX_SIGNER_THUMBPRINT` pins the certificate used for the current tag. Once a signed release exists, the required `AGENTDESK_PREVIOUS_MSIX_SIGNER_THUMBPRINT` variable pins the certificate that signed the immediately previous release used for rollback. During certificate rotation, set the current pin to the new 40-hex SHA-1 thumbprint and keep the previous pin on the old certificate; after the new release is independently accepted, advance the previous pin for the next tag. Keep the PFX outside the repository and remove obsolete secrets only after the rollback window no longer depends on them. Branch and pull-request builds do not receive release signing secrets.

Generate and escrow the update signing key offline. The private key must exist only in a protected secret store or a short-lived file under the runner/system temporary directory; never commit it, place it in a workflow argument, or print its Base64 value. The reviewed current public key pin is committed at `desktop/update/AgentDesk-update-public-key.spki.base64`; its SPKI SHA-256 fingerprint is `a7350091fed6493ac0aa0d6222b4f2e0b80eb365c70fcf89d9040276e47b6e15`. The tag workflow proves that the private secret matches this repository pin before publishing either signature. Once a signed release exists, the required repository variable `AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256` must contain the 64-hex SHA-256 of the exact `AgentDesk-update-public-key.spki` asset from the immediately previous release. Rollback verification downloads that previous SPKI but trusts it only after its stable snapshot matches this independent pin.

Update-key rotation requires a reviewed client trust migration before publishing metadata signed only by the new key. Change the committed current pin and private-key secret to the new key while keeping `AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256` on the old release key for the first post-rotation tag. After that release and its rollback bundle are independently accepted, advance the previous-key variable to the SHA-256 of the newly published SPKI for the next tag.

## Version Rules

Tags use `v<version>`. Accepted versions are:

- Stable: `major.minor.patch`, for example `v0.1.0`.
- Prerelease: `ci`, `alpha`, `beta`, `preview`, or `rc` followed by a decimal number, for example `v0.2.0-alpha.1`.

Other suffixes, build metadata, four-part input versions, leading-zero sequence numbers, or sequences above the configured MSIX range are rejected by `Build-AgentDeskPackage.ps1`. Tags carry exactly one leading `v`, but direct `-Version` values passed to the package script must omit it. Stable builds map to MSIX revision `65535`, above prereleases of the same three-part version; release ordering is `ci < alpha < beta < preview < rc < stable`.

## Preflight

From a clean commit intended for `main`:

Run this preflight from Visual Studio 2022 Developer PowerShell with the Desktop development with C++ workload, MSVC v143, and Windows 11 SDK 10.0.26100 available. The repository selects .NET SDK 10.0.302 through `global.json` and Rust 1.92.0 through `rust-toolchain.toml`; install and export the repository-pinned `protoc` 29.3 before the Rust commands:

```powershell
$env:PROTOC = .\scripts\agentdesk\Install-Protoc.ps1 `
  -Version 29.3 `
  -Destination "$env:TEMP\agentdesk-protoc"

Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..

dotnet test .\desktop\AgentDesk.sln --configuration Release -m:1
dotnet format .\desktop\AgentDesk.sln --verify-no-changes --no-restore
dotnet format .\cloud\src\AgentDesk.Cloud\AgentDesk.Cloud.csproj --verify-no-changes --no-restore
dotnet format .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj --verify-no-changes --no-restore
dotnet test .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj --configuration Release
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskCloudDatabaseMaintenance.ps1

cargo fmt --package xai-proto-build --package xai-grok-pager-bin `
  --package xai-grok-sandbox --package xai-grok-shell -- --check
cargo test --locked -p xai-proto-build
cargo test --locked -p xai-grok-memory
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo test --locked -p xai-grok-shell --test agentdesk_session_transfer -- --test-threads=1
cargo check --locked -p xai-grok-pager --lib
cargo build --locked -p xai-grok-pager-bin --profile release-dist --features release-dist

pwsh -NoProfile -File .\scripts\agentdesk\Generate-AgentDeskThirdPartyNotices.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskUpdateManifest.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskRollbackBundle.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskReleaseScripts.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskPublicRepository.ps1
git diff --check
git status --short
```

The notice generator must leave no unexpected diff. Review every intended diff, dependency update, binary asset, upstream modification notice, and bilingual document. Confirm Windows Automation remains explicit, allow-once, and value-redacted; pairing file tests cover path substitution/atomic replacement; worktree review still requires an editable preparation step followed by the standard prompt/permission path; Portable background update checks still default off, run periodically only after opt-in, and never apply automatically; and remote Cloud profiles fail closed for all Plugin/Marketplace actions that can load code. Run packaged x64/ARM64, real-target Windows Automation, and accessibility/manual checks recorded in the roadmap; do not infer them from unit tests.

Confirm the repository contains no real API key, Cloud token, PFX, password, prompt, private source, username, or local path. Never use a live production credential in a release workflow. A separately authorized live-provider smoke may be run from a developer process using the environment-only procedure in [Build and Test](BUILD-AND-TEST.md#real-service-testing-rules); retain only its sanitized status/timing output.

`Build-AgentDeskPackage.ps1` publishes a self-contained, single-file updater for the selected RID to `package-input-<arch>/update-staging/AgentDesk.Updater.exe`. This directory is deliberately outside the replaceable `portable` application payload so a running updater is not moved with the application it replaces. Local unsigned package inputs contain `DEVELOPMENT-ONLY.txt` and must not be presented as a trusted update channel. Only `New-AgentDeskUpdateManifest.ps1`, with both trusted key inputs, creates publishable update metadata.

## Publish A Tag

Create the reviewed tag only after the public repository exists, authentication is available, branch protection is configured, and the exact commit is on protected `main`:

```powershell
git tag -a v0.1.0 -m "AgentDesk v0.1.0"
git push https://github.com/rkshadow999/AgentDesk.git v0.1.0
```

The tag workflow must complete all jobs:

1. Linux x64/ARM64 sidecar build, enforced sandbox tests, ELF/GLIBC check, SBOM, and checksum.
2. Optional Cloud server Release integration tests plus the Windows real-process offline backup/restore/rollback E2E job.
3. Windows x64/ARM64 Web/.NET/Rust/lifecycle tests and signed MSIX/Portable package inputs.
4. Per-architecture SPDX/CycloneDX over the complete package input (including the external updater), Portable zip, MSIX, signing status, and SHA-256 assembly.
5. Deterministic two-architecture update manifest generation, P-256 DER detached signing, and independent public-key verification.
6. Stage the immutable versioned GitHub Release as a draft, including cryptographic re-verification of both previous MSIX files against the independent previous-signer pin and verification of each previous Portable archive against the prior release's signed application update manifest and independently pinned previous SPKI before creating the rollback bundle.
7. Strictly advance the fixed signed feeds, re-resolve the remote lightweight or annotated tag to `GITHUB_SHA`, and only then publish the versioned draft.

If either MSIX signing secret, the update-signing private secret, or the current repository-pinned public key is unavailable or mismatched, the tag workflow fails rather than publishing unsigned release/update metadata. Once a previous signed release exists, either missing or mismatched previous-release trust pin also blocks publication. Do not bypass that gate.

## Fixed Update Feeds And Publication Order

`update-prerelease` is the fixed signed feed for every accepted tag. A stable tag also advances `update-stable`; stable releases therefore advance both fixed feeds. These fixed GitHub Releases are intentionally marked as prereleases so clients never depend on GitHub's stable-only `/releases/latest/` alias.

Tag builds may run in parallel, but publication is FIFO. Before the release job performs checkout or any publication side effect, it queries GitHub Actions for the same workflow and waits while any lower `run_number` push run whose remote Tag peels to that run's `headSha` remains incomplete. A same-named `v1.2.3` branch without that exact Tag/commit is not a blocker. An earlier Tag run that completed without success blocks later publication until it is rerun successfully or explicitly abandoned through a documented incident; after recording that decision and removing the abandoned Tag as appropriate, delete the failed Actions run with `gh run delete <database-id>` before retrying the later workflow. This preserves queued Tags instead of relying on GitHub concurrency's single replaceable pending slot. Push one reviewed release Tag at a time where practical; a four-hour wait timeout fails clearly and leaves the remainder of the six-hour release job budget for publication rather than silently reordering releases.

`workflow_dispatch` is validation-only even when a Tag ref is selected. Release signing credentials and the versioned Release/feed/final publication jobs are enabled only for a `push` event whose ref type is `tag`.

Before any `--clobber`, the workflow verifies the candidate application and updater manifests with the repository-pinned P-256 key. It then downloads every existing target feed, verifies both detached signatures, requires matching application/updater versions, and requires strict advancement under `ci < alpha < beta < preview < rc < stable`. Existing metadata may use the current key or the previous key independently pinned by `AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256`; an older or otherwise unpinned key fails closed and requires a reviewed key/feed migration.

The versioned Release remains `draft` while the fixed feeds advance. During the short interval between a feed upload and final publication, the signed manifest URLs can point at assets that unauthenticated clients cannot download from the draft. Update checks therefore fail closed temporarily instead of receiving unsigned or older bytes. The final job re-fetches the exact remote tag, peels either a lightweight or annotated tag to a commit, requires that commit to equal `GITHUB_SHA`, and then changes `draft=false`.

For retry recovery, use GitHub's **re-run failed jobs** action. If the feed job succeeded but final publication failed, re-run only the failed finalize job; a full workflow retry is intentionally rejected because the fixed feed already contains the same version. If a run is cancelled or fails after only some fixed feeds changed, stop automation, inspect the signed feed bytes and draft assets, and use a documented maintainer incident decision to finish or repair the release. Never bypass the same-version/downgrade guard with an ad hoc clobber.

## Inspect Published Assets

The GitHub Release must contain, for both `x64` and `arm64`:

- `AgentDesk-<version>-win-<arch>-portable.zip`
- `AgentDesk-<version>-win-<arch>-updater.zip` (contains exactly `AgentDesk.Updater.exe`)
- `AgentDesk-<version>-win-<arch>-UPDATE-STATUS.txt`
- `AgentDesk-<version>-win-<arch>.msix`
- `AgentDesk-<version>-win-<arch>.spdx.json`
- `AgentDesk-<version>-win-<arch>.cyclonedx.json`
- `AgentDesk-<version>-win-<arch>-MSIX-SIGNING-STATUS.txt`
- `AgentDesk-<version>-win-<arch>-SHA256SUMS.txt`
- The matching Linux sidecar archive and `.sha256`.

The release also contains shared update metadata:

- `AgentDesk-update-manifest.json`
- `AgentDesk-update-manifest.json.sig` (binary RFC 3279 DER ECDSA signature)
- `AgentDesk-updater-manifest.json`
- `AgentDesk-updater-manifest.json.sig` (binary RFC 3279 DER ECDSA signature)
- `AgentDesk-update-public-key.spki`
- `AgentDesk-update-metadata-SHA256SUMS.txt`

The first release also contains `AgentDesk-<version>-NO-PREVIOUS-ROLLBACK.txt`. Later releases contain `AgentDesk-<current>-rollback-to-<previous>.zip` and `.zip.sha256`. Rollback generation validates every entry produced by the previous architecture checksum, including the updater and update-status files, while the manual rollback archive contains only the Portable, MSIX, SBOM, signing-status, and original checksum assets. The Portable files are accepted only when their stable bytes match the previous release's ECDSA-signed application manifest and that manifest verifies with the previous SPKI named by `AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256`.

Download the published MSIX/Portable files again, verify each SHA-256, inspect `SOURCE-REVISION.txt` inside the package, verify MSIX Authenticode and Publisher on Windows, open both SBOM formats, and confirm license/source notices are present. Smoke-test installation and launch on real x64 and ARM64 Windows 11 machines. Until both checks are recorded for the exact release, do not describe ARM64 as verified.

Independently verify the exact bytes of both manifests with their detached signatures and the expected pinned P-256 SPKI key. Confirm each manifest contains exactly one `x64` and one `arm64` asset, in that order. The application manifest URL, byte size, lowercase SHA-256, and `AgentDesk.App.exe` entry point must match each downloaded portable archive. The updater manifest must likewise match each updater zip and declare `AgentDesk.Updater.exe` as its entry point; staging must extract that file before launch. The public key copied beside the release is an inspection aid, not a replacement for the client-pinned trust root.

Check generated release notes for private issue references, unsupported claims, or upstream branding before announcing the release.

## Failure And Recovery

- A missing architecture, previous MSIX signer pin, previous update-public-key pin, signed previous application manifest, invalid update/MSIX signature, mismatched update key or Publisher, stale notice, failed test, missing updater/SBOM/checksum, wrong source revision, or an out-of-order/backfill tag at or below an existing release blocks publication.
- A fixed feed with an invalid signature, mismatched app/updater version, same version, downgrade, or unpinned signing key blocks publication while the versioned Release remains draft.
- Delete or mark a broken GitHub Release/tag only through a documented maintainer incident decision; do not silently replace assets under an announced tag.
- Revoke exposed signing/cloud credentials immediately and use GitHub Private Vulnerability Reporting for coordinated response.
- Keep the previous signed release available. Test the new rollback bundle before announcement and follow [Installation](INSTALLATION.md) for manual downgrade limitations.

GitHub Release assets are mutable by maintainers, so checksums alone are not a transparency log. Protected release environments and future artifact attestations remain recommended supply-chain improvements in the [threat model](AGENTDESK-THREAT-MODEL.md).
