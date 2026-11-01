[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [string]$OutputRoot,
    [switch]$ForceRebuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo "artifacts/filament-prepared/$RuntimeIdentifier" }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
. (Join-Path $PSScriptRoot 'FilamentToolchain.ps1')
Import-Module (Join-Path $PSScriptRoot 'FilamentArtifactCache.psm1') -Force
$toolchain = Get-FilamentWindowsToolchain -RuntimeIdentifier $RuntimeIdentifier -OutputRoot $OutputRoot
$result = Invoke-FilamentRuntimePreparation -RepoRoot $repo -RuntimeIdentifier $RuntimeIdentifier -OutputRoot $OutputRoot -ToolchainIdentity $toolchain.Identity -ForceRebuild:$ForceRebuild
$outputs = [ordered]@{
    'runtime-root' = $result.RuntimeRoot
    'cache-key' = $result.CacheKey
    'cache-status' = $result.CacheStatus
    'cache-directory' = $result.CacheDirectory
    'cache-publish-eligible' = $result.CachePublishEligible.ToString().ToLowerInvariant()
    'cache-retention-days' = $result.CacheRetentionDays
}
foreach ($entry in $outputs.GetEnumerator()) {
    if ([string]$entry.Value -match '[\r\n]') { throw 'Invalid newline in Filament preparation output.' }
    if ($env:GITHUB_OUTPUT) { "$($entry.Key)=$($entry.Value)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8NoBOM }
}
$receiptPath = Join-Path $OutputRoot 'preparation-receipt.json'
$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
if ($env:GITHUB_STEP_SUMMARY) {
    $summary = @(
        '### Windows Filament runtime', '',
        "- RID: ``$RuntimeIdentifier``",
        "- Cache: **$($result.CacheStatus)**; fingerprint: ``$($result.Fingerprint)``",
        "- Artifact source: $($result.SourceArtifactId); run: $($result.SourceRunId)",
        "- Restore decision: $($result.RestoreReason.Replace("`r", ' ').Replace("`n", ' '))",
        "- Native cache retention: $($result.CacheRetentionDays) days; publication allowed: $($result.CachePublishEligible)", '',
        'Toolchain:', '```json', ($result.ToolchainIdentity | ConvertTo-Json -Depth 20), '```', ''
    )
    $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8NoBOM
}
Write-Host "Filament runtime $($result.CacheStatus): $($result.RuntimeRoot)"
# A recovered cache operation must not leak a stale native exit code to pwsh Actions.
$global:LASTEXITCODE = 0
