<#
.SYNOPSIS
Runs focused regression tests for fixed AgentDesk update-feed advancement.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$validator = Join-Path $PSScriptRoot "Confirm-AgentDeskUpdateFeedAdvance.ps1"
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-update-feed-test-" + [guid]::NewGuid().ToString("N"))

function Write-SignedFeedMetadata {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][System.Security.Cryptography.ECDsa]$Key,
        [switch]$EmptyAssets,
        [switch]$UppercaseHash
    )

    [System.IO.Directory]::CreateDirectory($Directory) | Out-Null
    foreach ($manifest in @(
        @{ Name = "AgentDesk-update-manifest.json"; Product = "AgentDesk" },
        @{ Name = "AgentDesk-updater-manifest.json"; Product = "AgentDesk.Updater" }
    )) {
        $entryPoint = if ($manifest.Product -ceq "AgentDesk") {
            "AgentDesk.App.exe"
        }
        else {
            "AgentDesk.Updater.exe"
        }
        [object[]]$assets = @()
        if (-not $EmptyAssets) {
            $assets = @(
                [ordered]@{
                    architecture = "x64"
                    url = "https://github.com/example/AgentDesk/releases/download/v$Version/AgentDesk-$Version-win-x64.zip"
                    sha256 = if ($UppercaseHash) { "A" * 64 } else { "0" * 64 }
                    size = 1
                    entryPoint = $entryPoint
                },
                [ordered]@{
                    architecture = "arm64"
                    url = "https://github.com/example/AgentDesk/releases/download/v$Version/AgentDesk-$Version-win-arm64.zip"
                    sha256 = "1" * 64
                    size = 1
                    entryPoint = $entryPoint
                }
            )
        }
        $document = [ordered]@{
            schemaVersion = 1
            product = $manifest.Product
            version = $Version
            assets = $assets
        }
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
            ($document | ConvertTo-Json -Compress -Depth 4))
        $signature = $Key.SignData(
            $bytes,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
        [System.IO.File]::WriteAllBytes((Join-Path $Directory $manifest.Name), $bytes)
        [System.IO.File]::WriteAllBytes((Join-Path $Directory "$($manifest.Name).sig"), $signature)
    }
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$ExpectedMessage
    )

    try {
        & $Action
    }
    catch {
        if (-not $_.Exception.Message.Contains($ExpectedMessage)) {
            throw "Update-feed validation failed for the wrong reason: $($_.Exception.Message)"
        }
        return
    }
    throw "Expected update-feed validation to reject '$ExpectedMessage'."
}

$key = [System.Security.Cryptography.ECDsa]::Create(
    [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    [System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $publicKeyPath = Join-Path $fixtureRoot "trusted.spki"
    [System.IO.File]::WriteAllBytes($publicKeyPath, $key.ExportSubjectPublicKeyInfo())

    $existingCi = Join-Path $fixtureRoot "existing-ci"
    $candidateAlpha = Join-Path $fixtureRoot "candidate-alpha"
    Write-SignedFeedMetadata -Directory $existingCi -Version "1.0.0-ci.9999" -Key $key
    Write-SignedFeedMetadata -Directory $candidateAlpha -Version "1.0.0-alpha.0" -Key $key
    & $validator `
        -ExistingDirectory $existingCi `
        -CandidateDirectory $candidateAlpha `
        -ExpectedCandidateVersion "1.0.0-alpha.0" `
        -TrustedPublicKeyPath $publicKeyPath | Out-Null

    $emptyCandidate = Join-Path $fixtureRoot "empty-candidate"
    Write-SignedFeedMetadata `
        -Directory $emptyCandidate `
        -Version "1.0.0-alpha.1" `
        -Key $key `
        -EmptyAssets
    Assert-Rejected `
        -ExpectedMessage "asset list is invalid" `
        -Action {
            & $validator `
                -CandidateDirectory $emptyCandidate `
                -ExpectedCandidateVersion "1.0.0-alpha.1" `
                -TrustedPublicKeyPath $publicKeyPath | Out-Null
        }

    $uppercaseHashCandidate = Join-Path $fixtureRoot "uppercase-hash-candidate"
    Write-SignedFeedMetadata `
        -Directory $uppercaseHashCandidate `
        -Version "1.0.0-alpha.1" `
        -Key $key `
        -UppercaseHash
    Assert-Rejected `
        -ExpectedMessage "asset metadata is invalid" `
        -Action {
            & $validator `
                -CandidateDirectory $uppercaseHashCandidate `
                -ExpectedCandidateVersion "1.0.0-alpha.1" `
                -TrustedPublicKeyPath $publicKeyPath | Out-Null
        }

    Assert-Rejected `
        -ExpectedMessage "strictly advance" `
        -Action {
            & $validator `
                -ExistingDirectory $candidateAlpha `
                -CandidateDirectory $candidateAlpha `
                -ExpectedCandidateVersion "1.0.0-alpha.0" `
                -TrustedPublicKeyPath $publicKeyPath | Out-Null
        }

    $existingBeta = Join-Path $fixtureRoot "existing-beta"
    Write-SignedFeedMetadata -Directory $existingBeta -Version "1.0.0-beta.1" -Key $key
    Assert-Rejected `
        -ExpectedMessage "strictly advance" `
        -Action {
            & $validator `
                -ExistingDirectory $existingBeta `
                -CandidateDirectory $candidateAlpha `
                -ExpectedCandidateVersion "1.0.0-alpha.0" `
                -TrustedPublicKeyPath $publicKeyPath | Out-Null
        }

    $mismatchedExisting = Join-Path $fixtureRoot "mismatched-existing"
    Write-SignedFeedMetadata -Directory $mismatchedExisting -Version "1.0.0-ci.1" -Key $key
    $replacementUpdater = Join-Path $fixtureRoot "replacement-updater"
    Write-SignedFeedMetadata -Directory $replacementUpdater -Version "1.0.0-ci.2" -Key $key
    Copy-Item `
        -LiteralPath (Join-Path $replacementUpdater "AgentDesk-updater-manifest.json") `
        -Destination (Join-Path $mismatchedExisting "AgentDesk-updater-manifest.json") `
        -Force
    Copy-Item `
        -LiteralPath (Join-Path $replacementUpdater "AgentDesk-updater-manifest.json.sig") `
        -Destination (Join-Path $mismatchedExisting "AgentDesk-updater-manifest.json.sig") `
        -Force
    Assert-Rejected `
        -ExpectedMessage "versions do not match" `
        -Action {
            & $validator `
                -ExistingDirectory $mismatchedExisting `
                -CandidateDirectory $candidateAlpha `
                -ExpectedCandidateVersion "1.0.0-alpha.0" `
                -TrustedPublicKeyPath $publicKeyPath | Out-Null
        }

    $tamperedExisting = Join-Path $fixtureRoot "tampered-existing"
    Copy-Item -LiteralPath $existingCi -Destination $tamperedExisting -Recurse
    $signaturePath = Join-Path $tamperedExisting "AgentDesk-update-manifest.json.sig"
    $signature = [System.IO.File]::ReadAllBytes($signaturePath)
    $signature[0] = $signature[0] -bxor 0xff
    [System.IO.File]::WriteAllBytes($signaturePath, $signature)
    Assert-Rejected `
        -ExpectedMessage "signature is invalid" `
        -Action {
            & $validator `
                -ExistingDirectory $tamperedExisting `
                -CandidateDirectory $candidateAlpha `
                -ExpectedCandidateVersion "1.0.0-alpha.0" `
                -TrustedPublicKeyPath $publicKeyPath | Out-Null
        }

    $previousKey = [System.Security.Cryptography.ECDsa]::Create(
        [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $previousKeyPath = Join-Path $fixtureRoot "previous-trusted.spki"
        [System.IO.File]::WriteAllBytes(
            $previousKeyPath,
            $previousKey.ExportSubjectPublicKeyInfo())
        $previousKeyExisting = Join-Path $fixtureRoot "previous-key-existing"
        Write-SignedFeedMetadata `
            -Directory $previousKeyExisting `
            -Version "1.0.0-ci.9999" `
            -Key $previousKey
        & $validator `
            -ExistingDirectory $previousKeyExisting `
            -CandidateDirectory $candidateAlpha `
            -ExpectedCandidateVersion "1.0.0-alpha.0" `
            -TrustedPublicKeyPath $publicKeyPath `
            -PreviousTrustedPublicKeyPath $previousKeyPath | Out-Null
    }
    finally {
        $previousKey.Dispose()
    }
}
finally {
    $key.Dispose()
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "AgentDesk update-feed advancement tests passed."
