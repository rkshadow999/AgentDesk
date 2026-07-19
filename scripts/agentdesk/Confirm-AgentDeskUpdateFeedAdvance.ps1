<#
.SYNOPSIS
Verifies signed AgentDesk update metadata and requires a fixed feed to advance.

.DESCRIPTION
Authenticates both application and updater manifests with the trusted P-256 key,
checks their shared AgentDesk release version, and rejects same-version or
downgrade replacement of an existing fixed feed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$ExpectedCandidateVersion,
    [Parameter(Mandatory)][string]$TrustedPublicKeyPath,
    [string]$PreviousTrustedPublicKeyPath,
    [string]$ExistingDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-AgentDeskReleaseVersion {
    param([Parameter(Mandatory)][string]$Version)

    $match = [System.Text.RegularExpressions.Regex]::Match(
        $Version,
        '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(ci|alpha|beta|preview|rc)\.(0|[1-9]\d*))?$')
    if (-not $match.Success) {
        throw "The signed update feed contains an unsupported AgentDesk release version: $Version"
    }

    $components = foreach ($groupIndex in 1..3) {
        [uint64]$component = 0
        if (-not [uint64]::TryParse($match.Groups[$groupIndex].Value, [ref]$component) -or
            $component -gt 65535) {
            throw "The signed update feed version exceeds the supported release range: $Version"
        }
        [int]$component
    }

    $revision = 65535
    if ($match.Groups[4].Success) {
        [uint64]$sequence = 0
        if (-not [uint64]::TryParse($match.Groups[5].Value, [ref]$sequence) -or
            $sequence -gt 9999) {
            throw "The signed update feed prerelease sequence exceeds the supported range: $Version"
        }
        $channelOffsets = @{
            ci = 0
            alpha = 10000
            beta = 20000
            preview = 30000
            rc = 40000
        }
        $revision = $channelOffsets[$match.Groups[4].Value] + [int]$sequence
    }

    return [Version]::new($components[0], $components[1], $components[2], $revision)
}

function Read-BoundedFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description,
        [long]$MinimumBytes = 1,
        [Parameter(Mandatory)][long]$MaximumBytes
    )

    $file = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $file -or
        $file.PSIsContainer -or
        $file.Length -lt $MinimumBytes -or
        $file.Length -gt $MaximumBytes -or
        ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The $Description is missing or invalid."
    }
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -ne $file.Length) {
        [System.Array]::Clear($bytes, 0, $bytes.Length)
        throw "The $Description changed while it was being read."
    }
    return ,$bytes
}

function Assert-ManifestAssetList {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Assets,
        [Parameter(Mandatory)][string]$ExpectedEntryPoint,
        [Parameter(Mandatory)][string]$ExpectedProduct
    )

    if ($Assets.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or
        $Assets.GetArrayLength() -ne 2) {
        throw "The $ExpectedProduct update manifest asset list is invalid."
    }

    $trustedHosts = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($hostName in @(
        "github.com",
        "api.github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    )) {
        [void]$trustedHosts.Add($hostName)
    }
    $expectedArchitectures = @("x64", "arm64")
    $assetIndex = 0
    foreach ($asset in $Assets.EnumerateArray()) {
        if ($asset.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw "The $ExpectedProduct update manifest asset structure is invalid."
        }
        $expectedProperties = @("architecture", "url", "sha256", "size", "entryPoint")
        $seen = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        foreach ($property in $asset.EnumerateObject()) {
            if ($property.Name -notin $expectedProperties -or -not $seen.Add($property.Name)) {
                throw "The $ExpectedProduct update manifest asset structure is invalid."
            }
        }
        if ($seen.Count -ne $expectedProperties.Count -or
            $asset.GetProperty("architecture").GetString() -cne $expectedArchitectures[$assetIndex]) {
            throw "The $ExpectedProduct update manifest architecture set is invalid."
        }

        $uri = $null
        $uriText = $asset.GetProperty("url").GetString()
        if (-not [System.Uri]::TryCreate(
                $uriText,
                [System.UriKind]::Absolute,
                [ref]$uri) -or
            $uri.Scheme -cne [System.Uri]::UriSchemeHttps -or
            -not $uri.IsDefaultPort -or
            -not [string]::IsNullOrEmpty($uri.UserInfo) -or
            -not [string]::IsNullOrEmpty($uri.Query) -or
            -not [string]::IsNullOrEmpty($uri.Fragment) -or
            -not $trustedHosts.Contains($uri.IdnHost.TrimEnd('.'))) {
            throw "The $ExpectedProduct update manifest asset URI is invalid."
        }

        $sha256 = $asset.GetProperty("sha256").GetString()
        $size = $asset.GetProperty("size").GetInt64()
        $entryPoint = $asset.GetProperty("entryPoint").GetString()
        if ($sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $size -le 0 -or
            $size -gt 512L * 1024 * 1024 -or
            $entryPoint -cne $ExpectedEntryPoint) {
            throw "The $ExpectedProduct update manifest asset metadata is invalid."
        }
        $assetIndex++
    }
}

function Read-VerifiedManifestVersion {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$ManifestName,
        [Parameter(Mandatory)][string]$ExpectedProduct,
        [Parameter(Mandatory)][System.Security.Cryptography.ECDsa]$Verifier
    )

    $manifestPath = Join-Path $Directory $ManifestName
    $signaturePath = "$manifestPath.sig"
    $manifestBytes = Read-BoundedFile `
        -Path $manifestPath `
        -Description "$ExpectedProduct manifest" `
        -MaximumBytes (64 * 1024)
    $signatureBytes = Read-BoundedFile `
        -Path $signaturePath `
        -Description "$ExpectedProduct manifest signature" `
        -MinimumBytes 8 `
        -MaximumBytes 128
    try {
        $valid = $Verifier.VerifyData(
            $manifestBytes,
            $signatureBytes,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
    }
    catch [System.Security.Cryptography.CryptographicException] {
        $valid = $false
    }
    if (-not $valid) {
        throw "The $ExpectedProduct update manifest signature is invalid."
    }

    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    [void]$strictUtf8.GetString($manifestBytes)
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
    $options.MaxDepth = 8
    $document = [System.Text.Json.JsonDocument]::Parse(
        [System.ReadOnlyMemory[byte]]::new($manifestBytes),
        $options)
    try {
        $root = $document.RootElement
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw "The $ExpectedProduct update manifest structure is invalid."
        }
        $expectedProperties = @("schemaVersion", "product", "version", "assets")
        $seen = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        foreach ($property in $root.EnumerateObject()) {
            if ($property.Name -notin $expectedProperties -or -not $seen.Add($property.Name)) {
                throw "The $ExpectedProduct update manifest structure is invalid."
            }
        }
        if ($seen.Count -ne $expectedProperties.Count -or
            $root.GetProperty("schemaVersion").GetInt32() -ne 1 -or
            $root.GetProperty("product").GetString() -cne $ExpectedProduct) {
            throw "The $ExpectedProduct update manifest identity is invalid."
        }
        $expectedEntryPoint = if ($ExpectedProduct -ceq "AgentDesk") {
            "AgentDesk.App.exe"
        }
        else {
            "AgentDesk.Updater.exe"
        }
        Assert-ManifestAssetList `
            -Assets $root.GetProperty("assets") `
            -ExpectedEntryPoint $expectedEntryPoint `
            -ExpectedProduct $ExpectedProduct
        $version = $root.GetProperty("version").GetString()
        if ([string]::IsNullOrWhiteSpace($version)) {
            throw "The $ExpectedProduct update manifest version is invalid."
        }
        [pscustomobject]@{
            Text = $version
            Comparable = ConvertTo-AgentDeskReleaseVersion -Version $version
        }
    }
    finally {
        $document.Dispose()
    }
}

function Read-VerifiedFeedVersion {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][System.Security.Cryptography.ECDsa]$Verifier
    )

    $application = Read-VerifiedManifestVersion `
        -Directory $Directory `
        -ManifestName "AgentDesk-update-manifest.json" `
        -ExpectedProduct "AgentDesk" `
        -Verifier $Verifier
    $updater = Read-VerifiedManifestVersion `
        -Directory $Directory `
        -ManifestName "AgentDesk-updater-manifest.json" `
        -ExpectedProduct "AgentDesk.Updater" `
        -Verifier $Verifier
    if ($application.Text -cne $updater.Text) {
        throw "The signed application and updater feed versions do not match."
    }
    return $application
}

function Import-TrustedPublicKey {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    $publicKeyBytes = Read-BoundedFile `
        -Path $Path `
        -Description $Description `
        -MinimumBytes 32 `
        -MaximumBytes 1024
    $verifier = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $bytesRead = 0
        $verifier.ImportSubjectPublicKeyInfo($publicKeyBytes, [ref]$bytesRead)
        $parameters = $verifier.ExportParameters($false)
        if ($bytesRead -ne $publicKeyBytes.Length -or
            $verifier.KeySize -ne 256 -or
            $parameters.Curve.Oid.Value -ne "1.2.840.10045.3.1.7") {
            throw "The $Description must be an exact ECDSA P-256 SPKI."
        }
        return $verifier
    }
    catch {
        $verifier.Dispose()
        throw
    }
    finally {
        [System.Array]::Clear($publicKeyBytes, 0, $publicKeyBytes.Length)
    }
}

$verifier = Import-TrustedPublicKey `
    -Path $TrustedPublicKeyPath `
    -Description "trusted update public key"
$previousVerifier = $null
try {
    $candidate = Read-VerifiedFeedVersion `
        -Directory $CandidateDirectory `
        -Verifier $verifier
    if ($candidate.Text -cne $ExpectedCandidateVersion) {
        throw "The signed candidate feed version does not match the release tag."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExistingDirectory)) {
        try {
            $existing = Read-VerifiedFeedVersion `
                -Directory $ExistingDirectory `
                -Verifier $verifier
        }
        catch {
            if ([string]::IsNullOrWhiteSpace($PreviousTrustedPublicKeyPath)) {
                throw
            }
            $previousVerifier = Import-TrustedPublicKey `
                -Path $PreviousTrustedPublicKeyPath `
                -Description "previous trusted update public key"
            $existing = Read-VerifiedFeedVersion `
                -Directory $ExistingDirectory `
                -Verifier $previousVerifier
        }
        if ($candidate.Comparable -le $existing.Comparable) {
            throw "The fixed update feed must strictly advance beyond $($existing.Text); refusing same-version or downgrade publication."
        }
    }

    Write-Output $candidate.Text
}
finally {
    if ($null -ne $previousVerifier) {
        $previousVerifier.Dispose()
    }
    $verifier.Dispose()
}
